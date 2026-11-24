using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Plugin reload without restarting Civil 3D (C70). The loader (Civil3DMcpLoader) loads every plugin build into its
/// own AssemblyLoadContext from a shadow copy and publishes a reload request in <see cref="AppContext"/>; this asks
/// it to stop this build and start the newest one in bin/Release once Civil 3D is idle. Refused while a request,
/// a queued request or a job is running. Need: C65 made the DLL rebuildable while Civil 3D runs, but a new build
/// was only used after a restart (reported 2026-10-09: the fix was expected to make restarting Civil 3D
/// unnecessary).
/// </summary>
public static class PluginReloadCommands
{
  /// <summary>AppContext key under which the loader publishes a <c>Func&lt;string&gt;</c> that schedules the reload
  /// and returns the folder the new build is copied from.</summary>
  internal const string RequestKey = "Civil3DMcp.RequestReload";

  /// <summary>Write time of the running build and its shadow folder, e.g. "2026-10-09 16:20:11, 20261009-162013-35704".</summary>
  internal static string Build()
  {
    var location = typeof(PluginReloadCommands).Assembly.Location;
    if (string.IsNullOrEmpty(location) || !File.Exists(location)) return "unknown";
    return $"{File.GetLastWriteTime(location):yyyy-MM-dd HH:mm:ss}, {Path.GetFileName(Path.GetDirectoryName(location))}";
  }

  public static Task<object?> ReloadPluginAsync(JsonObject? parameters)
  {
    var status = PluginRuntime.GetStatus();
    if (status.OperationInProgress || status.QueueDepth > 0)
      throw new JsonRpcDispatchException("CIVIL3D.BUSY",
        $"Civil 3D is running '{status.CurrentOperation ?? "a request"}' ({status.QueueDepth} queued); reload when it is idle. Nothing was changed.");
    var jobs = JobRegistry.GetStats();
    if (jobs.Running > 0)
      throw new JsonRpcDispatchException("CIVIL3D.BUSY", $"{jobs.Running} job(s) still running; reload when they are done. Nothing was changed.");
    if (AppContext.GetData(RequestKey) is not Func<string> request)
      throw new JsonRpcDispatchException("CIVIL3D.UNSUPPORTED",
        "This plugin was not loaded by a loader with reload support (C70). Close Civil 3D once, deploy the new Civil3DMcpLoader.dll and start it again. Nothing was changed.");

    var source = request();
    PluginLog.Info("PluginEntry", $"Reload requested (running build {Build()}, new build from {source}).");
    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["scheduled"] = true,
      ["runningBuild"] = Build(),
      ["source"] = source,
      ["note"] = "The listener stops about a second after this answer; the newest build starts when Civil 3D is idle (not while a command runs). Check civil3d_health or the plugin log after a few seconds.",
    });
  }
}
