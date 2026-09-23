using Avalonia.Controls;
using MdReader.Edge;
using MdReader.Shell.Commands;
using MdReader.Shell.Documents;
using MdReader.Shell.ViewModels;
using MdReader.Ui.Commands;
using MdReader.Ui.Documents;
using MdReader.Ui.WebView;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Ui.Platform.Windows;

/// <summary>
/// <see cref="IDocumentWebViewHost"/> over the WebView2 backend: the native host window of
/// <see cref="WebView2Host"/> and a <see cref="WebView2Channel"/> on the controller inside it. This is the shape
/// phase 1 shipped, lifted out of the document view unchanged so the same view can drive WKWebView as well.
/// </summary>
internal sealed class EdgeDocumentWebViewHost : IDocumentWebViewHost
{
    private readonly DocumentViewServices _services;
    private readonly WebView2Host _host;

    internal EdgeDocumentWebViewHost(DocumentViewServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _host = new WebView2Host(services.Log);
        _host.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
        _host.WebViewFocused += OnWebViewFocused;
    }

    public Control Control => _host;

    public event EventHandler<ForwardedKeyEventArgs>? AcceleratorKeyPressed;

    public event EventHandler? WebViewFocused;

    public IHostedWebViewChannel CreateChannel() =>
        new WebView2Channel(new WebView2HostSurface(_host), _services.Theme, _services.Paths, _services.Perf, _services.Log,
                            _services.Time);

    public async Task InitializeAsync(IHostedWebViewChannel channel, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // The shared layer only knows IWebViewEnvironmentProvider; the WebView2 environment itself comes from the
        // backend's own contract, which the composition root registers for the same singleton.
        var provider = (IWebView2EnvironmentProvider)_services.Environment;
        CoreWebView2Environment environment = await provider.GetAsync();
        await ((WebView2Channel)channel).InitializeAsync(environment, resourceRoot);
    }

    public void DestroyWebView() => _host.DestroyController();

    public void FocusWebView() => _host.FocusWebView();

    public void Shutdown() => _host.Shutdown();

    public void Dispose()
    {
        _host.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;
        _host.WebViewFocused -= OnWebViewFocused;
    }

    /// <summary>
    /// Raised synchronously with the browser process blocked, so nothing here may call a CoreWebView2 API: the key is
    /// translated and handed on, and whether it was taken is copied straight back (§4.12).
    /// </summary>
    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (AcceleratorKeyPressed is not { } handler)
        {
            return;
        }

        bool isKeyUp = e.KeyEventKind is CoreWebView2KeyEventKind.KeyUp or CoreWebView2KeyEventKind.SystemKeyUp;
        var args = new ForwardedKeyEventArgs(AvaloniaShortcutRouter.ToShortcutKey((int)e.VirtualKey), isKeyUp);
        handler(this, args);
        if (args.Handled)
        {
            e.Handled = true;
        }
    }

    private void OnWebViewFocused(object? sender, EventArgs e) => WebViewFocused?.Invoke(this, EventArgs.Empty);
}
