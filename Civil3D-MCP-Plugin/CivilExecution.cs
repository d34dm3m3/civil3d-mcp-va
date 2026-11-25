using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public static class CivilExecution
{
  private static readonly SemaphoreSlim HostExecutionGate = new(1, 1);

  private const int NotStarted = 0, Running = 1, Finished = 2, Abandoned = 3;
  private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(100);
  private static readonly TimeSpan ContinuationGrace = TimeSpan.FromSeconds(10);

  private sealed class ContextState
  {
    public int Value = NotStarted;
  }

  // C64 batch: calls dispatched inside one command context (BatchCommands) run inline on the main thread with
  // their own document lock and transaction, without the host gate (held by the batch) or a new command context.
  private static readonly AsyncLocal<int> BatchThread = new();

  internal static bool InBatch => BatchThread.Value != 0;

  /// <summary>
  /// C93c (live 2026-10-10): the active drawing and the Civil document of that very drawing, read at the moment the
  /// call runs. The Civil document comes from the drawing's database (CivilDocument.GetCivilDocument), not from
  /// CivilApplication.ActiveDocument, so it cannot belong to another open drawing. Should AutoCAD run the call in the
  /// context of another document than the active one (DocumentCollection.CurrentDocument), a write is refused before
  /// anything changes (layouts, system variables and the working database would still follow the other document) and a
  /// read is logged and works on the active drawing.
  /// </summary>
  internal static (Document Doc, CivilDocument CivilDoc) ActiveDrawing(bool write)
  {
    var manager = App.DocumentManager;
    var doc = manager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
    var context = manager.CurrentDocument;
    if (context != null && !DocumentCommands.Same(context, doc))
    {
      var operation = PluginRuntime.GetCurrentRequestOperation();
      PluginLog.Warn("Execution", $"{operation}: running in the context of '{context.Name}' while '{doc.Name}' is active.");
      if (write)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"{operation}: Civil 3D ran the call in the context of '{context.Name}' while '{doc.Name}' is active; retry once the switch has settled (documents_list). Nothing was changed.");
    }
    var civilDoc = CivilDocument.GetCivilDocument(doc.Database)
      ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", $"No Civil 3D document is available for '{doc.Name}'.");
    DocumentSnapshot.Refresh();                         // main thread: keeps civil3d_health current
    return (doc, civilDoc);
  }

  private static T RunInline<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    if (Environment.CurrentManagedThreadId != BatchThread.Value)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT",
        $"'{PluginRuntime.GetCurrentRequestOperation()}' left the main thread inside a batch; call it on its own. Nothing more was run.");
    var (doc, civilDoc) = ActiveDrawing(write);
    PluginRuntime.CheckRequestedDrawing(doc, PluginRuntime.GetRequestedDrawing());   // C150 (same thread: the batch's value)
    using var documentLock = doc.LockDocument();
    using var transaction = doc.Database.TransactionManager.StartTransaction();
    var result = action(doc, civilDoc, doc.Database, transaction);
    if (write) transaction.Commit();
    return result;
  }

  /// <summary>Runs <paramref name="action"/> (a sequence of dispatched calls) in ONE command context: one queue
  /// slot, one start, one continuation for the whole batch instead of one per call. With singleTransaction the calls'
  /// own transactions nest in one outer transaction that commits once at the end (Civil 3D then updates dependent
  /// profile views once, live 5,6 s per committed profile view change) or is aborted when the action says so.</summary>
  public static async Task<T> ExecuteBatchAsync<T>(Func<Task<(T Result, bool Commit)>> action, bool singleTransaction,
    Func<T, bool, Task>? afterCommit = null)
  {
    if (InBatch)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", "A batch cannot contain another batch.");
    return await ExecuteSerializedAsync(async () =>
    {
      T? result = default;
      Exception? capturedException = null;
      var state = new ContextState();
      var callbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      async Task Context() => await App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
      {
        if (Interlocked.CompareExchange(ref state.Value, Running, NotStarted) != NotStarted) return;
        DocumentLock? outerLock = null;
        Transaction? outer = null;
        try
        {
          if (singleTransaction)
          {
            var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
            outerLock = doc.LockDocument();
            outer = doc.Database.TransactionManager.StartTransaction();
          }

          BatchThread.Value = Environment.CurrentManagedThreadId;
          var (value, commit) = await action();
          result = value;
          if (outer != null)
          {
            if (commit) outer.Commit();
            else outer.Abort();
          }
          // C210: a step after the calls (batch save), still in the batch's command context and after the outer commit
          if (afterCommit != null) await afterCommit(value, commit);
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
        finally
        {
          outer?.Dispose();                          // not committed = aborted: nothing of the batch stays
          outerLock?.Dispose();
          BatchThread.Value = 0;
          Volatile.Write(ref state.Value, Finished);
          callbackDone.TrySetResult();
        }
      }, null);

      await AwaitContextAsync(Context(), state, callbackDone.Task).ConfigureAwait(false);
      if (capturedException != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      return result!;
    });
  }

  public static async Task<T> ExecuteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    if (InBatch) return RunInline(action, write);
    var requestedDrawing = PluginRuntime.GetRequestedDrawing();   // C150: read here, the command context may not carry it
    return await ExecuteSerializedAsync(async () =>
    {
      T? result = default;
      Exception? capturedException = null;
      var state = new ContextState();
      var callbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

      var context = RunInCommandContextAsync(() =>
      {
        // Abandoned after StartTimeout: AutoCAD may still run it later, then it must not touch the drawing.
        if (Interlocked.CompareExchange(ref state.Value, Running, NotStarted) != NotStarted) return;
        try
        {
          var (doc, civilDoc) = ActiveDrawing(write);
          PluginRuntime.CheckRequestedDrawing(doc, requestedDrawing);
          var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
          var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
          }
          var database = doc.Database;

          using var documentLock = doc.LockDocument();
          using var transaction = database.TransactionManager.StartTransaction();

          result = action(doc, civilDoc, database, transaction);

          if (write)
          {
            transaction.Commit();
          }
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
        finally
        {
          Volatile.Write(ref state.Value, Finished);
          callbackDone.TrySetResult();
        }
      });

      await AwaitContextAsync(context, state, callbackDone.Task).ConfigureAwait(false);

      if (capturedException != null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      }

      return result!;
    });
  }

  private static async Task RunInCommandContextAsync(Action callback)
  {
    await App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
    {
      callback();
      await Task.CompletedTask;
    }, null);
  }

  /// <summary>
  /// Waits for a command-context call without letting a stranded request hold the host gate
  /// forever (live 2026-10-02: a call AutoCAD never ran locked the plugin until Civil 3D closed).
  /// Not started within StartTimeout: abandoned, the callback becomes a no-op, nothing changes.
  /// Callback finished but AutoCAD's continuation missing after ContinuationGrace (the C5
  /// pattern): the callback's result is used. A callback still running (a long rebuild) is waited for.
  /// </summary>
  private static async Task AwaitContextAsync(Task context, ContextState state, Task callbackDone)
  {
    var first = await Task.WhenAny(context, callbackDone, Task.Delay(StartTimeout)).ConfigureAwait(false);
    if (first != context && first != callbackDone)
    {
      if (Interlocked.CompareExchange(ref state.Value, Abandoned, NotStarted) == NotStarted)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.TIMEOUT",
          $"Civil 3D did not run the request within {StartTimeout.TotalSeconds:0} s (command active or host stalled). Nothing was changed.");
      }
      await Task.WhenAny(context, callbackDone).ConfigureAwait(false);
    }

    if (!context.IsCompleted &&
        await Task.WhenAny(context, Task.Delay(ContinuationGrace)).ConfigureAwait(false) != context)
    {
      PluginLog.Warn("Execution", $"Command-context continuation not invoked {ContinuationGrace.TotalSeconds:0} s after the callback finished; using the callback result.");
      return;
    }

    await context.ConfigureAwait(false);
  }

  public static async Task<T> ExecuteInCommandContextAsync<T>(Func<Task<T>> action)
  {
    if (InBatch) return await action();                 // already in the batch's command context
    return await ExecuteSerializedAsync(async () =>
    {
      T? result = default;
      Exception? capturedException = null;

      await App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
      {
        try
        {
          result = await action();
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
      }, null);

      if (capturedException != null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
      }

      return result!;
    });
  }

  /// <summary>
  /// Write that runs inside a real AutoCAD command (<see cref="DeferredCommand"/>) instead of
  /// the command context. The host gate is held until the command finishes or the timeout
  /// expires; the timeout is kept below the Node client's 120 s so the caller gets a real error.
  /// </summary>
  public static async Task<T> WriteInCommandAsync<T>(
    Func<Document, CivilDocument, Database, Transaction, T> action, string trace, TimeSpan timeout)
  {
    if (InBatch)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{trace} runs as its own AutoCAD command and cannot be part of a batch. Nothing more was run.");
    return await ExecuteSerializedAsync(async () =>
    {
      // Started from Application.Idle, never from command context (see DeferredCommand);
      // the drawing identity is checked by the command before it changes anything.
      var requestedDrawing = PluginRuntime.GetRequestedDrawing();   // C150: checked by the command before it changes anything
      var completion = DeferredCommand.Enqueue(
        (d, c, db, tr) => { PluginRuntime.CheckRequestedDrawing(d, requestedDrawing); return action(d, c, db, tr); },
        PluginRuntime.GetExpectedDrawingIdentity(), trace);
      DeferredCommand.StartOnIdle();

      var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
      if (finished != completion.Task)
      {
        throw DeferredCommand.Withdraw(completion)
          ? new JsonRpcDispatchException("CIVIL3D.TIMEOUT", $"{trace}: the Civil 3D command did not start within {timeout.TotalSeconds:0} s. Nothing was changed.")
          : new JsonRpcDispatchException("CIVIL3D.TIMEOUT", $"{trace}: the Civil 3D command started but did not finish within {timeout.TotalSeconds:0} s; check the drawing before retrying.");
      }

      return (T)(await completion.Task.ConfigureAwait(false))!;
    }).ConfigureAwait(false);
  }

  // C93: the step after the answer (document_close) holds the host gate while it waits for an idle Civil 3D, and the
  // next request queues behind it within the client timeout (120 s); a close not started in time is skipped (logged).
  private static readonly TimeSpan AfterAnswerTimeout = TimeSpan.FromSeconds(30);

  /// <summary>
  /// C93 (2026-10-10): runs <paramref name="action"/> in AutoCAD's application context, where documents can be opened,
  /// created, activated and closed (DocumentCollection.Open/Add, MdiActiveDocument, Document.CloseAndDiscard); the
  /// command context of ExecuteAsync and the deferred command both belong to a document. The action runs on the main
  /// thread from Application.Idle when no command is running (<see cref="ApplicationContextQueue"/>), holding the host
  /// gate like every other host operation, so the next request starts after it and its command context belongs to the
  /// document that is active then. Not inside a batch (the batch holds a document's command context), except for
  /// reads that are safe there (<paramref name="inlineInBatch"/>).
  /// </summary>
  public static async Task<T> ExecuteInApplicationContextAsync<T>(Func<T> action, string trace, TimeSpan timeout, bool inlineInBatch = false)
  {
    if (InBatch)
    {
      if (!inlineInBatch)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{trace} runs in the Civil 3D application context (documents are switched or closed there) and cannot be part of a batch. Nothing more was run.");
      if (Environment.CurrentManagedThreadId != BatchThread.Value)
        throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"'{trace}' left the main thread inside a batch; call it on its own. Nothing more was run.");
      return action();
    }
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    return await ExecuteSerializedAsync(() => ApplicationContextQueue.RunAsync(action, trace, timeout, cancellationToken)).ConfigureAwait(false);
  }

  /// <summary>
  /// C209 (2026-11-24): runs <paramref name="action"/> in the application context like
  /// <see cref="ExecuteInApplicationContextAsync{T}"/>, then, still holding the host gate, waits until
  /// <paramref name="settled"/> holds in the application context (checked every 100 ms) and a command context call
  /// runs in the document it names (AutoCAD switches the command context after MdiActiveDocument changes; a write
  /// that came too early was refused as "running in the context of another document"). Returns the action's result
  /// and how long the wait took; settled is false when the timeout passed first.
  /// </summary>
  public static async Task<(T Result, bool Settled, long WaitedMs)> ExecuteInApplicationContextThenSettleAsync<T>(
    Func<T> action, Func<bool> settled, Func<bool> inCommandContext, string trace, TimeSpan timeout, TimeSpan settleTimeout)
  {
    if (InBatch)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{trace} runs in the Civil 3D application context (documents are switched or closed there) and cannot be part of a batch. Nothing more was run.");
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    return await ExecuteSerializedAsync(async () =>
    {
      var result = await ApplicationContextQueue.RunAsync(action, trace, timeout, cancellationToken).ConfigureAwait(false);
      var timer = System.Diagnostics.Stopwatch.StartNew();
      var ok = false;
      while (timer.Elapsed < settleTimeout)
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (await ApplicationContextQueue.RunAsync(settled, trace + " (settle)", settleTimeout, cancellationToken).ConfigureAwait(false))
        {
          ok = true;
          break;
        }
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
      }
      if (ok)
      {
        // The command context probe: the next request's call runs in the same kind of context.
        var state = new ContextState();
        var callbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = false;
        var context = RunInCommandContextAsync(() =>
        {
          if (Interlocked.CompareExchange(ref state.Value, Running, NotStarted) != NotStarted) return;
          try { probe = inCommandContext(); }
          catch (Exception ex) { PluginLog.Warn("Execution", $"{trace}: settle probe failed: {ex.Message}"); }
          finally
          {
            Volatile.Write(ref state.Value, Finished);
            callbackDone.TrySetResult();
          }
        });
        try
        {
          await AwaitContextAsync(context, state, callbackDone.Task).ConfigureAwait(false);
        }
        catch (JsonRpcDispatchException ex)
        {
          PluginLog.Warn("Execution", $"{trace}: settle probe not run: {ex.Message}");
        }
        ok = probe;
      }
      return (result, ok, timer.ElapsedMilliseconds);
    }).ConfigureAwait(false);
  }

  /// <summary>
  /// C93: like <see cref="ExecuteInApplicationContextAsync{T}"/>, and the step the action returns runs in the
  /// application context only after the answer has been written to the client (document_close: "never close before the
  /// answer has been sent"). The host gate is held from the action through the step, so no other request reaches Civil 3D
  /// in between; the step is skipped when the answer was an error or did not reach the client (the request then had no
  /// effect beyond what the action itself did and reported). <paramref name="whenScheduled"/> hears that the step is
  /// registered, <paramref name="whenNotRun"/> why a registered step was not run or failed (documents_list shows it).
  /// </summary>
  public static async Task<T> ExecuteThenAfterAnswerAsync<T>(Func<(T Result, Action? AfterAnswer)> action, string trace, TimeSpan timeout,
    Action<string>? whenNotRun = null, Action? whenScheduled = null)
  {
    if (InBatch)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{trace} runs in the Civil 3D application context and cannot be part of a batch. Nothing more was run.");
    // A job inherits the scope of the request that started it, already answered: not open, refused as no scope.
    var answer = AnswerScope.Current is { IsOpen: true } open ? open
      : throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"{trace} must be a request of its own (not a job). Nothing was changed.");
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    PluginRuntime.QueueHostOperation();
    var started = false;
    var handedOver = false;
    try
    {
      await HostExecutionGate.WaitAsync(cancellationToken);
      started = true;
      PluginRuntime.StartHostOperation();
      cancellationToken.ThrowIfCancellationRequested();
      (T Result, Action? AfterAnswer) outcome;
      using (IdleWatchdog.Arm())
        outcome = await ApplicationContextQueue.RunAsync(action, trace, timeout, cancellationToken).ConfigureAwait(false);
      if (outcome.AfterAnswer is { } afterAnswer)
      {
        // The gate passes to the step: released when it has run or been skipped.
        handedOver = answer.TryRegister(written => _ = RunAfterAnswerAsync(afterAnswer, written, trace, whenNotRun));
        if (!handedOver)
        {
          Tell(whenNotRun, "the step after the answer could not be scheduled");
          throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"{trace}: the step after the answer could not be scheduled and was not run; check documents_list.");
        }
        try
        {
          whenScheduled?.Invoke();
        }
        catch (Exception ex)
        {
          PluginLog.Warn("Execution", $"{trace}: reporting the scheduled step failed: {ex.Message}");
        }
      }
      return outcome.Result;
    }
    finally
    {
      if (!started)
      {
        PluginRuntime.CancelQueuedHostOperation();
      }
      else if (!handedOver)
      {
        PluginRuntime.CompleteHostOperation();
        HostExecutionGate.Release();
      }
    }
  }

  private static async Task RunAfterAnswerAsync(Action afterAnswer, bool answerWritten, string trace, Action<string>? whenNotRun)
  {
    try
    {
      if (!answerWritten)
      {
        PluginLog.Warn("Execution", $"{trace}: the answer was an error or did not reach the client; the step after it was not run.");
        Tell(whenNotRun, "the answer was an error or did not reach the client");
        return;
      }

      using var nudge = IdleWatchdog.Arm();
      await ApplicationContextQueue.RunAsync<object?>(() =>
      {
        afterAnswer();
        return null;
      }, $"{trace} (after the answer)", AfterAnswerTimeout).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      PluginLog.Error("Execution", $"{trace}: the step after the answer failed.", ex);
      Tell(whenNotRun, ex.Message);
    }
    finally
    {
      PluginRuntime.CompleteHostOperation();
      HostExecutionGate.Release();
    }
  }

  private static void Tell(Action<string>? whenNotRun, string reason)
  {
    try
    {
      whenNotRun?.Invoke(reason);
    }
    catch (Exception ex)
    {
      PluginLog.Warn("Execution", $"Reporting a step that was not run failed: {ex.Message}");
    }
  }

  public static Task<T> ReadAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, false);
  }

  public static Task<T> WriteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, true);
  }

  private static async Task<T> ExecuteSerializedAsync<T>(Func<Task<T>> action)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    PluginRuntime.QueueHostOperation();
    var started = false;

    try
    {
      await HostExecutionGate.WaitAsync(cancellationToken);
      started = true;
      PluginRuntime.StartHostOperation();
      cancellationToken.ThrowIfCancellationRequested();
      using var nudge = IdleWatchdog.Arm();             // C64: keep the message loop moving while this runs
      return await action();
    }
    finally
    {
      if (started)
      {
        PluginRuntime.CompleteHostOperation();
        HostExecutionGate.Release();
      }
      else
      {
        PluginRuntime.CancelQueuedHostOperation();
      }
    }
  }
}
