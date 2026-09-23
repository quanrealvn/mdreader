using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Settings;
using MdReader.Ui.Interop;

namespace MdReader.Ui.Services;

/// <summary>
/// Restores the main window placement at startup; Save records it during shutdown (window Closing or session end,
/// §4.11). Restore only if the saved rectangle intersects a monitor's work area; else centered 1200×900 DIP.
/// --capture forces 1280×900 in the normal state, parks the window off the visible desktop, and neither reads nor
/// writes the saved placement.
/// </summary>
/// <remarks>
/// <para>
/// The settings store the outer window rectangle in device-independent units, the same one WPF's
/// <c>Window.Left/Top/Width/Height</c> describes, so both shells read each other's placement. Three conversions are
/// needed for that: Avalonia positions windows in physical pixels; <c>Window.Width/Height</c> set the <em>client</em>
/// size, so the frame has to be added back; and there is no <c>RestoreBounds</c>, so the last normal-state rectangle is
/// tracked here.
/// </para>
/// <para>The on-screen check uses the system DPI, exactly as the WPF shell does.</para>
/// </remarks>
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
    private Rect? _lastNormalBounds;
    private bool _lastNonMinimizedMaximized;

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
            ParkForCapture(window);
            return;
        }

        WindowPlacement? saved = _settings.Current.Window;
        if (saved is not null && IsOnScreen(saved))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            SetOuterSize(window, saved.Width, saved.Height);
            window.Position = ToPixels(window, saved.Left, saved.Top);
            window.WindowState = saved.IsMaximized ? WindowState.Maximized : WindowState.Normal;
        }
        else
        {
            if (saved is not null)
            {
                _log.Write(AppLogLevel.Info, Category, "Saved window placement is off-screen; using the default placement");
            }

            PixelRect work = (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary)?.WorkingArea
                             ?? new PixelRect(0, 0, 1920, 1080);
            double scale = DesktopScaling(window);
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SetOuterSize(window, Math.Min(DefaultWidth, Math.Max(window.MinWidth, (work.Width / scale) - 48)),
                                 Math.Min(DefaultHeight, Math.Max(window.MinHeight, (work.Height / scale) - 48)));
            window.WindowState = WindowState.Normal;
        }

        TrackNormalBounds(window);
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

        // Minimized windows restore to the state they had before being minimized; a maximized window is saved with the
        // rectangle it will return to.
        bool maximized = window.WindowState switch
        {
            WindowState.Maximized => true,
            WindowState.Minimized => _lastNonMinimizedMaximized,
            _ => false,
        };
        Rect? bounds = window.WindowState == WindowState.Normal ? CurrentBounds(window) : _lastNormalBounds;
        if (bounds is not { } rect || rect.Width <= 0 || rect.Height <= 0
            || !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height))
        {
            return;
        }

        var placement = new WindowPlacement(rect.X, rect.Y, rect.Width, rect.Height, maximized);
        _settings.Update(s => s with { Window = placement });
    }

    /// <summary>
    /// Park the capture window off the visible desktop so a screenshot run never covers what the user is doing. The
    /// window is still visible (never minimized), which is what WebView2 needs to keep rendering for
    /// CapturePreviewAsync; only its position is outside every monitor.
    /// </summary>
    private static void ParkForCapture(Window window)
    {
        double scale = DesktopScaling(window);
        int left = int.MaxValue;
        int top = int.MaxValue;
        foreach (Screen screen in window.Screens.All)
        {
            left = Math.Min(left, screen.Bounds.X);
            top = Math.Min(top, screen.Bounds.Y);
        }

        if (left == int.MaxValue)
        {
            left = 0;
            top = 0;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        SetOuterSize(window, CaptureWidth, CaptureHeight);
        window.WindowState = WindowState.Normal;
        window.Position = new PixelPoint(left - (int)Math.Round((CaptureWidth + OffscreenMargin) * scale), top);
    }

    /// <summary>
    /// Sizes the window so its outer rectangle is <paramref name="width"/> × <paramref name="height"/> DIP. Avalonia's
    /// Width/Height are the client size, and the difference is read from the window itself — its handle already exists,
    /// so the frame is whatever this window style is worth at this monitor's DPI.
    /// </summary>
    private static void SetOuterSize(Window window, double width, double height)
    {
        Size frame = window.FrameSize ?? window.ClientSize;
        Size client = window.ClientSize;
        double horizontal = Math.Max(0, frame.Width - client.Width);
        double vertical = Math.Max(0, frame.Height - client.Height);
        window.Width = Math.Max(window.MinWidth, width - horizontal);
        window.Height = Math.Max(window.MinHeight, height - vertical);
    }

    private void TrackNormalBounds(Window window)
    {
        _lastNormalBounds = null;
        _lastNonMinimizedMaximized = window.WindowState == WindowState.Maximized;
        window.PositionChanged += (_, _) => CaptureNormalBounds(window);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == TopLevel.ClientSizeProperty)
            {
                CaptureNormalBounds(window);
            }
        };
    }

    private void CaptureNormalBounds(Window window)
    {
        if (window.WindowState != WindowState.Minimized)
        {
            _lastNonMinimizedMaximized = window.WindowState == WindowState.Maximized;
        }

        if (window.WindowState == WindowState.Normal && CurrentBounds(window) is { Width: > 0, Height: > 0 } bounds)
        {
            _lastNormalBounds = bounds;
        }
    }

    /// The outer window rectangle in device-independent units, which is what WPF's Left/Top/Width/Height report.
    private static Rect? CurrentBounds(Window window)
    {
        double scale = DesktopScaling(window);
        Size frame = window.FrameSize ?? window.Bounds.Size;
        if (!double.IsFinite(frame.Width) || !double.IsFinite(frame.Height))
        {
            return null;
        }

        return new Rect(window.Position.X / scale, window.Position.Y / scale, frame.Width, frame.Height);
    }

    private static PixelPoint ToPixels(Window window, double left, double top)
    {
        double scale = DesktopScaling(window);
        return new PixelPoint((int)Math.Round(left * scale), (int)Math.Round(top * scale));
    }

    private static double DesktopScaling(Window window)
    {
        double scaling = window.DesktopScaling;
        return scaling > 0 ? scaling : 1;
    }

    /// True if the rectangle (DIPs, converted with the system DPI) intersects some monitor's work area by a usable amount.
    private static bool IsOnScreen(WindowPlacement placement)
    {
        if (!double.IsFinite(placement.Left) || !double.IsFinite(placement.Top)
            || !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height))
        {
            return false;
        }

        uint dpi = NativeMethods.GetDpiForSystem();
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var rect = new NativeMethods.RECT(
            (int)Math.Round(placement.Left * scale),
            (int)Math.Round(placement.Top * scale),
            (int)Math.Round((placement.Left + placement.Width) * scale),
            (int)Math.Round((placement.Top + placement.Height) * scale));

        nint monitor = NativeMethods.MonitorFromRect(in rect, NativeMethods.MONITOR_DEFAULTTONULL);
        if (monitor == 0)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        NativeMethods.RECT work = info.rcWork;
        int visibleWidth = Math.Min(rect.Right, work.Right) - Math.Max(rect.Left, work.Left);
        int visibleHeight = Math.Min(rect.Bottom, work.Bottom) - Math.Max(rect.Top, work.Top);
        return visibleWidth >= MinVisibleWidthPx && visibleHeight >= MinVisibleHeightPx
               && rect.Top >= work.Top - 8;   // the title bar must be reachable
    }
}
