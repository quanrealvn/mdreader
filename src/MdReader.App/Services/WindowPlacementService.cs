using System.Windows;
using MdReader.App.Interop;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Settings;

namespace MdReader.App.Services;

/// Restores the main window placement at startup; Save records it during shutdown (window Closing or session end, §4.11).
/// Restore only if the saved rectangle intersects a monitor's work area; else centered 1200×900 DIP.
/// --capture forces 1280×900 in the normal state, parks the window off the visible desktop, and neither reads nor
/// writes the saved placement.
public sealed class WindowPlacementService
{
    internal const double DefaultWidth = 1200;
    internal const double DefaultHeight = 900;
    internal const double CaptureWidth = 1280;
    internal const double CaptureHeight = 900;
    private const double OffscreenMargin = 64;   // --capture parks the window this far left of every monitor
    private const int MinVisibleWidthPx = 100;
    private const int MinVisibleHeightPx = 40;
    private const string Category = "Placement";

    private readonly SettingsCoordinator _settings;
    private readonly CommandLineOptions _options;
    private readonly IAppLog _log;
    private WindowState _lastNonMinimizedState = WindowState.Normal;

    public WindowPlacementService(SettingsCoordinator settings, CommandLineOptions options, IAppLog log)
    {
        _settings = settings;
        _options = options;
        _log = log;
    }

    /// Call before the window is shown.
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (_options.CapturePath is not null)
        {
            // Park the capture window off the visible desktop so a screenshot run never covers what the user is doing.
            // The window is still Visible (never minimized), which is what WebView2 needs to keep rendering for
            // CapturePreviewAsync; only its position is outside every monitor.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = SystemParameters.VirtualScreenLeft - CaptureWidth - OffscreenMargin;
            window.Top = SystemParameters.VirtualScreenTop;
            window.Width = CaptureWidth;
            window.Height = CaptureHeight;
            window.WindowState = WindowState.Normal;
            return;
        }

        var saved = _settings.Current.Window;
        if (saved is not null && IsOnScreen(saved))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = saved.Left;
            window.Top = saved.Top;
            window.Width = saved.Width;
            window.Height = saved.Height;
            window.WindowState = saved.IsMaximized ? WindowState.Maximized : WindowState.Normal;
        }
        else
        {
            if (saved is not null)
            {
                _log.Write(AppLogLevel.Info, Category, "Saved window placement is off-screen; using the default placement");
            }

            var workArea = SystemParameters.WorkArea;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Width = Math.Min(DefaultWidth, Math.Max(window.MinWidth, workArea.Width - 48));
            window.Height = Math.Min(DefaultHeight, Math.Max(window.MinHeight, workArea.Height - 48));
            window.WindowState = WindowState.Normal;
        }

        _lastNonMinimizedState = window.WindowState;
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Minimized)
            {
                _lastNonMinimizedState = window.WindowState;
            }
        };
    }

    /// Records the window's current placement in the settings (in memory; the caller flushes). Called by the shutdown
    /// sequence while the window still exists: on Closing and on session end (§4.11). No-op for --capture.
    public void Save(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_options.CapturePath is not null)
        {
            return;
        }

        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
        {
            return;
        }

        // Minimized windows restore to the state they had before being minimized.
        var state = window.WindowState == WindowState.Minimized ? _lastNonMinimizedState : window.WindowState;
        var placement = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, state == WindowState.Maximized);
        _settings.Update(s => s with { Window = placement });
    }

    /// True if the rectangle (DIPs, converted with the system DPI) intersects some monitor's work area by a usable amount.
    private static bool IsOnScreen(WindowPlacement placement)
    {
        if (!double.IsFinite(placement.Left) || !double.IsFinite(placement.Top)
            || !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height))
        {
            return false;
        }

        var dpi = NativeMethods.GetDpiForSystem();
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var rect = new NativeMethods.RECT(
            (int)Math.Round(placement.Left * scale),
            (int)Math.Round(placement.Top * scale),
            (int)Math.Round((placement.Left + placement.Width) * scale),
            (int)Math.Round((placement.Top + placement.Height) * scale));

        var monitor = NativeMethods.MonitorFromRect(in rect, NativeMethods.MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var work = info.rcWork;
        var visibleWidth = Math.Min(rect.Right, work.Right) - Math.Max(rect.Left, work.Left);
        var visibleHeight = Math.Min(rect.Bottom, work.Bottom) - Math.Max(rect.Top, work.Top);
        return visibleWidth >= MinVisibleWidthPx && visibleHeight >= MinVisibleHeightPx
               && rect.Top >= work.Top - 8;   // the title bar must be reachable
    }
}
