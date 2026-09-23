using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using MdReader.Edge;
using MdReader.Ui.Spike;
using MdReader.Ui.WebView;
using MdReader.Core.Diagnostics;
using MdReader.Core.Settings;
using MdReader.Core.Theming;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Ui.Views;

/// <summary>
/// The spike's only window: an Avalonia chrome strip, the native web view, and two probes for the questions phase 1
/// exists to answer — an in-window overlay (airspace) and keystroke logging on both sides of the native boundary.
/// </summary>
internal sealed class MainWindow : Window
{
    private const string Category = "MainWindow";

    private readonly SpikeContext _context;
    private readonly IAppLog _log;
    private readonly WebView2Host _webViewHost;
    private readonly Border _overlay;
    private readonly Popup _popupProbe;
    private readonly TextBlock _status;
    private readonly SpikeSession _session;

    private WebViewChannel? _channel;
    private ThemePreference _preference;
    private bool _captureStarted;

    public MainWindow(SpikeContext context)
    {
        _context = context;
        _log = context.Log;
        _preference = context.Options.ThemeOverride ?? ThemePreference.System;

        Title = "MdReader (Avalonia spike)";
        Width = 1280;
        Height = 900;
        MinWidth = 400;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Capture mode must not take focus from whatever the user is doing.
        if (context.IsCapture)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
        }

        _session = new SpikeSession(context.DocumentPath, () => EffectiveTheme, _log);
        _session.ExternalLinkRequested += (_, uri) => OpenExternally(uri);

        _webViewHost = new WebView2Host(_log, context.Environment.GetAsync);
        _webViewHost.CoreWebView2Ready += OnCoreWebView2Ready;
        _webViewHost.InitializationFailed += OnWebViewInitializationFailed;
        _webViewHost.AcceleratorKeyPressed += OnAcceleratorKeyPressed;

        _status = new TextBlock
        {
            Text = "Starting the WebView2 environment…",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
            FontSize = 12,
        };

        _overlay = new Border
        {
            // Airspace probe: an ordinary Avalonia control in the same cell as the NativeControlHost, drawn after it.
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x6F, 0xEB)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 24, 24, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,   // shown once the page is up, so the probes sit over real content
            Child = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                MaxWidth = 340,
                TextWrapping = TextWrapping.Wrap,
                Text = "Avalonia overlay (airspace probe).\nIf you can read this over the document, in-window overlays work.",
            },
        };

        _popupProbe = new Popup
        {
            Placement = PlacementMode.Center,
            VerticalOffset = 160,
            IsLightDismissEnabled = false,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x8B, 0x20, 0x9E)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12),
                Child = new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 14,
                    MaxWidth = 340,
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Avalonia Popup probe (its own HWND).\nFind bars and toasts can use this if in-window overlays lose.",
                },
            },
        };

        var chrome = new Border
        {
            Height = 36,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
            Child = _status,
        };

        var content = new Grid();
        content.Children.Add(_webViewHost);
        content.Children.Add(_overlay);
        content.Children.Add(_popupProbe);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        Grid.SetRow(chrome, 0);
        Grid.SetRow(content, 1);
        root.Children.Add(chrome);
        root.Children.Add(content);
        Content = root;

        _popupProbe.PlacementTarget = content;

        // Tunnel: this is the Avalonia side of the keyboard question. Anything logged here reached the shell;
        // anything that only shows up in the AcceleratorKeyPressed log never did.
        AddHandler(KeyDownEvent, OnAvaloniaKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private AppTheme EffectiveTheme
    {
        get
        {
            PlatformThemeVariant os = PlatformSettings?.GetColorValues().ThemeVariant ?? PlatformThemeVariant.Light;
            return ThemeResolver.Resolve(_preference, os == PlatformThemeVariant.Light);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RequestedThemeVariant = EffectiveTheme == AppTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (PlatformSettings is { } settings)
        {
            settings.ColorValuesChanged += OnColorValuesChanged;
        }

        _log.Write(AppLogLevel.Info, Category,
            $"Window opened: {Width}x{Height} DIP, render scaling {RenderScaling:0.###}, OS theme {EffectiveTheme}.");
    }

    protected override void OnClosed(EventArgs e)
    {
        if (PlatformSettings is { } settings)
        {
            settings.ColorValuesChanged -= OnColorValuesChanged;
        }

        _channel?.Dispose();
        _webViewHost.Shutdown();
        base.OnClosed(e);
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues values)
    {
        AppTheme theme = EffectiveTheme;
        _log.Write(AppLogLevel.Info, Category, $"The OS theme changed; the effective theme is now {theme}.");
        RequestedThemeVariant = theme == AppTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (_webViewHost.Controller is { } controller)
        {
            controller.DefaultBackgroundColor = WebViewSecurity.PageBackgroundColor(theme);
            WebViewSecurity.ApplyPreferredColorScheme(controller.CoreWebView2, theme, _log);
        }

        _session.PostTheme();
    }

    private void OnCoreWebView2Ready(object? sender, CoreWebView2ReadyEventArgs e) => _ = InitializeChannelAsync(e);

    private async Task InitializeChannelAsync(CoreWebView2ReadyEventArgs e)
    {
        try
        {
            var channel = new WebViewChannel(e.Controller, _context.Paths.WebRoot, _log);
            _channel = channel;
            channel.PageFailed += (_, detail) => SetStatus("The viewer page failed: " + detail);
            _session.Attach(channel);
            channel.Ready += (_, _) => OnPageReady();

            string resourceRoot = await _session.ResourceRootTask;
            channel.Initialize(resourceRoot, EffectiveTheme, _session.RouteLink);
            SetStatus($"Loading {Path.GetFileName(_context.DocumentPath)}…");
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "Wiring the web view failed.", ex);
            SetStatus("Wiring the web view failed: " + ex.Message);
        }
    }

    private void OnPageReady()
    {
        SetStatus($"{_context.DocumentPath}  ·  Ctrl+F toggles the Avalonia overlay, Esc hides it.");
        if (!_context.IsCapture)
        {
            SetOverlayVisible(true);
            _webViewHost.FocusWebView();
        }

        if (_context.IsCapture && !_captureStarted)
        {
            _captureStarted = true;
            _ = CaptureAsync();
        }
        else if (KeyProbe.IsEnabled)
        {
            _ = RunKeyProbeAsync();
        }
    }

    private async Task RunKeyProbeAsync()
    {
        try
        {
            foreach (Screen screen in Screens.All)
            {
                _log.Write(AppLogLevel.Info, "Probe",
                    $"Screen {screen.DisplayName}: {screen.Bounds.Width}x{screen.Bounds.Height} px, scaling {screen.Scaling:0.###}, primary {screen.IsPrimary}.");
            }

            // Resize probe: Avalonia sizes the child HWND, the child's WM_SIZE re-applies CoreWebView2Controller.Bounds.
            foreach ((int width, int height) in new[] { (1000, 700), (1440, 1000), (900, 600) })
            {
                Width = width;
                Height = height;
                await Task.Delay(400);
                System.Drawing.Rectangle bounds = _webViewHost.Controller?.Bounds ?? default;
                _log.Write(AppLogLevel.Info, "Probe",
                    $"Window {width}x{height} DIP at scaling {RenderScaling:0.###} → controller bounds {bounds.Width}x{bounds.Height} px.");
            }

            nint window = TryGetPlatformHandle()?.Handle ?? 0;
            await KeyProbe.RunAsync(window, _webViewHost.HostHandle, _log);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "The key probe failed.", ex);
        }
        finally
        {
            _context.Shutdown(SpikeExitCodes.Success);
        }
    }

    private void OnWebViewInitializationFailed(object? sender, Exception exception)
    {
        string message = WebViewEnvironmentFactory.DescribeFailure(exception);
        Console.Error.WriteLine(message);
        SetStatus(message);
        _context.Shutdown(SpikeExitCodes.WebViewUnavailable);
    }

    /// <summary>--capture: wait for 'rendered{phase:"enhanced"}', write the PNG, exit 0.</summary>
    private async Task CaptureAsync()
    {
        string path = _context.Options.CapturePath!;
        string temp = path + ".tmp";
        try
        {
            await _session.Enhanced.WaitAsync(SpikeContext.CaptureTimeout);

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            CoreWebView2 core = _channel!.Core;
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }

            if (new FileInfo(temp).Length == 0)
            {
                throw new InvalidOperationException("The captured image is empty.");
            }

            File.Move(temp, path, overwrite: true);
            _log.Write(AppLogLevel.Info, Category, $"Captured {_context.DocumentPath} to {path}.");
            _context.Shutdown(SpikeExitCodes.Success);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            _log.Write(AppLogLevel.Error, Category, $"Capture to {path} failed.", ex);
            Console.Error.WriteLine($"Capture failed: {ex.Message}");
            _context.Shutdown(SpikeExitCodes.CaptureFailed);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// The WebView2 side of the keyboard probe. This fires for keys pressed while focus is inside the native view,
    /// before the page sees them; it is the only hook a host shortcut can use there.
    /// </summary>
    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown))
        {
            return;
        }

        bool ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
        bool shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
        int key = (int)e.VirtualKey;
        _log.Write(AppLogLevel.Info, "Keyboard",
            $"WebView2 AcceleratorKeyPressed: vk 0x{key:X2}{(ctrl ? " +Ctrl" : string.Empty)}{(shift ? " +Shift" : string.Empty)} ({e.KeyEventKind}).");

        if (ctrl && key == NativeMethods.VK_F)
        {
            e.Handled = true;
            // Do the work after the event returns: the WebView2 event is raised from a nested message loop, so calling
            // back into layout or focus here is the deferred-action pattern the WPF shell already uses.
            Dispatcher.UIThread.Post(() => SetOverlayVisible(!_overlay.IsVisible));
        }
        else if (key == NativeMethods.VK_ESCAPE)
        {
            e.Handled = true;
            Dispatcher.UIThread.Post(() => SetOverlayVisible(false));
        }
    }

    private void OnAvaloniaKeyDown(object? sender, KeyEventArgs e) =>
        _log.Write(AppLogLevel.Info, "Keyboard", $"Avalonia KeyDown: {e.Key} (modifiers {e.KeyModifiers}, handled {e.Handled}).");

    private void SetOverlayVisible(bool visible)
    {
        _overlay.IsVisible = visible;
        _popupProbe.IsOpen = visible;
        _log.Write(AppLogLevel.Info, Category, $"Overlay probes {(visible ? "shown" : "hidden")}.");
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _log.Write(AppLogLevel.Info, Category, text);
    }

    private void OpenExternally(Uri uri)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            _log.Write(AppLogLevel.Info, Category, $"Launched {uri.AbsoluteUri}.");
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, $"Couldn't launch {uri.AbsoluteUri}.", ex);
        }
    }
}
