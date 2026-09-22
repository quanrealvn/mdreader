using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MdReader.App.Commands;
using MdReader.App.Services;
using MdReader.App.ViewModels;
using MdReader.Core.Cli;
using Microsoft.Web.WebView2.Core;

namespace MdReader.App.Views;

/// The single top-level window. View-only logic lives here: dropdown menus, drag-and-drop onto the WPF chrome, the status
/// toast popup and the About dialog. Everything else is bound to MainViewModel.
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IThemeService _theme;
    private readonly CommandLineOptions _options;
    private readonly HashSet<ContextMenu> _wiredMenus = [];

    public MainWindow(MainViewModel viewModel, KeyboardShortcuts shortcuts, IThemeService theme, WindowPlacementService placement,
                      CommandLineOptions options)
    {
        _viewModel = viewModel;
        _theme = theme;
        _options = options;

        InitializeComponent();
        DataContext = viewModel;

        shortcuts.Attach(this);
        theme.AttachWindow(this);
        placement.Attach(this);

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        LocationChanged += (_, _) => RepositionStatusPopup();
        SizeChanged += (_, _) => RepositionStatusPopup();
        StateChanged += (_, _) => UpdateStatusPopup();
        IsVisibleChanged += (_, _) => UpdateStatusPopup();
        Activated += (_, _) => UpdateStatusPopup();     // re-shows a toast that is still within its 4 s
        Deactivated += (_, _) => UpdateStatusPopup();   // WPF popups are topmost: never float over other apps
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            StatusPopup.IsOpen = false;
        };
    }

    // ----- Menus -----

    private void OnMoreButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("MoreMenu");
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private void OnRecentButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("RecentMenu");
        menu.Items.Clear();
        var recent = _viewModel.RecentFiles.OfType<RecentFileEntry>().ToList();
        if (recent.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No recent files", IsEnabled = false });
        }
        else
        {
            foreach (var entry in recent)
            {
                menu.Items.Add(CreateRecentMenuItem(entry));
            }

            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "_Clear recent files", Command = _viewModel.ClearRecentFilesCommand };
            AutomationProperties.SetAutomationId(clear, "ClearRecentMenuItem");
            menu.Items.Add(clear);
        }

        OpenMenu(menu, (FrameworkElement)sender);
    }

    private MenuItem CreateRecentMenuItem(RecentFileEntry entry)
    {
        var folder = new TextBlock
        {
            Text = entry.Folder,
            FontSize = 12,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 440,
        };
        folder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Foreground.Muted");
        var header = new StackPanel { Margin = new Thickness(0, 3, 0, 4) };
        header.Children.Add(new TextBlock { Text = entry.FileName, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440 });
        header.Children.Add(folder);

        var icon = new TextBlock { Text = "\uE8A5" };
        icon.SetResourceReference(StyleProperty, "MenuIconStyle");

        var item = new MenuItem
        {
            Header = header,
            Icon = icon,
            Command = _viewModel.OpenRecentCommand,
            CommandParameter = entry.FullPath,
            ToolTip = entry.FullPath,
        };
        ToolTipService.SetInitialShowDelay(item, 900);
        AutomationProperties.SetAutomationId(item, "RecentMenuItem");
        AutomationProperties.SetName(item, entry.FullPath);
        return item;
    }

    private void OpenMenu(ContextMenu menu, FrameworkElement target)
    {
        if (_wiredMenus.Add(menu))
        {
            // A mouse click closes a WPF context menu, but a UI Automation Invoke on an item doesn't; close it explicitly so a
            // (topmost) menu never lingers after its command ran.
            menu.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler((_, _) => menu.IsOpen = false));
        }

        menu.DataContext = DataContext;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = AlignRightBelow;
        menu.IsOpen = true;
    }

    /// Right-aligns the menu's visible border with the button and opens it just below the chrome row. The menu template
    /// has an 8 px transparent margin (room for its shadow), which is compensated here. Sizes are in device pixels.
    private CustomPopupPlacement[] AlignRightBelow(Size popupSize, Size targetSize, Point offset)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var margin = 8 * dpi.DpiScaleX;
        var gap = 2 * dpi.DpiScaleY;
        return
        [
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + margin, targetSize.Height + gap), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(-margin, targetSize.Height + gap), PopupPrimaryAxis.Horizontal),
        ];
    }

    // ----- Drag and drop onto the WPF chrome (drops onto a WebView are handled by the document view) -----

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            return;
        }

        e.Handled = true;
        _viewModel.OpenDroppedFiles(files);
    }

    // ----- Status toast -----

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsStatusVisible) or nameof(MainViewModel.StatusText))
        {
            UpdateStatusPopup();
        }
        else if (e.PropertyName is nameof(MainViewModel.HasTabs) && IsInitialized && PresentationSource.FromVisual(this) is not null)
        {
            UpdateEmptyState();
        }
    }

    /// Runs inside Show(), after Program opened the command-line documents and before the first layout pass.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateEmptyState();
    }

    /// The empty state (with its recent-files list) is built only when it's first needed.
    private void UpdateEmptyState()
    {
        if (_viewModel.HasTabs)
        {
            EmptyStateHost.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyStateHost.Content ??= new EmptyState();
        EmptyStateHost.Visibility = Visibility.Visible;
    }

    /// The toast is a Popup (its own topmost window, so it can sit above the WebView2 HWND). It's shown only while the main
    /// window is active, visible and not minimized; a toast raised while inactive appears on activation if its 4 s are left.
    private void UpdateStatusPopup()
    {
        var show = _viewModel.IsStatusVisible && IsVisible && IsActive && WindowState != WindowState.Minimized;
        if (show && !StatusPopup.IsOpen)
        {
            StatusPopup.IsOpen = true;
        }
        else if (!show && StatusPopup.IsOpen)
        {
            StatusPopup.IsOpen = false;
        }
        else if (show)
        {
            RepositionStatusPopup();   // new text may change the size
        }
    }

    /// WPF popups don't follow their target when the window moves or resizes; nudging an offset forces a re-placement.
    private void RepositionStatusPopup()
    {
        if (!StatusPopup.IsOpen)
        {
            return;
        }

        var offset = StatusPopup.HorizontalOffset;
        StatusPopup.HorizontalOffset = offset + 1;
        StatusPopup.HorizontalOffset = offset;
    }

    private void OnStatusClicked(object sender, MouseButtonEventArgs e) => StatusPopup.IsOpen = false;

    // ----- About -----

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var version = GetAppVersion();
        if (_options.IsTestMode)
        {
            _viewModel.ShowStatus($"MdReader {version}");   // no modal dialogs in test mode (§4.7)
            return;
        }

        var about = BuildAboutWindow(version);
        _theme.AttachWindow(about);
        about.ShowDialog();
    }

    private Window BuildAboutWindow(string version)
    {
        var window = new Window
        {
            Owner = this,
            Title = "About MdReader",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
            FontFamily = FontFamily,
            FontSize = FontSize,
        };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        window.SetResourceReference(BackgroundProperty, "Brush.Document.Background");
        window.SetResourceReference(ForegroundProperty, "Brush.Foreground");

        var tile = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Left };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.Illustration.Background");
        var glyph = new TextBlock { Text = "\uE8A5", FontSize = 26 };
        glyph.SetResourceReference(StyleProperty, "IconTextStyle");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        tile.Child = glyph;

        var title = new TextBlock { Text = "MdReader", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");

        var details = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = $"Version {version}\nA Markdown reader for Windows.\n\n"
                   + $".NET {Environment.Version} · WebView2 Runtime {GetWebViewVersion()}",
        };
        details.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Foreground.Muted");

        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        ok.Click += (_, _) => window.Close();

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 20), Width = 340 };
        panel.Children.Add(tile);
        panel.Children.Add(title);
        panel.Children.Add(details);
        panel.Children.Add(ok);
        window.Content = panel;
        return window;
    }

    private static string GetAppVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private static string GetWebViewVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "not installed";
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            return "not installed";
        }
    }
}
