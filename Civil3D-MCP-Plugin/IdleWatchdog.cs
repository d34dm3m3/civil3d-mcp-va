using System.Runtime.InteropServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Keeps AutoCAD's message loop moving while a request is in flight (C64, 2026-10-08). Application.Idle and the
/// command-context continuation only run when the main thread processes a message; with no mouse or keyboard
/// activity a queued deferred command could wait, or the continuation was "not invoked", until the user touched
/// Civil 3D (a small plot woke it). While armed, a background timer posts WM_NULL (a no-op message) to the main
/// window every second; processing it ends with an idle pass, which starts queued work.
/// </summary>
internal static class IdleWatchdog
{
  private const uint WmNull = 0x0000;
  private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);
  private static readonly object Sync = new();
  private static IntPtr _mainWindow;
  private static int _armed;
  private static Timer? _timer;
  private static long _posted;

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

  /// <summary>Remembers the main window; call on the main thread (plugin initialization).</summary>
  internal static void Initialize()
  {
    try
    {
      _mainWindow = App.MainWindow?.Handle ?? IntPtr.Zero;
    }
    catch (System.Exception ex)
    {
      PluginLog.Warn("Watchdog", $"Main window handle not available, watchdog off: {ex.Message}");
      _mainWindow = IntPtr.Zero;
    }
  }

  /// <summary>Stops nudging regardless of open arms (plugin reload, C70).</summary>
  internal static void Shutdown()
  {
    lock (Sync)
    {
      _armed = 0;
      _timer?.Dispose();
      _timer = null;
    }
  }

  internal static long PostedCount => Interlocked.Read(ref _posted);

  /// <summary>Starts nudging (reference counted); dispose the result to stop.</summary>
  internal static IDisposable Arm()
  {
    lock (Sync)
    {
      if (_armed++ == 0 && _mainWindow != IntPtr.Zero)
        _timer = new Timer(_ => Nudge(), null, Period, Period);
    }

    return new Release();
  }

  private static void Nudge()
  {
    if (_mainWindow != IntPtr.Zero && PostMessage(_mainWindow, WmNull, IntPtr.Zero, IntPtr.Zero))
      Interlocked.Increment(ref _posted);
  }

  private sealed class Release : IDisposable
  {
    private int _done;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _done, 1) != 0) return;
      lock (Sync)
      {
        if (--_armed > 0) return;
        _armed = 0;
        _timer?.Dispose();
        _timer = null;
      }
    }
  }
}
