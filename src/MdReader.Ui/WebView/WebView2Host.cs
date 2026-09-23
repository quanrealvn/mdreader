using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MdReader.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Ui.WebView;

/// <summary>
/// Avalonia has no WebView control, so this is the seam the whole port rests on: a <see cref="NativeControlHost"/> that
/// owns a child HWND of its own and drives a <see cref="CoreWebView2Controller"/> inside it. One per tab, created once
/// and never re-parented (§4.11).
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
    private readonly TaskCompletionSource<nint> _hostWindowReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private nint _hwnd;
    private CoreWebView2Controller? _controller;
    private bool _destroyed;

    public WebView2Host(IAppLog log)
    {
        _log = log;
    }

    /// <summary>
    /// Every key the page sees, before the page sees it. This is the only route by which keystrokes typed inside the
    /// native web view can reach the Avalonia shell: Avalonia's own <c>KeyDown</c> never fires while the native child
    /// HWND has focus. Raised synchronously with the browser process blocked, so handlers must defer their work (§4.12).
    /// </summary>
    public event EventHandler<CoreWebView2AcceleratorKeyPressedEventArgs>? AcceleratorKeyPressed;

    /// <summary>
    /// Keyboard focus moved into the native web view. Avalonia never sees this, because the child HWND takes the focus
    /// away from the top level; the print-mode fallback (R6) needs it.
    /// </summary>
    public event EventHandler? WebViewFocused;

    public CoreWebView2Controller? Controller => _controller;

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

    /// <summary>
    /// Creates the controller once the host window exists. Also used to build a replacement after the browser process
    /// exited, which leaves the old controller closed for good (§4.10).
    /// </summary>
    /// <param name="background">
    /// The colour the view paints before the page has drawn anything; set before the controller becomes visible, so
    /// nothing flashes white (§10).
    /// </param>
    public async Task<CoreWebView2Controller> CreateControllerAsync(CoreWebView2Environment environment, Color background)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (_controller is not null)
        {
            throw new InvalidOperationException("This host already has a WebView2 controller.");
        }

        nint hwnd = await _hostWindowReady.Task.ConfigureAwait(true);
        ObjectDisposedException.ThrowIf(_destroyed, this);

        CoreWebView2Controller controller = await environment.CreateCoreWebView2ControllerAsync(hwnd).ConfigureAwait(true);
        if (_destroyed || _hwnd != hwnd)
        {
            controller.Close();
            throw new OperationCanceledException("The document view was closed while its WebView was being created.");
        }

        _controller = controller;
        controller.DefaultBackgroundColor = background;

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

        try
        {
            controller.AllowExternalDrop = true;   // files dropped onto the page (§4.11)
        }
        catch (NotImplementedException)
        {
            _log.Write(AppLogLevel.Warning, Category, "This WebView2 runtime can't accept external drops.");
        }

        ApplyScale();
        controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
        SyncBounds();
        controller.IsVisible = true;
        _log.Write(AppLogLevel.Info, Category,
            $"Controller ready; bounds {controller.Bounds.Width}x{controller.Bounds.Height} px, rasterization scale {controller.RasterizationScale:0.###}.");
        return controller;
    }

    /// <summary>Closes the controller but keeps the host window, so a replacement can be created in place.</summary>
    public void DestroyController()
    {
        CoreWebView2Controller? controller = _controller;
        _controller = null;
        if (controller is null)
        {
            return;
        }

        try
        {
            controller.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;
            controller.Close();   // closes the browser/render processes for this controller
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Closing the WebView2 controller failed.", ex);
        }
    }

    /// <summary>
    /// Tears the native side down from the tab's Dispose. Avalonia does not call
    /// <see cref="DestroyNativeControlCore"/> when the application shuts down, and leaving the controller open makes
    /// the browser process complain on the way out ("Failed to unregister class Chrome_WidgetWin_0").
    /// </summary>
    public void Shutdown() => DestroyNativeControlCore(new PlatformHandle(_hwnd, "HWND"));

    protected override unsafe IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (_hwnd != 0)
        {
            // Avalonia destroys and recreates the native control when the host moves between top levels. A document
            // view lives in exactly one window, so this can only mean a bug elsewhere.
            _log.Write(AppLogLevel.Warning, Category, "The native control was created a second time; the WebView2 controller is not re-parented.");
            return new PlatformHandle(_hwnd, "HWND");
        }

        _hwnd = NativeMethods.CreateHostWindow(parent.Handle, &StaticWndProc);
        Hosts[_hwnd] = this;
        _log.Write(AppLogLevel.Debug, Category,
            $"Host window 0x{_hwnd:X} created under parent 0x{parent.Handle:X} ({parent.HandleDescriptor}); DPI {NativeMethods.GetDpiForWindow(_hwnd)}.");

        _hostWindowReady.TrySetResult(_hwnd);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_destroyed)
        {
            return;
        }

        _destroyed = true;
        _hostWindowReady.TrySetException(new OperationCanceledException("The document view was closed before its WebView existed."));
        DestroyController();

        nint hwnd = _hwnd;
        _hwnd = 0;
        if (hwnd != 0)
        {
            Hosts.TryRemove(hwnd, out _);
            NativeMethods.DestroyWindow(hwnd);
        }

        _log.Write(AppLogLevel.Debug, Category, $"Host window 0x{hwnd:X} destroyed.");
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
                WebViewFocused?.Invoke(this, EventArgs.Empty);
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
