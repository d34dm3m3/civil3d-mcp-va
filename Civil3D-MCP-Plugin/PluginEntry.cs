using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Civil3DMcpPlugin.PluginEntry))]
[assembly: CommandClass(typeof(Civil3DMcpPlugin.PluginEntry))]

namespace Civil3DMcpPlugin;

public sealed class PluginEntry : IExtensionApplication
{
  public void Initialize()
  {
    try
    {
      // C93: watchdog, application-context queue and document snapshot first, so they run even when the listener
      // cannot start (port in use; C3DMCPSTART later) and before the first request can arrive.
      IdleWatchdog.Initialize();
      ApplicationContextQueue.Initialize();              // idle handler attached once, on the main thread
      DocumentSnapshot.Start();
      PluginRuntime.StartServer();
      SelectionTracker.Start();
      if (AppContext.GetData(PluginReloadCommands.RequestKey) != null) DeferredCommand.RegisterOwnCommand();   // C70b
      PluginLog.Info("PluginEntry", $"Civil3D MCP plugin initialized on port {PluginRuntime.Port} (build {PluginReloadCommands.Build()}). Log file: {PluginLog.LogFilePath}");
      WriteMessage("Civil3D MCP plugin initialized.");
    }
    catch (System.Exception ex)
    {
      PluginLog.Error("PluginEntry", "Plugin failed to initialize", ex);
      WriteMessage($"Civil3D MCP plugin failed to initialize: {ex.Message}");
    }
  }

  public void Terminate() => Shutdown();

  /// <summary>Stops the listener and detaches everything hooked into Civil 3D, so a newer build can be loaded beside
  /// this one by the loader without restarting Civil 3D (C70). Also used when Civil 3D closes.</summary>
  public void Shutdown()
  {
    try
    {
      PluginRuntime.StopServer();
      SelectionTracker.Stop();
      DeferredCommand.Shutdown();
      DeferredCommand.UnregisterOwnCommand();
      ApplicationContextQueue.Shutdown();                // C93
      DocumentSnapshot.Stop();
      IdleWatchdog.Shutdown();
      PluginLog.Info("PluginEntry", $"Civil3D MCP plugin terminated cleanly (build {PluginReloadCommands.Build()}).");
    }
    catch (System.Exception ex)
    {
      PluginLog.Error("PluginEntry", "Error during plugin termination", ex);
    }
  }

  [CommandMethod("C3DMCPSTART")]
  public void StartCommand()
  {
    PluginRuntime.StartServer();
    WriteMessage($"Civil3D MCP listener started on port {PluginRuntime.Port}.");
  }

  [CommandMethod("C3DMCPSTOP")]
  public void StopCommand()
  {
    PluginRuntime.StopServer();
    WriteMessage("Civil3D MCP listener stopped.");
  }

  [CommandMethod("C3DMCPSTATUS")]
  public void StatusCommand()
  {
    var status = PluginRuntime.GetStatus();
    WriteMessage($"Civil3D MCP listener running: {status.IsRunning}; pending: {status.QueueDepth}; active: {status.OperationInProgress}; current: {status.CurrentOperation ?? "<none>"}");
  }

  private static void WriteMessage(string message)
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    doc?.Editor.WriteMessage($"\n{message}");
  }
}
