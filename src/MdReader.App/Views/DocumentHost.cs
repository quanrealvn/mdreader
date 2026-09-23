using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using MdReader.Shell.Commands;
using MdReader.Shell.Services;
using MdReader.Shell.ViewModels;

namespace MdReader.App.Views;

/// <summary>
/// Keeps one <see cref="DocumentView"/> per tab alive (ARCHITECTURE §4.11). Listens only to Add/Remove/Replace/Reset of
/// <see cref="Tabs"/> (Move is ignored, so reordering never touches a WebView), shows the active tab's view and hides the
/// others. A view is created once per tab and never re-parented; Remove only detaches it — the tab's Dispose (called by
/// ITabHost.Close) disposes its WebView.
/// </summary>
public sealed class DocumentHost : Grid
{
    public static readonly DependencyProperty TabsProperty = DependencyProperty.Register(
        nameof(Tabs),
        typeof(ReadOnlyObservableCollection<IDocumentTab>),
        typeof(DocumentHost),
        new PropertyMetadata(null, OnTabsChanged));

    private readonly Dictionary<IDocumentTab, DocumentView> _views = new(ReferenceEqualityComparer.Instance);
    private INotifyCollectionChanged? _subscribedTabs;

    public DocumentHost()
    {
        // Same color as the page (--mdr-bg) and WebView2.DefaultBackgroundColor, so nothing flashes (§10).
        SetResourceReference(BackgroundProperty, "Brush.Document.Background");
        ClipToBounds = true;
    }

    public ReadOnlyObservableCollection<IDocumentTab>? Tabs
    {
        get => (ReadOnlyObservableCollection<IDocumentTab>?)GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DocumentHostAutomationPeer(this);

    private static void OnTabsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DocumentHost)d).OnTabsChanged((ReadOnlyObservableCollection<IDocumentTab>?)e.NewValue);

    private void OnTabsChanged(ReadOnlyObservableCollection<IDocumentTab>? tabs)
    {
        if (_subscribedTabs is not null)
        {
            _subscribedTabs.CollectionChanged -= OnCollectionChanged;
            _subscribedTabs = null;
        }

        if (tabs is not null)
        {
            _subscribedTabs = tabs;
            _subscribedTabs.CollectionChanged += OnCollectionChanged;
        }

        Synchronize();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnCollectionChanged(sender, e));
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                AddViews(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                RemoveViews(e.OldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                RemoveViews(e.OldItems);
                AddViews(e.NewItems);
                break;
            case NotifyCollectionChangedAction.Reset:
                Synchronize();
                break;
            case NotifyCollectionChangedAction.Move:
            default:
                break;   // order never matters: every view occupies the same cell
        }
    }

    private void Synchronize()
    {
        ReadOnlyObservableCollection<IDocumentTab>? tabs = Tabs;
        var current = new HashSet<IDocumentTab>(ReferenceEqualityComparer.Instance);
        if (tabs is not null)
        {
            foreach (IDocumentTab tab in tabs)
            {
                current.Add(tab);
            }
        }

        foreach (IDocumentTab stale in _views.Keys.Where(t => !current.Contains(t)).ToList())
        {
            RemoveView(stale);
        }

        if (tabs is not null)
        {
            foreach (IDocumentTab tab in tabs)
            {
                AddView(tab);
            }
        }
    }

    private void AddViews(System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (object item in items)
        {
            if (item is IDocumentTab tab)
            {
                AddView(tab);
            }
        }
    }

    private void RemoveViews(System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (object item in items)
        {
            if (item is IDocumentTab tab)
            {
                RemoveView(tab);
            }
        }
    }

    private void AddView(IDocumentTab tab)
    {
        if (_views.ContainsKey(tab) || tab is not DocumentTabViewModel viewModel)
        {
            return;
        }

        // A tab gets exactly one view (one WebView2) for its whole life; a tab that comes back after a Reset keeps it.
        DocumentView view = viewModel.View as DocumentView ?? new DocumentView(viewModel);
        view.Visibility = tab.IsActive ? Visibility.Visible : Visibility.Hidden;
        tab.PropertyChanged += OnTabPropertyChanged;
        _views.Add(tab, view);
        if (view.Parent is null)
        {
            Children.Add(view);
        }
    }

    private void RemoveView(IDocumentTab tab)
    {
        if (!_views.Remove(tab, out DocumentView? view))
        {
            return;
        }

        tab.PropertyChanged -= OnTabPropertyChanged;
        Children.Remove(view);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not IDocumentTab tab
            || !(string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(IDocumentTab.IsActive)))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnTabPropertyChanged(sender, e));
            return;
        }

        if (_views.TryGetValue(tab, out DocumentView? view))
        {
            // Hidden (not Collapsed) keeps the WebView's size, so switching tabs never re-lays out a large page.
            view.Visibility = tab.IsActive ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private sealed class DocumentHostAutomationPeer(DocumentHost owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(DocumentHost);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
    }
}
