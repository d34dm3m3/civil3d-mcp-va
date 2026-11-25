using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// C64 (2026-10-08): many plugin calls in one request and one command context, e.g. 140 PVIs, a column of texts and
/// lines, labels or attribute moves. One TCP round trip, one queue slot and one command-context start instead of
/// one per call (live about 0.1 s and one idle pass each). By default each call keeps its own transaction and
/// commits on its own, so the batch is not atomic: with stopOnError (default) the calls after a failing one are not
/// run, the ones before it stay. singleTransaction: true nests the calls in one outer transaction that commits once
/// if every call succeeded and is aborted otherwise (all or nothing); Civil 3D then updates the dependent profile
/// views once instead of once per call (live 2026-10-09: two profile view changes 11,0 s as separate transactions,
/// in a batch or not). Calls that run as their own AutoCAD command (plot, deferred writes), in the application context
/// (C93: newDrawing, document_open/_activate/_close; documents_list runs inline) or start another batch
/// are refused; saveDrawing runs, except in singleTransaction.
/// </summary>
public static class BatchCommands
{
  private const int MaxCalls = 2000;

  public static async Task<object?> RunAsync(JsonObject? parameters, CancellationToken cancellationToken)
  {
    if (PluginRuntime.GetParameter(parameters, "calls") is not JsonArray list || list.Count == 0)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "calls must list at least one {method, params}.");
    if (list.Count > MaxCalls)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"At most {MaxCalls} calls per batch ({list.Count} given).");
    var stopOnError = PluginRuntime.GetOptionalBool(parameters, "stopOnError") ?? true;
    var singleTransaction = PluginRuntime.GetOptionalBool(parameters, "singleTransaction") ?? false;
    // C210 (2026-11-24): save: true saves the drawing after the calls (after the commit with singleTransaction) when no
    // call failed, through the database as saveDrawing does inside a batch. Use case: every script ended with its own save
    // call; the template is saved this way (a COM save of a .dwt can open a dialog).
    var save = PluginRuntime.GetOptionalBool(parameters, "save") ?? false;
    if (singleTransaction) stopOnError = true;                 // all or nothing: stop at the first failure
    var calls = new List<(string Method, JsonObject? Params)>();
    foreach (var node in list)
    {
      if (node is not JsonObject o || o["method"]?.GetValue<string>() is not { Length: > 0 } method)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "Each call must be an object with a method name. Nothing was run.");
      if (string.Equals(method, "batch", StringComparison.OrdinalIgnoreCase))
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A batch cannot contain another batch. Nothing was run.");
      if (save && method is "saveDrawing")
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "save: true saves after the calls; leave saveDrawing out of calls. Nothing was run.");
      if (singleTransaction && method is "saveDrawing" or "newDrawing" or "undoDrawing" or "redoDrawing")
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{method} cannot run inside a single transaction (it would act before the batch commits). Nothing was run.");
      // C98c: the label expression collection is not opened in the transaction, so an aborted batch could not undo it.
      if (singleTransaction && method is "labelExpressionSet" or "labelExpressionDelete")
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{method} cannot run inside a single transaction (label expressions may be written outside it, so an aborted batch could not undo the change). Nothing was run.");
      calls.Add((method, o["params"]?.DeepClone() as JsonObject));
    }

    // C150: the batch's expectDrawing applies to every call; a call's own expectDrawing replaces it for that call. Read
    // here, on the request's thread: the batch runs on the main thread, which does not carry the request's AsyncLocals.
    var batchDrawing = PluginRuntime.GetRequestedDrawing().Name;
    return await CivilExecution.ExecuteBatchAsync<object?>(async () =>
    {
      var results = new List<Dictionary<string, object?>>();
      var failed = 0;
      var timer = System.Diagnostics.Stopwatch.StartNew();
      for (var i = 0; i < calls.Count; i++)
      {
        var (method, callParameters) = calls[i];
        cancellationToken.ThrowIfCancellationRequested();
        var own = callParameters?["expectDrawing"] is JsonValue ownValue && ownValue.TryGetValue<string>(out var ownText)
          && !string.IsNullOrWhiteSpace(ownText) ? ownText.Trim() : null;
        var previousDrawing = PluginRuntime.SetRequestedDrawing(own ?? batchDrawing);
        try
        {
          var result = await CommandDispatcher.DispatchAsync(method, callParameters, cancellationToken);
          results.Add(new Dictionary<string, object?> { ["index"] = i, ["method"] = method, ["ok"] = true, ["result"] = result });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          failed++;
          var code = ex is JsonRpcDispatchException d ? d.Code : PluginRuntime.ClassifyUnhandled(ex).Code;   // C163
          PluginLog.Info("Batch", $"call {i} {method} failed {code}: {ex.Message}");
          results.Add(new Dictionary<string, object?>
          {
            ["index"] = i, ["method"] = method, ["ok"] = false,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = ex is JsonRpcDispatchException ? ex.Message : PluginRuntime.DescribeException(ex) },   // C149
          });
          if (stopOnError) break;
        }
        finally
        {
          PluginRuntime.SetRequestedDrawing(previousDrawing);
        }
      }

      var commit = !singleTransaction || failed == 0;
      PluginLog.Info("Batch", $"{results.Count}/{calls.Count} calls run, {failed} failed, {timer.ElapsedMilliseconds} ms"
        + (singleTransaction ? (commit ? ", one transaction committed" : ", one transaction aborted") : ""));
      return ((object?)new Dictionary<string, object?>
      {
        ["singleTransaction"] = singleTransaction,
        ["committed"] = singleTransaction ? commit : null,
        ["calls"] = calls.Count,
        ["run"] = results.Count,
        ["failed"] = failed,
        ["notRun"] = calls.Count - results.Count,
        ["durationMs"] = timer.ElapsedMilliseconds,
        ["results"] = results,
        ["saved"] = null,
      }, commit);
    }, singleTransaction, save ? SaveAfterAsync : null);

    async Task SaveAfterAsync(object? value, bool commit)
    {
      if (value is not Dictionary<string, object?> answer) return;
      if (!commit || (int)answer["failed"]! > 0 || (int)answer["notRun"]! > 0)
      {
        answer["saved"] = new Dictionary<string, object?> { ["saved"] = false, ["reason"] = "a call failed or was not run; the drawing was not saved" };
        return;
      }
      var previousDrawing = PluginRuntime.SetRequestedDrawing(batchDrawing);
      try
      {
        answer["saved"] = await CommandDispatcher.DispatchAsync("saveDrawing", null, cancellationToken);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        answer["saved"] = new Dictionary<string, object?> { ["saved"] = false, ["error"] = ex is JsonRpcDispatchException ? ex.Message : PluginRuntime.DescribeException(ex) };
      }
      finally
      {
        PluginRuntime.SetRequestedDrawing(previousDrawing);
      }
    }
  }
}
