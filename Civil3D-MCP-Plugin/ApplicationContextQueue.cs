using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// C93 (2026-10-10): work that must run in the AutoCAD application context (open, create, activate and close
/// documents), used through <see cref="CivilExecution.ExecuteInApplicationContextAsync{T}"/>. It starts from
/// Application.Idle when the active document has no command running, the pattern DeferredCommand and the C70 reload
/// use (IdleWatchdog keeps idle passes coming while a host operation waits); should Idle ever not be the application
/// context, the work is handed to DocumentCollection.ExecuteInApplicationContext. One piece of work at a time; the host
/// gate serializes the callers. The idle handler is attached once on the main thread (plugin start) and detached at
/// shutdown, so no worker thread adds or removes it (a removal racing a new subscription would strand work).
/// </summary>
internal static class ApplicationContextQueue
{
  private sealed class Work(Func<object?> action, string trace)
  {
    public Func<object?> Action { get; } = action;
    public string Trace { get; } = trace;
    public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  }

  private static readonly object Sync = new();
  private static Work? _pending;
  private static bool _attached;
  private static bool _shutDown;

  /// <summary>Attaches the idle handler; call on the main thread (plugin initialization).</summary>
  internal static void Initialize()
  {
    lock (Sync)
    {
      if (_attached) return;
      _attached = true;
      _shutDown = false;
    }
    App.Idle += OnIdle;
  }

  /// <summary>Waits for the work to run (at most <paramref name="timeout"/> for it to start); a cancelled request
  /// withdraws work that has not started.</summary>
  internal static async Task<T> RunAsync<T>(Func<T> action, string trace, TimeSpan timeout, CancellationToken cancellationToken = default)
  {
    if (!Volatile.Read(ref _attached))
      throw Volatile.Read(ref _shutDown)
        ? new JsonRpcDispatchException("CIVIL3D.BUSY", "The Civil 3D MCP plugin is being reloaded; retry the request.")
        : new JsonRpcDispatchException("CIVIL3D.UNAVAILABLE", $"{trace}: the application-context queue is not running (plugin not initialized). Nothing was changed.");
    var work = new Work(() => action(), trace);
    var deadline = DateTime.UtcNow + timeout;
    lock (Sync)
    {
      if (_pending != null)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", $"{trace}: another application-context operation is pending. Nothing was changed.");
      _pending = work;
    }

    var finished = await Task.WhenAny(work.Completion.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
    if (finished != work.Completion.Task)
    {
      if (cancellationToken.IsCancellationRequested)
      {
        if (Withdraw(work)) throw new OperationCanceledException(cancellationToken);
        // Started already: its result stands, waited for no longer than an uncancelled request would wait.
        var remaining = deadline - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero && await Task.WhenAny(work.Completion.Task, Task.Delay(remaining)).ConfigureAwait(false) == work.Completion.Task)
          return (T)(await work.Completion.Task.ConfigureAwait(false))!;
        throw new JsonRpcDispatchException("CIVIL3D.TIMEOUT", $"{trace}: started in Civil 3D but did not finish within {timeout.TotalSeconds:0} s (a dialog may be waiting); check Civil 3D before retrying.");
      }
      throw Withdraw(work)
        ? new JsonRpcDispatchException("CIVIL3D.TIMEOUT", $"{trace}: Civil 3D was not idle within {timeout.TotalSeconds:0} s (a command is running). Nothing was changed.")
        : new JsonRpcDispatchException("CIVIL3D.TIMEOUT", $"{trace}: started in Civil 3D but did not finish within {timeout.TotalSeconds:0} s (a dialog may be waiting); check Civil 3D before retrying.");
    }
    return (T)(await work.Completion.Task.ConfigureAwait(false))!;
  }

  /// <summary>Detaches the idle handler and fails queued work (plugin reload, C70).</summary>
  internal static void Shutdown()
  {
    App.Idle -= OnIdle;
    Work? pending;
    lock (Sync)
    {
      _attached = false;
      _shutDown = true;
      pending = _pending;
      _pending = null;
    }
    pending?.Completion.TrySetException(new JsonRpcDispatchException("CIVIL3D.BUSY", "The Civil 3D MCP plugin is being reloaded; retry the request."));
  }

  private static bool Withdraw(Work work)
  {
    lock (Sync)
    {
      if (!ReferenceEquals(_pending, work)) return false;
      _pending = null;
    }
    return true;
  }

  private static void OnIdle(object? sender, EventArgs e)
  {
    Work? work;
    lock (Sync) work = _pending;
    if (work == null) return;

    var doc = App.DocumentManager.MdiActiveDocument;
    if (doc != null && !doc.Editor.IsQuiescent) return;   // never while a command runs; the next idle comes
    lock (Sync)
    {
      if (!ReferenceEquals(_pending, work)) return;
      _pending = null;
    }

    if (App.DocumentManager.IsApplicationContext)
    {
      Run(work);
      return;
    }

    // Need: Idle is expected to be the application context; if it is not, AutoCAD is asked to run the work there.
    PluginLog.Warn("Execution", $"{work.Trace}: Application.Idle was not in the application context; handed to ExecuteInApplicationContext.");
    try
    {
      App.DocumentManager.ExecuteInApplicationContext(_ => Run(work), null);
    }
    catch (System.Exception ex)
    {
      work.Completion.TrySetException(ex);
    }
  }

  private static void Run(Work work)
  {
    try
    {
      PluginLog.Info("Trace", $"{work.Trace}: running in the application context");
      var result = work.Action();
      DocumentSnapshot.Refresh();
      work.Completion.TrySetResult(result);
    }
    catch (System.Exception ex)
    {
      work.Completion.TrySetException(ex);
    }
  }
}
