using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using MdReader.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Ui.WebView;

/// <summary>Raised once the tab's <see cref="CoreWebView2"/> exists and is ready to be configured and navigated.</summary>
internal sealed class CoreWebView2ReadyEventArgs(CoreWebView2Controller controller) : EventArgs
{
    public CoreWebView2Controller Controller { get; } = controller;

    public CoreWebView2 Core { get; } = controller.CoreWebView2;
}

/// <summary>
/// Avalonia has no WebView control, so this is the seam the whole port rests on: a <see cref="NativeControlHost"/> that
/// owns a child HWND of its own and drives a <see cref="CoreWebView2Controller"/> inside it.
/// </summary>
/// <remarks>
/// <para>
/// The handle: Avalonia calls <see cref="CreateNativeControlCore"/> with the top level's HWND and expects an HWND back.
/// We create our own child window class rather than taking Avalonia's default child, because we need its WndProc:
/// Avalonia sizes the child with <c>MoveWindow</c> (device pixels, scaled by the top level), and the resulting WM_SIZE
/// is the only reliable signal for keeping <see cref="CoreWebView2Controller.Bounds"/> in sync. Avalonia raises no
/// per-attachment "your native control was resized" event, and a Bounds-property subscription would be one layout pass
/// behind and in DIPs rather than device pixels.
/// </para>
/// <para>
/// DPI: the controller's default <c>BoundsMode</c> is raw pixels, which is exactly what WM_SIZE reports, and
/// <c>RasterizationScale</c> is driven from the top level's <c>RenderScaling</c> instead of letting WebView2 track the
/// monitor on its own, so the page and the rest of the UI can never scale differently. The process must be
/// per-monitor-v2 aware (app.manifest) or Windows bitmap-stretches the child window.
/// </para>
/// <para>All members are Avalonia-UI-thread only, except the WndProc, which Windows calls on that same thread.</para>
/// </remarks>
internal sealed class WebView2Host : NativeControlHost
{
    private const string Category = "WebView2Host";

    private static readonly ConcurrentDictionary<nint, WebView2Host> Hosts = new();

    private readonly IAppLog _log;
    private readonly Func<Task<CoreWebView2Environment>> _environmentFactory;

    private nint _hwnd;
    private CoreWebView2Controller? _controller;
    private bool _destroyed;

    public WebView2Host(IAppLog log, Func<Task<CoreWebView2Environment>> environmentFactory)
    {
        _log = log;
        _environmentFactory = environmentFactory;
    }

    /// <summary>The controller is created asynchronously after the control is attached to a window.</summary>
    public event EventHandler<CoreWebView2ReadyEventArgs>? CoreWebView2Ready;

    /// <summary>Failure to create the environment or the controller (WebView2 runtime missing, etc.).</summary>
    public event EventHandler<Exception>? InitializationFailed;

    /// <summary>
    /// Every key the page sees, before the page sees it. This is the only route by which keystrokes typed inside the
    /// native web view can reach the Avalonia shell: Avalonia's own <c>KeyDown</c> never fires while the native child
    /// HWND has focus.
    /// </summary>
    public event EventHandler<CoreWebView2AcceleratorKeyPressedEventArgs>? AcceleratorKeyPressed;

    /// <summary>The child HWND the controller lives in (0 before the control is attached).</summary>
    public nint HostHandle => _hwnd;

    public CoreWebView2Controller? Controller => _controller;

    public CoreWebView2? Core => _controller?.CoreWebView2;

    /// <summary>Moves keyboard focus into the page. Never called in capture mode (it would activate the window).</summary>
    public void FocusWebView()
    {
        try
        {
            _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "MoveFocus failed.", ex);
        }
    }

    protected override unsafe IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (_hwnd != 0)
        {
            // Avalonia destroys and recreates the native control when the host moves between top levels. The spike has
            // one window, so this is a "would break in the real shell" marker rather than a handled case.
            _log.Write(AppLogLevel.Warning, Category, "The native control was created a second time; the WebView2 controller is not re-parented.");
            return new PlatformHandle(_hwnd, "HWND");
        }

        _hwnd = NativeMethods.CreateHostWindow(parent.Handle, &StaticWndProc);
        Hosts[_hwnd] = this;
        _log.Write(AppLogLevel.Info, Category,
            $"Host window 0x{_hwnd:X} created under parent 0x{parent.Handle:X} ({parent.HandleDescriptor}); DPI {NativeMethods.GetDpiForWindow(_hwnd)}.");

        _ = CreateControllerAsync();
        return new PlatformHandle(_hwnd, "HWND");
    }

    /// <summary>
    /// Tears the native side down from the window's <c>Closed</c>. Avalonia does not call
    /// <see cref="DestroyNativeControlCore"/> when the application shuts down, and leaving the controller open makes
    /// the browser process complain on the way out ("Failed to unregister class Chrome_WidgetWin_0").
    /// </summary>
    public void Shutdown() => DestroyNativeControlCore(new PlatformHandle(_hwnd, "HWND"));

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_destroyed)
        {
            return;
        }

        _destroyed = true;
        CoreWebView2Controller? controller = _controller;
        _controller = null;
        if (controller is not null)
        {
            try
            {
                controller.Close();   // closes the browser/render processes for this controller
            }
            catch (Exception ex)
            {
                _log.Write(AppLogLevel.Warning, Category, "Closing the WebView2 controller failed.", ex);
            }
        }

        nint hwnd = _hwnd;
        _hwnd = 0;
        if (hwnd != 0)
        {
            Hosts.TryRemove(hwnd, out _);
            NativeMethods.DestroyWindow(hwnd);
        }

        _log.Write(AppLogLevel.Info, Category, $"Host window 0x{hwnd:X} destroyed.");
    }

    private async Task CreateControllerAsync()
    {
        nint hwnd = _hwnd;
        try
        {
            CoreWebView2Environment environment = await _environmentFactory().ConfigureAwait(true);
            if (_destroyed || _hwnd != hwnd)
            {
                return;
            }

            CoreWebView2Controller controller = await environment.CreateCoreWebView2ControllerAsync(hwnd).ConfigureAwait(true);
            if (_destroyed || _hwnd != hwnd)
            {
                controller.Close();
                return;
            }

            _controller = controller;

            // One source of truth for scale. WebView2 would happily track the monitor itself, but then Avalonia's
            // RenderScaling and the page's RasterizationScale are two independent values that agree only by accident
            // (they diverge as soon as Avalonia's scaling is overridden, or its DPI awareness differs from ours).
            // Driving the scale from the top level is also the shape that ports: on macOS WKWebView follows the
            // window's backingScaleFactor.
            try
            {
                controller.ShouldDetectMonitorScaleChanges = false;
            }
            catch (NotImplementedException)
            {
                _log.Write(AppLogLevel.Warning, Category, "This WebView2 runtime can't turn off monitor scale detection.");
            }

            ApplyScale();
            controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
            SyncBounds();
            controller.IsVisible = true;
            _log.Write(AppLogLevel.Info, Category,
                $"Controller ready; bounds {controller.Bounds.Width}x{controller.Bounds.Height} px, rasterization scale {controller.RasterizationScale:0.###}.");

            CoreWebView2Ready?.Invoke(this, new CoreWebView2ReadyEventArgs(controller));
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "Creating the WebView2 controller failed.", ex);
            InitializationFailed?.Invoke(this, ex);
        }
    }

    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        try
        {
            AcceleratorKeyPressed?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "An AcceleratorKeyPressed handler failed.", ex);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.ScalingChanged += OnScalingChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.ScalingChanged -= OnScalingChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        ApplyScale();
        SyncBounds();
    }

    private void ApplyScale()
    {
        CoreWebView2Controller? controller = _controller;
        if (controller is null || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        try
        {
            if (Math.Abs(controller.RasterizationScale - topLevel.RenderScaling) > 0.001)
            {
                controller.RasterizationScale = topLevel.RenderScaling;
                _log.Write(AppLogLevel.Info, Category, $"Rasterization scale set to {topLevel.RenderScaling:0.###}.");
            }
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Setting the rasterization scale failed.", ex);
        }
    }

    private void SyncBounds()
    {
        CoreWebView2Controller? controller = _controller;
        if (controller is null || _hwnd == 0)
        {
            return;
        }

        (int width, int height) = NativeMethods.GetClientSize(_hwnd);
        try
        {
            controller.Bounds = new Rectangle(0, 0, width, height);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, $"Setting the WebView2 bounds to {width}x{height} failed.", ex);
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_SIZE:
                SyncBounds();
                break;

            case NativeMethods.WM_DPICHANGED_AFTERPARENT:
                // The child follows the top level to another monitor. Avalonia also raises ScalingChanged and a WM_SIZE
                // follows, but the order isn't guaranteed, so re-sync here as well; all three paths are idempotent.
                ApplyScale();
                SyncBounds();
                _log.Write(AppLogLevel.Info, Category,
                    $"DPI changed to {NativeMethods.GetDpiForWindow(hwnd)}; rasterization scale {_controller?.RasterizationScale:0.###}.");
                break;

            case NativeMethods.WM_WINDOWPOSCHANGED:
                // Dialog placement and IME windows are positioned relative to the parent; WebView2 caches that.
                try
                {
                    _controller?.NotifyParentWindowPositionChanged();
                }
                catch (Exception ex)
                {
                    _log.Write(AppLogLevel.Debug, Category, "NotifyParentWindowPositionChanged failed.", ex);
                }

                break;

            case NativeMethods.WM_SETFOCUS:
                FocusWebView();
                break;
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // Messages sent while CreateWindowExW is still running arrive before the dictionary entry exists; the window is
        // 1x1 and has no controller then, so the default handling is correct.
        if (Hosts.TryGetValue(hwnd, out WebView2Host? host))
        {
            try
            {
                return host.WndProc(hwnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                // A managed exception must never unwind into Windows' message dispatch.
                Dispatcher.UIThread.Post(() => host._log.Write(AppLogLevel.Error, Category, $"WndProc failed for message 0x{msg:X}.", ex));
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
