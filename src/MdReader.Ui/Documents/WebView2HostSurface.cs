using System.Drawing;
using MdReader.Edge;
using MdReader.Ui.WebView;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Ui.Documents;

/// <summary>
/// The Avalonia half of one WebView2 view for <see cref="WebView2Channel"/>. Where WPF has a control that owns the
/// controller, the Avalonia shell owns the <see cref="CoreWebView2Controller"/> itself, inside the native host window
/// of <see cref="WebView2Host"/>.
/// </summary>
internal sealed class WebView2HostSurface : IWebView2Surface
{
    private readonly WebView2Host _host;
    private Color _background = Color.White;
    private double _zoomFactor = 1;
    private CoreWebView2Controller? _controller;

    public WebView2HostSurface(WebView2Host host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>
    /// The zoom is a global setting, so it is set before the tab's controller exists; like the background colour it is
    /// remembered here and applied at creation, so the page never renders at the wrong size first.
    /// </summary>
    public double ZoomFactor
    {
        get => _controller?.ZoomFactor ?? _zoomFactor;
        set
        {
            _zoomFactor = value;
            if (_controller is { } controller)
            {
                controller.ZoomFactor = value;
            }
        }
    }

    public event EventHandler? ZoomFactorChanged;

    public async Task<CoreWebView2> EnsureCoreWebView2Async(CoreWebView2Environment environment)
    {
        CoreWebView2Controller controller = await _host.CreateControllerAsync(environment, _background);
        _controller = controller;
        controller.ZoomFactor = _zoomFactor;
        controller.ZoomFactorChanged += OnZoomFactorChanged;
        return controller.CoreWebView2;
    }

    /// <summary>
    /// Before the controller exists the value is remembered and handed to it at creation, so the first frame it paints
    /// already has the page background. Until then the host window shows what Avalonia drew underneath it, which is the
    /// same colour (§10).
    /// </summary>
    public void SetDefaultBackgroundColor(byte red, byte green, byte blue)
    {
        _background = Color.FromArgb(255, red, green, blue);
        if (_controller is { } controller)
        {
            controller.DefaultBackgroundColor = _background;
        }
    }

    private void OnZoomFactorChanged(object? sender, object e) => ZoomFactorChanged?.Invoke(this, EventArgs.Empty);
}
