using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MdReader.Shell.ViewModels;

namespace MdReader.Ui.Views;

/// Tab strip behavior that isn't expressible in XAML: wheel scrolling, middle-click close, keeping the active tab in view.
public partial class TabStrip : UserControl
{
    private const double WheelStep = 48;

    private ScrollViewer? _scroller;

    public TabStrip()
    {
        InitializeComponent();
        TabList.SelectionChanged += OnSelectionChanged;
        TabList.AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
        TabList.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        TabList.TemplateApplied += (_, _) => AttachScroller();
    }

    private void AttachScroller()
    {
        ScrollViewer? scroller = TabList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (ReferenceEquals(scroller, _scroller) || scroller is null)
        {
            return;
        }

        _scroller = scroller;
        scroller.ScrollChanged += (_, _) => UpdateScrollButtons();
        scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
            {
                UpdateScrollButtons();
            }
        };

        UpdateScrollButtons();
    }

    /// Shows the edge buttons only while the tabs overflow, and dims the one that can't scroll any further.
    private void UpdateScrollButtons()
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        double scrollable = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        bool overflow = scrollable > 0.5;
        ScrollLeftButton.IsVisible = overflow;
        ScrollRightButton.IsVisible = overflow;
        ScrollLeftButton.IsEnabled = scroller.Offset.X > 0.5;
        ScrollRightButton.IsEnabled = scroller.Offset.X < scrollable - 0.5;
    }

    private void OnScrollLeft(object? sender, RoutedEventArgs e) => ScrollBy(-WheelStep);

    private void OnScrollRight(object? sender, RoutedEventArgs e) => ScrollBy(WheelStep);

    private void ScrollBy(double delta)
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        double scrollable = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        scroller.Offset = scroller.Offset.WithX(Math.Clamp(scroller.Offset.X + delta, 0, scrollable));
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_scroller is not { } scroller || scroller.Extent.Width - scroller.Viewport.Width <= 0)
        {
            return;
        }

        ScrollBy(e.Delta.Y > 0 ? -WheelStep : WheelStep);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle)
        {
            return;
        }

        ListBoxItem? container = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (container?.DataContext is IDocumentTab tab && tab.CloseCommand.CanExecute(null))
        {
            tab.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TabList.SelectedItem is not { } item)
        {
            return;
        }

        // After layout, so a freshly added tab has a container to scroll to.
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(TabList.SelectedItem, item))
            {
                TabList.ScrollIntoView(item);
            }
        }, DispatcherPriority.Loaded);
    }
}
