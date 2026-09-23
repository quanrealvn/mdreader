using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MdReader.Shell.ViewModels;
using MdReader.Ui.Commands;

namespace MdReader.Ui.Views;

/// <summary>
/// Keeps one <see cref="DocumentView"/> per tab alive (ARCHITECTURE §4.11). Listens only to Add/Remove/Replace/Reset of
/// <see cref="Tabs"/> (Move is ignored, so reordering never touches a web view), shows the active tab's view and hides
/// the others. A view is created once per tab and never re-parented; Remove only detaches it — the tab's Dispose
/// (called by ITabHost.Close) disposes its web view.
/// </summary>
/// <remarks>
/// An invisible child is not arranged, so Avalonia's native host hides the child window at its last size
/// (<c>HideWithSize</c>) rather than collapsing it: switching tabs never re-lays out a large page.
/// </remarks>
public sealed class DocumentHost : Grid
{
    public static readonly StyledProperty<ReadOnlyObservableCollection<IDocumentTab>?> TabsProperty =
        AvaloniaProperty.Register<DocumentHost, ReadOnlyObservableCollection<IDocumentTab>?>(nameof(Tabs));

    private readonly Dictionary<IDocumentTab, DocumentView> _views = new(ReferenceEqualityComparer.Instance);
    private INotifyCollectionChanged? _subscribedTabs;

    public DocumentHost()
    {
        ClipToBounds = true;
    }

    public ReadOnlyObservableCollection<IDocumentTab>? Tabs
    {
        get => GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    /// <summary>
    /// The window's key router, handed to every document view so the keys WebView2 forwards reach the same shortcut
    /// map as the keys Avalonia delivers. Set by the main window before any tab arrives.
    /// </summary>
    public AvaloniaShortcutRouter? Shortcuts { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TabsProperty)
        {
            OnTabsChanged(change.GetNewValue<ReadOnlyObservableCollection<IDocumentTab>?>());
        }
    }

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
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnCollectionChanged(sender, e));
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

        foreach (object? item in items)
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

        foreach (object? item in items)
        {
            if (item is IDocumentTab tab)
            {
                RemoveView(tab);
            }
        }
    }

    private void AddView(IDocumentTab tab)
    {
        if (_views.ContainsKey(tab) || tab is not DocumentTabViewModel viewModel || Shortcuts is null)
        {
            return;
        }

        // A tab gets exactly one view (one WebView2) for its whole life; a tab that comes back after a Reset keeps it.
        DocumentView view = viewModel.View as DocumentView ?? new DocumentView(viewModel, Shortcuts);
        view.IsVisible = tab.IsActive;
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

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnTabPropertyChanged(sender, e));
            return;
        }

        if (_views.TryGetValue(tab, out DocumentView? view))
        {
            view.IsVisible = tab.IsActive;
        }
    }
}
