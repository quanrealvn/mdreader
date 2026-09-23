using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MdReader.Shell.ViewModels;

namespace MdReader.App.Views;

/// Tab strip behavior that isn't expressible in XAML: wheel scrolling, middle-click close, keeping the active tab in view.
public partial class TabStrip : UserControl
{
    private const double WheelStep = 48;

    public TabStrip()
    {
        InitializeComponent();
        TabList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    /// Dims the edge scroll button that can't scroll any further.
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scroller || scroller.Template is null)
        {
            return;
        }

        if (scroller.Template.FindName("PART_ScrollLeft", scroller) is UIElement left)
        {
            left.IsEnabled = scroller.HorizontalOffset > 0.5;
        }

        if (scroller.Template.FindName("PART_ScrollRight", scroller) is UIElement right)
        {
            right.IsEnabled = scroller.HorizontalOffset < scroller.ScrollableWidth - 0.5;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer() is not { } scroller || scroller.ScrollableWidth <= 0)
        {
            return;
        }

        var delta = e.Delta > 0 ? -WheelStep : WheelStep;
        scroller.ScrollToHorizontalOffset(Math.Clamp(scroller.HorizontalOffset + delta, 0, scroller.ScrollableWidth));
        e.Handled = true;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var container = ItemsControl.ContainerFromElement(TabList, source) as ListBoxItem;
        if (container?.DataContext is IDocumentTab tab && tab.CloseCommand.CanExecute(null))
        {
            tab.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabList.SelectedItem is { } item)
        {
            // After layout, so a freshly added tab has a container to scroll to.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (ReferenceEquals(TabList.SelectedItem, item))
                {
                    TabList.ScrollIntoView(item);
                }
            });
        }
    }

    private ScrollViewer? FindScrollViewer() => FindDescendant<ScrollViewer>(TabList);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
