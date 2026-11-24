using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Autodesk.AutoCAD.Runtime;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Civil3DMcpLoader.LoaderEntry))]
[assembly: CommandClass(typeof(Civil3DMcpLoader.LoaderCommands))]

namespace Civil3DMcpLoader;

/// <summary>
/// C65 (2026-10-09): Civil 3D locks a DLL it has loaded, so every plugin build had to wait
/// until Civil 3D was closed. The loader is autoloaded instead of the plugin: it copies the plugin folder (beside
/// the loader, the trusted folder) to a fresh shadow folder under %TEMP%\Civil3DMcp\shadow and loads the plugin from
/// there, so bin/Release can be rebuilt while Civil 3D runs. The plugin's own command classes are not registered
/// this way, so the loader registers the plugin's commands and forwards them.
/// C70 (2026-10-09): every build is loaded into its own AssemblyLoadContext, so a newer build can be loaded beside
/// the running one without restarting Civil 3D: the running build is shut down (listener, event handlers, idle
/// hooks), the newest build is copied and started. Triggered by the command C3DMCPRELOAD or by the plugin's
/// reloadPlugin request (through a Func published in AppContext); the reload runs on Application.Idle, not while a
/// command is active. Should the new build fail to start, the previous one is started again. Old builds stay
/// loaded (non-collectible contexts) until Civil 3D closes; a new loader itself still needs Civil 3D closed.
/// </summary>
public sealed class LoaderEntry : IExtensionApplication
{
  internal const string PluginFile = "Civil3DMcpPlugin.dll";
  private const string ReloadKey = "Civil3DMcp.RequestReload";
  private const int KeepShadows = 5;
  private const uint WmNull = 0x0000;
  private static readonly object Sync = new();
  internal static Assembly? Plugin;
  private static IExtensionApplication? _entry;
  private static string? _source;
  private static int _generation;
  private static IntPtr _mainWindow;
  private static bool _reloadPending;
  private static DateTime _reloadAfterUtc;

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

  public void Initialize()
  {
    try
    {
      _source = Path.GetDirectoryName(typeof(LoaderEntry).Assembly.Location)
        ?? throw new InvalidOperationException("The loader has no folder.");
      try
      {
        _mainWindow = App.MainWindow?.Handle ?? IntPtr.Zero;
      }
      catch (System.Exception)
      {
        _mainWindow = IntPtr.Zero;
      }

      AppContext.SetData(ReloadKey, (Func<string>)RequestReload);
      LoadNewest();
    }
    catch (System.Exception ex)
    {
      Write($"Civil3D MCP loader failed: {ex.GetType().Name}: {ex.Message}");
    }
  }

  public void Terminate()
  {
    try
    {
      Stop();
    }
    catch (System.Exception)
    {
      // shutting down
    }
  }

  /// <summary>Stops the running build and starts the newest one from bin/Release; on failure the previous build
  /// is started again. Runs on the main thread (idle handler or the C3DMCPRELOAD command).</summary>
  internal static void Reload()
  {
    var previousEntry = _entry;
    var previousPlugin = Plugin;
    Stop();
    try
    {
      LoadNewest();
    }
    catch (System.Exception ex)
    {
      Write($"Civil3D MCP reload failed: {ex.GetType().Name}: {ex.Message}. Starting the previous build again.");
      if (previousEntry == null) return;
      previousEntry.Initialize();
      _entry = previousEntry;
      Plugin = previousPlugin;
      LoaderCommands.Reset();
    }
  }

  /// <summary>Copies bin/Release to a fresh shadow folder, loads the plugin there into its own load context and
  /// starts it.</summary>
  private static void LoadNewest()
  {
    var source = _source ?? throw new InvalidOperationException("The loader is not initialized.");
    if (!File.Exists(Path.Combine(source, PluginFile)))
      throw new FileNotFoundException($"{PluginFile} is not beside the loader in {source}.");
    var generation = Interlocked.Increment(ref _generation);
    var shadowRoot = Path.Combine(Path.GetTempPath(), "Civil3DMcp", "shadow");
    var shadow = Path.Combine(shadowRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{generation}");
    Directory.CreateDirectory(shadow);
    foreach (var file in Directory.GetFiles(source))
    {
      var name = Path.GetFileName(file);
      if (name.StartsWith("Civil3DMcpLoader", StringComparison.OrdinalIgnoreCase) || name.Contains(".prev-", StringComparison.Ordinal))
        continue;
      File.Copy(file, Path.Combine(shadow, name), overwrite: true);
    }

    var context = new PluginLoadContext(shadow, $"Civil3DMcpPlugin-{generation}");
    var assembly = context.LoadFromAssemblyPath(Path.Combine(shadow, PluginFile));
    var entryType = assembly.GetType("Civil3DMcpPlugin.PluginEntry", throwOnError: true)!;
    var entry = (IExtensionApplication)Activator.CreateInstance(entryType)!;
    entry.Initialize();
    Plugin = assembly;
    _entry = entry;
    LoaderCommands.Reset();
    Write($"Civil3D MCP loader: plugin {assembly.GetName().Version} (load {generation}) loaded from {shadow}.");
    Prune(shadowRoot, shadow);
  }

  /// <summary>Shuts the running build down: Shutdown when the build has it (C70: listener, events, idle hooks),
  /// otherwise Terminate (listener only).</summary>
  private static void Stop()
  {
    var entry = _entry;
    if (entry == null) return;
    var shutdown = entry.GetType().GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
    if (shutdown != null) shutdown.Invoke(entry, null);
    else entry.Terminate();
    _entry = null;
  }

  /// <summary>Called by the plugin (its reloadPlugin request, on a worker thread): reloads at the next idle moment
  /// at least a second from now, so the plugin's answer reaches the client first. Returns the source folder.</summary>
  private static string RequestReload()
  {
    lock (Sync)
    {
      _reloadAfterUtc = DateTime.UtcNow.AddSeconds(1);
      if (!_reloadPending)
      {
        _reloadPending = true;
        App.Idle += OnIdle;
      }
    }

    // Application.Idle only fires when messages arrive: nudge the main window after the delay.
    _ = Task.Delay(1200).ContinueWith(_ => Nudge());
    _ = Task.Delay(3000).ContinueWith(_ => Nudge());
    return _source ?? "unknown";
  }

  private static void OnIdle(object? sender, EventArgs e)
  {
    lock (Sync)
    {
      if (DateTime.UtcNow < _reloadAfterUtc) return;
    }

    var doc = App.DocumentManager.MdiActiveDocument;
    if (doc != null && !doc.Editor.IsQuiescent) return;   // never while a command runs; the next idle comes
    App.Idle -= OnIdle;
    lock (Sync) _reloadPending = false;
    Reload();
  }

  private static void Nudge()
  {
    if (_mainWindow != IntPtr.Zero) PostMessage(_mainWindow, WmNull, IntPtr.Zero, IntPtr.Zero);
  }

  /// <summary>Removes old shadow copies (the loader's own temporary files), keeps the newest ones; a copy still in
  /// use by a running Civil 3D is locked and stays.</summary>
  private static void Prune(string shadowRoot, string current)
  {
    var old = Directory.GetDirectories(shadowRoot)
      .Where(d => !string.Equals(d, current, StringComparison.OrdinalIgnoreCase))
      .OrderByDescending(d => d, StringComparer.Ordinal)
      .Skip(KeepShadows - 1);
    foreach (var dir in old)
    {
      try
      {
        Directory.Delete(dir, recursive: true);
      }
      catch (System.Exception)
      {
        // locked by a running Civil 3D or already gone
      }
    }
  }

  internal static void Write(string message)
  {
    App.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n{message}");
  }
}

/// <summary>Loads the plugin's own files from its shadow folder; AutoCAD, Civil 3D and .NET come from the default
/// context, so their types are shared with Civil 3D.</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
  private readonly string _folder;

  public PluginLoadContext(string folder, string name) : base(name, isCollectible: false)
  {
    _folder = folder;
  }

  protected override Assembly? Load(AssemblyName assemblyName)
  {
    if (assemblyName.Name == null || assemblyName.Name.StartsWith("Civil3DMcpLoader", StringComparison.OrdinalIgnoreCase)) return null;
    var candidate = Path.Combine(_folder, assemblyName.Name + ".dll");
    return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
  }
}

/// <summary>The plugin's commands, registered here and forwarded to the shadow-loaded plugin.</summary>
public sealed class LoaderCommands
{
  private static object? _pluginEntry;

  /// <summary>Forgets the cached instance of the previous build (after a reload).</summary>
  internal static void Reset() => _pluginEntry = null;

  private static MethodInfo Method(string typeName, string methodName)
  {
    var plugin = LoaderEntry.Plugin ?? throw new InvalidOperationException("The Civil 3D MCP plugin is not loaded.");
    var type = plugin.GetType(typeName, throwOnError: true)!;
    return type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
      ?? throw new MissingMethodException(typeName, methodName);
  }

  private static void Forward(string typeName, string methodName)
  {
    try
    {
      var method = Method(typeName, methodName);
      object? target = null;
      if (!method.IsStatic)
        target = _pluginEntry ??= Activator.CreateInstance(method.DeclaringType!);
      method.Invoke(target, null);
    }
    catch (TargetInvocationException ex) when (ex.InnerException != null)
    {
      LoaderEntry.Write($"{methodName} failed: {ex.InnerException.Message}");
    }
    catch (System.Exception ex)
    {
      LoaderEntry.Write($"{methodName} failed: {ex.Message}");
    }
  }

  // Same name and flags as Civil3DMcpPlugin.DeferredCommand: queued writes run inside this command
  [CommandMethod("C3DMCPDEFERRED", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
  public static void Deferred() => Forward("Civil3DMcpPlugin.DeferredCommand", "Run");

  [CommandMethod("C3DMCPSTART")]
  public void Start() => Forward("Civil3DMcpPlugin.PluginEntry", "StartCommand");

  [CommandMethod("C3DMCPSTOP")]
  public void Stop() => Forward("Civil3DMcpPlugin.PluginEntry", "StopCommand");

  [CommandMethod("C3DMCPSTATUS")]
  public void Status() => Forward("Civil3DMcpPlugin.PluginEntry", "StatusCommand");

  /// <summary>C70: stop the running plugin build and start the newest one in bin/Release.</summary>
  [CommandMethod("C3DMCPRELOAD")]
  public static void Reload() => LoaderEntry.Reload();
}
