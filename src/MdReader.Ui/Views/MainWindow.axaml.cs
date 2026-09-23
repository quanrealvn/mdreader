using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using MdReader.Core.Cli;
using MdReader.Shell.ViewModels;
using MdReader.Ui.Commands;
using MdReader.Ui.Services;

namespace MdReader.Ui.Views;

/// The single top-level window. View-only logic lives here: dropdown menus, drag-and-drop onto the Avalonia chrome, the
/// status toast popup and the About dialog. Everything else is bound to MainViewModel.
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AvaloniaThemeWindows _theme;
    private readonly CommandLineOptions _options;
    private readonly MenuFlyout _recentMenu = new() { Placement = PlacementMode.BottomEdgeAlignedRight };

    public MainWindow(MainViewModel viewModel, AvaloniaShortcutRouter shortcuts, AvaloniaThemeWindows theme,
                      WindowPlacementService placement, CommandLineOptions options)
    {
        _viewModel = viewModel;
        _theme = theme;
        _options = options;

        InitializeComponent();
        DataContext = viewModel;
        DocumentHostControl.Shortcuts = shortcuts;
        TryLoadIcon();

        shortcuts.Attach(this);
        theme.AttachWindow(this);
        placement.Attach(this);

        // Screenshots (--capture) and automated runs (--instance-id / MDREADER_TEST_MODE) must never steal focus from
        // whatever the user is doing. A second instance forwarding a file still brings the window forward through
        // WindowActivator.
        if (options.CapturePath is not null || options.IsTestMode)
        {
            ShowActivated = false;
        }

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Activated += (_, _) => UpdateStatusPopup();     // re-shows a toast that is still within its 4 s
        Deactivated += (_, _) => UpdateStatusPopup();   // popups are topmost: never float over other apps
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            StatusPopup.IsOpen = false;
        };
    }

    // ----- Menus (More ▾ is the MoreButton's flyout, declared in XAML; Recent ▾ is rebuilt on every open) -----

    private void OnRecentButtonClick(object? sender, RoutedEventArgs e)
    {
        var items = new List<Control>();
        var recent = _viewModel.RecentFiles.OfType<RecentFileEntry>().ToList();
        if (recent.Count == 0)
        {
            items.Add(new MenuItem { Header = "No recent files", IsEnabled = false });
        }
        else
        {
            foreach (RecentFileEntry entry in recent)
            {
                items.Add(CreateRecentMenuItem(entry));
            }

            items.Add(new Separator());
            var clear = new MenuItem { Header = "_Clear recent files", Command = _viewModel.ClearRecentFilesCommand };
            AutomationProperties.SetAutomationId(clear, "ClearRecentMenuItem");
            items.Add(clear);
        }

        _recentMenu.ItemsSource = items;
        _recentMenu.ShowAt(RecentButton);
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
            Foreground = this.FindResource("Brush.Foreground.Muted") as IBrush,
        };
        var header = new StackPanel { Margin = new Thickness(0, 3, 0, 4) };
        header.Children.Add(new TextBlock { Text = entry.FileName, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440 });
        header.Children.Add(folder);

        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = "", Theme = this.FindResource("MenuIcon") as ControlTheme },
            Command = _viewModel.OpenRecentCommand,
            CommandParameter = entry.FullPath,
        };
        ToolTip.SetTip(item, entry.FullPath);
        ToolTip.SetShowDelay(item, 900);
        AutomationProperties.SetAutomationId(item, "RecentMenuItem");
        AutomationProperties.SetName(item, entry.FullPath);
        return item;
    }

    // ----- Drag and drop onto the Avalonia chrome (drops onto a web view are handled by the document view) -----

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } files)
        {
            return;
        }

        e.Handled = true;
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            _viewModel.OpenDroppedFiles(paths);
        }
    }

    // ----- Status toast -----

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsStatusVisible) or nameof(MainViewModel.StatusText))
        {
            UpdateStatusPopup();
        }
        else if (e.PropertyName is nameof(MainViewModel.HasTabs))
        {
            UpdateEmptyState();
        }
    }

    /// Runs inside the first Show, after Program opened the command-line documents.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateEmptyState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty)
        {
            UpdateStatusPopup();
        }
    }

    /// The empty state (with its recent-files list) is built only when it's first needed.
    private void UpdateEmptyState()
    {
        if (_viewModel.HasTabs)
        {
            EmptyStateHost.IsVisible = false;
            return;
        }

        EmptyStateHost.Content ??= new EmptyState();
        EmptyStateHost.IsVisible = true;
    }

    /// The toast is a Popup (its own topmost window, so it can sit above the native web-view window). It's shown only
    /// while the main window is active, visible and not minimized; a toast raised while inactive appears on activation
    /// if its 4 s are left.
    private void UpdateStatusPopup()
    {
        bool show = _viewModel.IsStatusVisible && IsVisible && IsActive && WindowState != WindowState.Minimized;
        if (StatusPopup.IsOpen != show)
        {
            StatusPopup.IsOpen = show;
        }
    }

    private void OnStatusClicked(object? sender, PointerReleasedEventArgs e) => StatusPopup.IsOpen = false;

    // ----- About -----

    /// The same About window the More ▾ menu opens, for the macOS application menu.
    internal void ShowAbout() => OnAboutClick(this, new RoutedEventArgs());

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        string version = GetAppVersion();
        if (_options.IsTestMode)
        {
            _viewModel.ShowStatus($"MdReader {version}");   // no modal dialogs in test mode (§4.7)
            return;
        }

        Window about = BuildAboutWindow(version);
        _theme.AttachWindow(about);
        _ = about.ShowDialog(this);
    }

    private Window BuildAboutWindow(string version)
    {
        var window = new Window
        {
            Title = "About MdReader",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Background = this.FindResource("Brush.Document.Background") as IBrush,
            Foreground = this.FindResource("Brush.Foreground") as IBrush,
        };

        var tile = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = this.FindResource("Brush.Illustration.Background") as IBrush,
            Child = new TextBlock
            {
                Text = "",
                FontSize = 26,
                Theme = this.FindResource("IconText") as ControlTheme,
                Foreground = this.FindResource("Brush.Accent") as IBrush,
            },
        };

        var title = new TextBlock
        {
            Text = "MdReader",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 14, 0, 0),
            FontFamily = this.FindResource("Font.Display") as FontFamily ?? FontFamily,
        };

        var details = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("Brush.Foreground.Muted") as IBrush,
            Text = $"Version {version}\n{ProductDescription}\n\n"
                   + $".NET {Environment.Version} · {GetWebViewVersion()}",
        };

        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        ok.Click += (_, _) => window.Close();

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 20), Width = 340 };
        panel.Children.Add(tile);
        panel.Children.Add(title);
        panel.Children.Add(details);
        panel.Children.Add(ok);
        window.Content = panel;
        return window;
    }

    private void TryLoadIcon()
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://MdReader/Assets/MdReader.ico"));
            Icon = new WindowIcon(stream);
        }
        catch (Exception)
        {
            // The window simply keeps the platform's default icon.
        }
    }

    private static string GetAppVersion()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    /// The one line of the About box that names the platform.
    private static partial string ProductDescription { get; }

    /// What the About box calls the engine behind the page: the WebView2 Runtime's version, or WebKit's.
    private static partial string GetWebViewVersion();
}
