using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using MdReader.Core.Diagnostics;
using MdReader.Core.Paths;
using MdReader.Core.Theming;
using MdReader.Mac;
using MdReader.Shell.Documents;
using MdReader.Shell.ViewModels;
using MdReader.Ui.Documents;

namespace MdReader.Ui.Platform.Mac;

/// <summary>
/// <see cref="IDocumentWebViewHost"/> over the WKWebView backend: a <see cref="NativeControlHost"/> whose native
/// control is an <c>NSView</c>, and a <see cref="WkWebViewChannel"/> whose web view lives inside it.
/// </summary>
/// <remarks>
/// <para>The container is created on attach and never re-parented, exactly as the Windows host window is. The web
/// view itself is created when the channel initializes and replaced on recovery, which is why the container exists
/// at all: it gives Avalonia something stable to size, and it is painted the page background so nothing flashes
/// white before the first frame (§10).</para>
/// <para>No sizing code: the web view is added with an autoresizing mask, so AppKit keeps it filling the container.
/// That is the whole of what the Windows host needs a WndProc and WM_SIZE for.</para>
/// </remarks>
internal sealed class WkDocumentWebViewHost : IDocumentWebViewHost
{
    private const string Category = "WkWebViewHost";

    private readonly DocumentViewServices _services;
    private readonly ContainerHost _container;

    internal WkDocumentWebViewHost(DocumentViewServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        (byte red, byte green, byte blue) = ThemePalette.PageBackground(services.Theme.EffectiveTheme);
        _container = new ContainerHost(services.Log, red, green, blue);
    }

    public Control Control => _container;

    /// <summary>
    /// Never raised: AppKit offers a key equivalent to the menu bar before the first responder, so every §4.12
    /// shortcut is taken by the native menu whether focus is in the page or in the shell's own chrome.
    /// </summary>
    public event EventHandler<ForwardedKeyEventArgs>? AcceleratorKeyPressed
    {
        add { }
        remove { }
    }

    public event EventHandler? WebViewFocused
    {
        add => _container.WebViewFocused += value;
        remove => _container.WebViewFocused -= value;
    }

    public IHostedWebViewChannel CreateChannel() =>
        new WkWebViewChannel(_container, _services.Theme, _services.Paths, _services.Perf, _services.Log, _services.Time,
                             PhysicalFileSystemProbe.Instance);

    public async Task InitializeAsync(IHostedWebViewChannel channel, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var provider = (WkWebViewEnvironmentProvider)_services.Environment;
        await ((WkWebViewChannel)channel).InitializeAsync(provider.Get(), resourceRoot);
    }

    public void DestroyWebView() => _container.DestroyWebView();

    public void FocusWebView() => _container.FocusWebView();

    public void Shutdown() => _container.Shutdown();

    public void Dispose() => _container.Shutdown();

    /// <summary>
    /// The Avalonia native control and the channel's surface in one object: Avalonia asks it for an NSView, the
    /// channel asks it where to put the web view, and both are the same container.
    /// </summary>
    private sealed class ContainerHost : NativeControlHost, IWkWebViewSurface
    {
        private readonly IAppLog _log;
        private readonly TaskCompletionSource<nint> _containerReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private nint _container;
        private nint _webView;
        private double _zoomFactor = 1;
        private byte _red;
        private byte _green;
        private byte _blue;
        private bool _shutdown;

        internal ContainerHost(IAppLog log, byte red, byte green, byte blue)
        {
            _log = log;
            _red = red;
            _green = green;
            _blue = blue;
        }

        internal event EventHandler? WebViewFocused;

        /// <summary>
        /// WKWebView's page zoom. Held here until the view exists, so a tab that opens while the setting is 130%
        /// never renders at 100% first.
        /// </summary>
        public double ZoomFactor
        {
            get => _zoomFactor;
            set
            {
                _zoomFactor = value;
                if (_webView != 0)
                {
                    MacNativeView.SetPageZoom(_webView, value);
                }
            }
        }

        /// <summary>Never raised: WKWebView has no page-zoom gesture to report (see <see cref="WkWebViewChannel"/>).</summary>
        public event EventHandler? ZoomFactorChanged
        {
            add { }
            remove { }
        }

        public Task<nint> GetContainerAsync() => _containerReady.Task;

        public void AttachWebView(nint webView)
        {
            if (_shutdown || _container == 0)
            {
                MacNativeView.Release(webView);
                return;
            }

            _webView = webView;
            MacNativeView.AddFillingSubview(_container, webView);
            MacNativeView.SetPageZoom(webView, _zoomFactor);
            _log.Write(AppLogLevel.Info, Category, "Web view attached to its container.");

            // AppKit gives no "the web view took focus" notification; the print-mode fallback (R6) settles for the
            // window becoming key, which the document view already watches, so this is raised once on attach to
            // match the Windows host's first WM_SETFOCUS.
            WebViewFocused?.Invoke(this, EventArgs.Empty);
        }

        public void SetDefaultBackgroundColor(byte red, byte green, byte blue)
        {
            _red = red;
            _green = green;
            _blue = blue;
            MacNativeView.SetBackgroundColor(_container, red, green, blue);
        }

        public void FocusWebView()
        {
            if (_webView != 0)
            {
                MacNativeView.MakeFirstResponder(_webView);
            }
        }

        internal void DestroyWebView()
        {
            nint webView = _webView;
            _webView = 0;
            MacNativeView.Release(webView);
        }

        internal void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            DestroyWebView();
            nint container = _container;
            _container = 0;
            _containerReady.TrySetException(new OperationCanceledException("The document view was closed before its web view existed."));
            MacNativeView.Release(container);
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            ArgumentNullException.ThrowIfNull(parent);
            if (_container != 0)
            {
                // Avalonia recreates the native control when the host moves between top levels. A document view lives
                // in exactly one window, so this can only mean a bug elsewhere.
                _log.Write(AppLogLevel.Warning, Category, "The native control was created a second time; the web view is not re-parented.");
                return new PlatformHandle(_container, "NSView");
            }

            _container = MacNativeView.CreateContainer(_red, _green, _blue);
            _containerReady.TrySetResult(_container);
            return new PlatformHandle(_container, "NSView");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control) => Shutdown();
    }
}
