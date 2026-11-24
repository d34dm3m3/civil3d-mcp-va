using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public sealed record PluginStatus(
  bool IsRunning,
  bool OperationInProgress,
  string? CurrentOperation,
  int QueueDepth,
  int QueueCapacity,
  long? CurrentOperationStartedAtUnixMs,
  string? CurrentRequestId);

public sealed class JsonRpcDispatchException : Exception
{
  public JsonRpcDispatchException(string code, string message) : base(message)
  {
    Code = code;
  }

  public string Code { get; }
}

public static class PluginRuntime
{
  public const int Port = 8080;

  private static readonly object Sync = new();
  private static RpcTcpServer? _server;
  private static readonly AsyncLocal<string?> CurrentRequestOperation = new();
  private static readonly AsyncLocal<string?> CurrentRequestId = new();
  private static readonly AsyncLocal<CancellationToken> CurrentRequestCancellation = new();
  private static readonly AsyncLocal<string?> CurrentExpectedDrawingIdentity = new();
  // C150: the drawing the caller says it writes to (params.expectDrawing: file name or full path), checked against the
  // active drawing when the call runs; a batch's value applies to every call in it.
  private static readonly AsyncLocal<string?> CurrentRequestedDrawing = new();
  private const int MaxQueuedHostOperations = 64;
  private static int _queueDepth;
  private static int _activeOperations;
  private static string? _currentOperation;
  private static string? _currentRequestId;
  private static long? _currentOperationStartedAtUnixMs;

  public static void StartServer()
  {
    lock (Sync)
    {
      if (_server != null)
      {
        return;
      }

      _server = new RpcTcpServer(Port, HandleRawRequestAsync);
      _server.Start();
    }
  }

  public static void StopServer()
  {
    lock (Sync)
    {
      _server?.Stop();
      _server = null;
      _currentOperation = null;
      _activeOperations = 0;
      _queueDepth = 0;
      _currentRequestId = null;
      _currentOperationStartedAtUnixMs = null;
    }
  }

  public static PluginStatus GetStatus()
  {
    lock (Sync)
    {
      return new PluginStatus(
        _server != null,
        _activeOperations > 0,
        _currentOperation,
        _queueDepth,
        MaxQueuedHostOperations,
        _currentOperationStartedAtUnixMs,
        _currentRequestId);
    }
  }

  public static async Task<string> HandleRawRequestAsync(string rawRequest, CancellationToken cancellationToken)
  {
    JsonNode? parsed;
    try
    {
      parsed = JsonNode.Parse(rawRequest);
    }
    catch (Exception ex)
    {
      return JsonRpcProtocol.SerializeError(null, -32700, "CIVIL3D.INVALID_JSON", $"Invalid JSON request: {ex.Message}");
    }

    if (parsed is not JsonObject request)
    {
      return JsonRpcProtocol.SerializeError(null, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must be an object.");
    }

    var id = request["id"]?.DeepClone();
    if (request["jsonrpc"] is not JsonValue versionValue
      || !versionValue.TryGetValue<string>(out var version)
      || version != "2.0")
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must specify jsonrpc='2.0'.");
    }

    var method = request["method"] is JsonValue methodValue
      && methodValue.TryGetValue<string>(out var methodText)
      ? methodText
      : null;

    if (string.IsNullOrWhiteSpace(method))
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request is missing a string method.");
    }

    if (request["params"] != null && request["params"] is not JsonObject)
    {
      return JsonRpcProtocol.SerializeError(id, -32602, "CIVIL3D.INVALID_INPUT", "JSON-RPC params must be an object when provided.");
    }
    var parameters = request["params"] as JsonObject;

    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    var previousRequestedDrawing = CurrentRequestedDrawing.Value;
    CurrentRequestOperation.Value = method;
    CurrentRequestId.Value = id?.ToJsonString();
    CurrentRequestCancellation.Value = cancellationToken;
    if (parameters?["expectDrawing"] is JsonValue expectValue && expectValue.TryGetValue<string>(out var expectText)
        && !string.IsNullOrWhiteSpace(expectText))
      CurrentRequestedDrawing.Value = expectText.Trim();

    var timer = System.Diagnostics.Stopwatch.StartNew();
    try
    {
      PluginLog.Debug("Dispatch", $"-> {method} [{CurrentRequestId.Value ?? "no-id"}]");
      var result = await CommandDispatcher.DispatchAsync(method, parameters, cancellationToken);
      PluginLog.Debug("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] ok durationMs={timer.ElapsedMilliseconds}");
      var answer = JsonRpcProtocol.SerializeResult(id, result);
      AnswerScope.Current?.MarkResult();                // C93: a step waiting for the answer runs only after a result
      return answer;
    }
    catch (JsonRpcDispatchException ex)
    {
      // Domain-level errors are part of the contract; record at info so they
      // show up in diagnostics without looking like runtime faults.
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] dispatch error {ex.Code} durationMs={timer.ElapsedMilliseconds}: {ex.Message}");
      return JsonRpcProtocol.SerializeError(id, JsonRpcProtocol.NumericErrorCode(ex.Code), ex.Code, ex.Message);
    }
    catch (OperationCanceledException)
    {
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] cancelled durationMs={timer.ElapsedMilliseconds}");
      return JsonRpcProtocol.SerializeError(id, -32010, "CIVIL3D.CANCELLED", $"Operation '{method}' was cancelled.");
    }
    catch (Exception ex)
    {
      PluginLog.Error("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] unhandled failure durationMs={timer.ElapsedMilliseconds} category={ex.GetType().Name}", ex);
      // C149: the exception's type and message go back to the caller (was a fixed text; the cause was only in the log).
      // C163: exceptions that are the caller's input or the drawing's state get that class, not INTERNAL_ERROR.
      var (code, number) = ClassifyUnhandled(ex);
      return JsonRpcProtocol.SerializeError(id, number, code, $"{method}: {(code == "CIVIL3D.INTERNAL_ERROR" ? "unexpected " : "")}{DescribeException(ex)}");
    }
    finally
    {
      CurrentRequestedDrawing.Value = previousRequestedDrawing;
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
    }
  }

  internal static CancellationToken GetCurrentRequestCancellationToken() => CurrentRequestCancellation.Value;

  internal static string GetCurrentRequestOperation() => CurrentRequestOperation.Value ?? "Civil 3D operation";

  internal static string? GetCurrentRequestId() => CurrentRequestId.Value;

  /// <summary>C149: "type: message" of an unhandled exception, with AutoCAD's ErrorStatus and the innermost cause.</summary>
  internal static string DescribeException(Exception ex)
  {
    var root = Civil3DCompatibility.UnwrapInvocation(ex);
    var text = $"{root.GetType().Name}: {root.Message}";
    if (root is Autodesk.AutoCAD.Runtime.Exception acad) text += $" (ErrorStatus {acad.ErrorStatus})";
    if (root.InnerException != null && root.InnerException.Message != root.Message)
      text += $" <- {root.InnerException.GetType().Name}: {root.InnerException.Message}";
    return text.Length > 600 ? text[..600] : text;
  }

  /// <summary>C150: the caller's expectDrawing with the operation name, read on the request's thread (the main thread
  /// running the call later does not carry the request's AsyncLocals).</summary>
  /// <summary>C163 (2026-10-21): the class of an exception a command did not handle. A parameter of the wrong JSON type
  /// (JsonNode GetValue) is invalid input; a name that exists already is a conflict; a missing referenced object (null
  /// object id, key not found) is "not found"; everything else stays an internal error. Found by the coverage sweep:
  /// createLayoutProfile with an existing name, setScale with a number, setMLeaderStyle with an unknown style.</summary>
  internal static (string Code, int Number) ClassifyUnhandled(Exception ex)
  {
    var root = Civil3DCompatibility.UnwrapInvocation(ex);
    var text = root.Message ?? "";
    if (root is InvalidOperationException && text.Contains("cannot be converted to", StringComparison.OrdinalIgnoreCase)
        || root is FormatException
        || root.GetType() == typeof(ArgumentException) && text.Contains("expected range", StringComparison.OrdinalIgnoreCase))   // E_INVALIDARG from Civil
      return ("CIVIL3D.INVALID_INPUT", -32602);
    if (text.Contains("already exist", StringComparison.OrdinalIgnoreCase)
        || root is Autodesk.AutoCAD.Runtime.Exception { ErrorStatus: Autodesk.AutoCAD.Runtime.ErrorStatus.DuplicateRecordName or Autodesk.AutoCAD.Runtime.ErrorStatus.DuplicateKey })
      return ("CIVIL3D.CONFLICT", -32009);
    if (root is KeyNotFoundException
        || root is Autodesk.AutoCAD.Runtime.Exception { ErrorStatus: Autodesk.AutoCAD.Runtime.ErrorStatus.NullObjectId or Autodesk.AutoCAD.Runtime.ErrorStatus.KeyNotFound })
      return ("CIVIL3D.OBJECT_NOT_FOUND", -32004);
    return ("CIVIL3D.INTERNAL_ERROR", -32603);
  }

  /// <summary>C150: sets the expectDrawing of the calls that follow on this thread (a batch's calls); returns the previous.</summary>
  internal static string? SetRequestedDrawing(string? name)
  {
    var previous = CurrentRequestedDrawing.Value;
    CurrentRequestedDrawing.Value = name;
    return previous;
  }

  internal static (string? Name, string Operation) GetRequestedDrawing() => (CurrentRequestedDrawing.Value, GetCurrentRequestOperation());

  /// <summary>C150: refuses the call when the caller named a drawing (expectDrawing) and another one is active. The
  /// name matches the active drawing's full path, its file name (with or without .dwg) or the document name
  /// (Drawing1.dwg for a drawing never saved).</summary>
  internal static void CheckRequestedDrawing(Autodesk.AutoCAD.ApplicationServices.Document doc, (string? Name, string Operation) request)
  {
    var requested = request.Name;
    if (string.IsNullOrWhiteSpace(requested)) return;
    var identity = GetDrawingIdentity(doc) ?? doc.Name;
    var want = requested.Replace('/', Path.DirectorySeparatorChar).Trim();
    bool Same(string? have) => !string.IsNullOrWhiteSpace(have)
      && (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(have), want, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileNameWithoutExtension(have), want, StringComparison.OrdinalIgnoreCase));
    if (Same(identity) || Same(doc.Name)) return;
    throw new JsonRpcDispatchException("CIVIL3D.WRONG_DRAWING",
      $"{request.Operation}: expectDrawing is '{requested}' but the active drawing is '{identity}'. Nothing was changed.");
  }

  internal static string? GetActiveDrawingIdentity()
  {
    var document = App.DocumentManager.MdiActiveDocument;
    return GetDrawingIdentity(document);
  }

  internal static string? GetDrawingIdentity(Autodesk.AutoCAD.ApplicationServices.Document? document)
  {
    if (document == null) return null;
    // C93: a drawing never saved has no file of its own; its Database.Filename may name the template it came from, which
    // can be open beside it (company template + new project drawing), so the document name (Drawing1.dwg) identifies it.
    var fileName = document.IsNamedDrawing ? document.Database.Filename : null;
    return string.IsNullOrWhiteSpace(fileName) ? document.Name : fileName;
  }

  internal static string? GetExpectedDrawingIdentity() => CurrentExpectedDrawingIdentity.Value;

  internal static async Task<T> RunWithRequestContextAsync<T>(
    string operation,
    string requestId,
    CancellationToken cancellationToken,
    string? expectedDrawingIdentity,
    Func<Task<T>> action)
  {
    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    var previousExpectedDrawingIdentity = CurrentExpectedDrawingIdentity.Value;
    CurrentRequestOperation.Value = operation;
    CurrentRequestId.Value = requestId;
    CurrentRequestCancellation.Value = cancellationToken;
    CurrentExpectedDrawingIdentity.Value = expectedDrawingIdentity;
    try
    {
      return await action();
    }
    finally
    {
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
      CurrentExpectedDrawingIdentity.Value = previousExpectedDrawingIdentity;
    }
  }

  internal static void QueueHostOperation()
  {
    lock (Sync)
    {
      if (_queueDepth >= MaxQueuedHostOperations)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          $"Civil 3D host queue is full ({MaxQueuedHostOperations} operations). Retry after current work completes.");
      }

      _queueDepth++;
    }
  }

  internal static void StartHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
      _activeOperations++;
      _currentOperation = GetCurrentRequestOperation();
      _currentRequestId = GetCurrentRequestId();
      _currentOperationStartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
  }

  internal static void CancelQueuedHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
    }
  }

  internal static void CompleteHostOperation()
  {
    lock (Sync)
    {
      _activeOperations = Math.Max(0, _activeOperations - 1);
      if (_activeOperations == 0)
      {
        _currentOperation = null;
        _currentRequestId = null;
        _currentOperationStartedAtUnixMs = null;
      }
    }
  }

  public static object? GetParameter(JsonObject? parameters, string name)
  {
    if (parameters == null)
    {
      return null;
    }

    return parameters.TryGetPropertyValue(name, out var value) ? value : null;
  }

  public static string GetRequiredString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    var stringValue = value.GetValue<string>();
    if (string.IsNullOrWhiteSpace(stringValue))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be a non-empty string.");
    }

    return stringValue;
  }

  public static double GetRequiredDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return value.GetValue<double>();
  }

  public static int GetRequiredInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return value.GetValue<int>();
  }

  public static string? GetOptionalString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<string?>();
  }

  public static double? GetOptionalDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<double>();
  }

  public static int? GetOptionalInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<int>();
  }

  public static bool? GetOptionalBool(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<bool>();
  }

}
