using MdReader.Edge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MdReader.App.Documents;

/// <summary>
/// The WPF half of one WebView2 view for <see cref="WebView2Channel"/>: the settings that live on the
/// <see cref="WebView2"/> control rather than on <see cref="CoreWebView2"/>.
/// </summary>
internal sealed class WpfWebViewSurface : IWebView2Surface
{
    private readonly WebView2 _control;

    public WpfWebViewSurface(WebView2 control)
    {
        _control = control;
        _control.ZoomFactorChanged += (sender, e) => ZoomFactorChanged?.Invoke(sender, e);
    }

    public double ZoomFactor
    {
        get => _control.ZoomFactor;
        set => _control.ZoomFactor = value;
    }

    public event EventHandler? ZoomFactorChanged;

    public async Task<CoreWebView2> EnsureCoreWebView2Async(CoreWebView2Environment environment)
    {
        await _control.EnsureCoreWebView2Async(environment);
        _control.AllowExternalDrop = true;   // lives on the WPF control, not on CoreWebView2
        return _control.CoreWebView2 ?? throw new InvalidOperationException("CoreWebView2 wasn't created.");
    }

    /// Works before the controller exists: the WPF control carries the value over to it.
    public void SetDefaultBackgroundColor(byte red, byte green, byte blue) =>
        _control.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, red, green, blue);
}
