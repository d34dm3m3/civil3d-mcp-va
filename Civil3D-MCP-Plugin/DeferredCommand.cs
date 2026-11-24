using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Civil3DMcpPlugin.DeferredCommand))]

namespace Civil3DMcpPlugin;

/// <summary>
/// Runs one queued write inside a real AutoCAD command instead of
/// ExecuteInCommandContextAsync. Creating a corridor surface in command context
/// committed correctly but the returned task never completed (live 2026-10-02,
/// three times; released only when Civil 3D closed). The command is started with
/// a fixed string; the work and its parameters stay in plugin memory.
/// </summary>
public sealed class DeferredCommand
{
  internal const string CommandName = "C3DMCPDEFERRED";

  private static readonly object Sync = new();
  private static Pending? _pending;
  private static Finished? _finished;

  // C70b: a build loaded by the C70 loader registers a command name of its own. AutoCAD keeps routing the fixed
  // name C3DMCPDEFERRED to the first build loaded in the session (live 2026-10-09: after a reload the queued command
  // reached the old build, whose queue was empty, and plots timed out), so the fixed name only serves a plugin
  // loaded on its own.
  private static string _commandName = CommandName;
  private static string? _ownGroup;
  private static Autodesk.AutoCAD.Internal.CommandCallback? _ownCallback;

  /// <summary>Registers this build's own deferred command (C70b); call on the main thread.</summary>
  internal static void RegisterOwnCommand()
  {
    if (_ownGroup != null) return;
    var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    var group = $"C3DMCP_{suffix}";
    var name = $"C3DMCPRUN{suffix}";
    _ownCallback = Run;
    Autodesk.AutoCAD.Internal.Utils.AddCommand(group, name, name, CommandFlags.Modal | CommandFlags.NoUndoMarker, _ownCallback);
    _ownGroup = group;
    _commandName = name;
    PluginLog.Info("Trace", $"Deferred command registered as {name}.");
  }

  /// <summary>Removes this build's own deferred command (plugin reload).</summary>
  internal static void UnregisterOwnCommand()
  {
    if (_ownGroup == null) return;
    try
    {
      Autodesk.AutoCAD.Internal.Utils.RemoveCommand(_ownGroup, _commandName);
    }
    catch (System.Exception ex)
    {
      PluginLog.Warn("Trace", $"Could not remove {_commandName}: {ex.Message}");
    }
    _ownGroup = null;
    _ownCallback = null;
    _commandName = CommandName;
  }

  private sealed record Pending(
    Func<Document, CivilDocument, Database, Transaction, object?> Action,
    TaskCompletionSource<object?> Completion,
    string? DrawingIdentity,
    string Trace);

  private sealed record Finished(
    TaskCompletionSource<object?> Completion,
    object? Result,
    System.Exception? Error,
    string Trace);

  internal static TaskCompletionSource<object?> Enqueue(
    Func<Document, CivilDocument, Database, Transaction, object?> action, string? drawingIdentity, string trace)
  {
    var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    lock (Sync)
    {
      if (_pending != null)
        throw new JsonRpcDispatchException("CIVIL3D.CONFLICT", "Another deferred Civil 3D command is already pending.");
      _pending = new Pending(action, completion, drawingIdentity, trace);
    }
    return completion;
  }

  /// <summary>Detaches the idle handlers and fails queued or finished work (plugin reload, C70).</summary>
  internal static void Shutdown()
  {
    App.Idle -= OnIdle;
    App.Idle -= OnIdleAfterCommand;
    Pending? pending;
    Finished? finished;
    lock (Sync)
    {
      pending = _pending;
      finished = _finished;
      _pending = null;
      _finished = null;
    }
    var reloading = new JsonRpcDispatchException("CIVIL3D.BUSY", "The Civil 3D MCP plugin is being reloaded; retry the request.");
    pending?.Completion.TrySetException(reloading);
    if (finished != null)
    {
      if (finished.Error != null) finished.Completion.TrySetException(finished.Error);
      else finished.Completion.TrySetResult(finished.Result);
    }
  }

  /// <summary>Drops the queued work if it is still the given one (e.g. after a timeout).</summary>
  internal static bool Withdraw(TaskCompletionSource<object?> completion)
  {
    lock (Sync)
    {
      if (_pending?.Completion != completion) return false;
      _pending = null;
    }
    App.Idle -= OnIdle;
    return true;
  }

  /// <summary>
  /// Starts the command from Application.Idle rather than from command context: a
  /// command-context continuation still pending when a corridor surface is created is
  /// never invoked (live 2026-10-02), so nothing here may wait on ExecutionResult.
  /// </summary>
  internal static void StartOnIdle()
  {
    App.Idle -= OnIdle;
    App.Idle += OnIdle;
  }

  private static void OnIdle(object? sender, EventArgs e)
  {
    Pending? pending;
    lock (Sync) pending = _pending;
    if (pending == null)
    {
      App.Idle -= OnIdle;
      return;
    }

    try
    {
      var doc = App.DocumentManager.MdiActiveDocument;
      // Never type into a command the user is running; wait for the next idle instead.
      if (doc == null || !doc.Editor.IsQuiescent) return;
      App.Idle -= OnIdle;
      doc.SendStringToExecute(_commandName + "\n", true, false, false);
      PluginLog.Info("Trace", $"{pending.Trace}: command queued");
    }
    catch (System.Exception ex)
    {
      App.Idle -= OnIdle;
      if (Withdraw(pending.Completion)) pending.Completion.TrySetException(ex);
    }
  }

  [CommandMethod(CommandName, CommandFlags.Modal | CommandFlags.NoUndoMarker)]
  public static void Run()
  {
    Pending? pending;
    lock (Sync)
    {
      pending = _pending;
      _pending = null;
    }
    if (pending == null) return;

    try
    {
      PluginLog.Info("Trace", $"{pending.Trace}: command started");
      var (doc, civilDoc) = CivilExecution.ActiveDrawing(write: true);   // C93c: the active drawing and its own Civil document
      var activeIdentity = PluginRuntime.GetDrawingIdentity(doc);
      if (!string.IsNullOrWhiteSpace(pending.DrawingIdentity) &&
          !string.Equals(pending.DrawingIdentity, activeIdentity, StringComparison.OrdinalIgnoreCase))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.CONFLICT",
          $"The active drawing changed from '{pending.DrawingIdentity}' to '{activeIdentity}' before the command ran. No drawing changes were made.");
      }
      object? result;
      using (var transaction = doc.Database.TransactionManager.StartTransaction())
      {
        result = pending.Action(doc, civilDoc, doc.Database, transaction);
        transaction.Commit();
      }
      PluginLog.Info("Trace", $"{pending.Trace}: command committed");
      FinishAfterCommand(pending, result, null);
    }
    catch (System.Exception ex)
    {
      FinishAfterCommand(pending, null, ex);
    }
  }

  /// <summary>
  /// Hands the result back only after the command has ended and the editor is quiescent.
  /// Completing inside Run() released the host gate while the command was still active;
  /// the next request's ExecuteInCommandContextAsync arrived 14 ms later and was never
  /// run (live 2026-10-02, REGEN did not release it).
  /// </summary>
  private static void FinishAfterCommand(Pending pending, object? result, System.Exception? error)
  {
    lock (Sync) _finished = new Finished(pending.Completion, result, error, pending.Trace);
    App.Idle -= OnIdleAfterCommand;
    App.Idle += OnIdleAfterCommand;
  }

  private static void OnIdleAfterCommand(object? sender, EventArgs e)
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    if (doc != null && !doc.Editor.IsQuiescent) return;
    App.Idle -= OnIdleAfterCommand;

    Finished? finished;
    lock (Sync)
    {
      finished = _finished;
      _finished = null;
    }
    if (finished == null) return;

    PluginLog.Info("Trace", $"{finished.Trace}: command ended");
    if (finished.Error != null) finished.Completion.TrySetException(finished.Error);
    else finished.Completion.TrySetResult(finished.Result);
  }
}
