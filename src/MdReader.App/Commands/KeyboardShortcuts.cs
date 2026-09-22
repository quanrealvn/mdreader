using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MdReader.App.ViewModels;
using MdReader.Core.Diagnostics;

namespace MdReader.App.Commands;

/// What a shortcut does. FindNext / FindPrevious / CloseFind are listed for gesture text only: they are not handled by the
/// window router but by the document view / find bar (WP6), because they depend on find-bar state that IDocumentTab
/// doesn't expose. Unhandled keys keep tunneling from MainWindow down to the DocumentView.
internal enum ShortcutAction
{
    Open,
    CloseTab,
    NextTab,
    PreviousTab,
    Find,
    FindNext,
    FindPrevious,
    CloseFind,
    Print,
    ExportPdf,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    Reload,
    ToggleToc,
}

/// The shortcut map (§4.12) and the single MainWindow.PreviewKeyDown router.
/// - Tunneling PreviewKeyDown sees keys from WPF focus and keys the WebView2 control forwards from AcceleratorKeyPressed.
/// - Key.System is normalized to e.SystemKey; modifiers must match exactly (so AltGr = Ctrl+Alt never triggers Ctrl rows).
/// - A match sets e.Handled = true and defers the action with Dispatcher.BeginInvoke(DispatcherPriority.Input): the browser
///   process is blocked while a forwarded key is being handled, so no CoreWebView2 API may be called synchronously here.
/// - Auto-repeat is honored only for rows marked repeat. Keys forwarded by WebView2 never carry IsRepeat, so a key that is
///   still held since its last handled press (no key-up seen yet) also counts as a repeat.
/// - No InputBindings; tooltips and menu gesture text read from this map.
public sealed class KeyboardShortcuts
{
    internal sealed record Shortcut(Key Key, ModifierKeys Modifiers, ShortcutAction Action, bool AllowRepeat, bool HandledByWindow = true);

    private const ModifierKeys Ctrl = ModifierKeys.Control;
    private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
    private const string Category = "Keyboard";

    /// Every row of the §4.12 table. The first row of each action is its primary gesture (shown in tooltips/menus).
    internal static IReadOnlyList<Shortcut> Map { get; } =
    [
        new(Key.O, Ctrl, ShortcutAction.Open, AllowRepeat: false),
        new(Key.W, Ctrl, ShortcutAction.CloseTab, AllowRepeat: false),
        new(Key.F4, Ctrl, ShortcutAction.CloseTab, AllowRepeat: false),
        new(Key.Tab, Ctrl, ShortcutAction.NextTab, AllowRepeat: true),
        new(Key.PageDown, Ctrl, ShortcutAction.NextTab, AllowRepeat: true),
        new(Key.Tab, CtrlShift, ShortcutAction.PreviousTab, AllowRepeat: true),
        new(Key.PageUp, Ctrl, ShortcutAction.PreviousTab, AllowRepeat: true),
        new(Key.F, Ctrl, ShortcutAction.Find, AllowRepeat: false),
        new(Key.F3, ModifierKeys.None, ShortcutAction.FindNext, AllowRepeat: true, HandledByWindow: false),
        new(Key.F3, ModifierKeys.Shift, ShortcutAction.FindPrevious, AllowRepeat: true, HandledByWindow: false),
        new(Key.Escape, ModifierKeys.None, ShortcutAction.CloseFind, AllowRepeat: false, HandledByWindow: false),
        new(Key.P, Ctrl, ShortcutAction.Print, AllowRepeat: false),
        new(Key.S, CtrlShift, ShortcutAction.ExportPdf, AllowRepeat: false),
        new(Key.OemPlus, Ctrl, ShortcutAction.ZoomIn, AllowRepeat: true),
        new(Key.OemPlus, CtrlShift, ShortcutAction.ZoomIn, AllowRepeat: true),
        new(Key.Add, Ctrl, ShortcutAction.ZoomIn, AllowRepeat: true),
        new(Key.OemMinus, Ctrl, ShortcutAction.ZoomOut, AllowRepeat: true),
        new(Key.OemMinus, CtrlShift, ShortcutAction.ZoomOut, AllowRepeat: true),
        new(Key.Subtract, Ctrl, ShortcutAction.ZoomOut, AllowRepeat: true),
        new(Key.D0, Ctrl, ShortcutAction.ZoomReset, AllowRepeat: false),
        new(Key.NumPad0, Ctrl, ShortcutAction.ZoomReset, AllowRepeat: false),
        new(Key.F5, ModifierKeys.None, ShortcutAction.Reload, AllowRepeat: false),
        new(Key.B, Ctrl, ShortcutAction.ToggleToc, AllowRepeat: false),
    ];

    /// Tooltip text keyed by AutomationId, e.g. ToolTips["OpenButton"] == "Open (Ctrl+O)". Used from XAML via x:Static.
    public static IReadOnlyDictionary<string, string> ToolTips { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OpenButton"] = Describe("Open", ShortcutAction.Open),
        ["RecentButton"] = "Recent files",
        ["TocToggleButton"] = Describe("Table of contents", ShortcutAction.ToggleToc),
        ["FindButton"] = Describe("Find", ShortcutAction.Find),
        ["ZoomOutButton"] = Describe("Zoom out", ShortcutAction.ZoomOut),
        ["ZoomResetButton"] = Describe("Reset zoom", ShortcutAction.ZoomReset),
        ["ZoomInButton"] = Describe("Zoom in", ShortcutAction.ZoomIn),
        ["MoreButton"] = "More options",
        ["TabCloseButton"] = Describe("Close tab", ShortcutAction.CloseTab),
        ["EmptyStateOpenButton"] = Describe("Open a Markdown file", ShortcutAction.Open),
        ["FindNextButton"] = Describe("Next match", ShortcutAction.FindNext),
        ["FindPreviousButton"] = Describe("Previous match", ShortcutAction.FindPrevious),
        ["FindCloseButton"] = Describe("Close", ShortcutAction.CloseFind),
    };

    /// Menu InputGestureText keyed by AutomationId, e.g. MenuGestures["ReloadMenuItem"] == "F5".
    public static IReadOnlyDictionary<string, string> MenuGestures { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ReloadMenuItem"] = GestureText(ShortcutAction.Reload),
        ["PrintMenuItem"] = GestureText(ShortcutAction.Print),
        ["ExportPdfMenuItem"] = GestureText(ShortcutAction.ExportPdf),
    };

    private readonly MainViewModel _viewModel;
    private readonly IAppLog _log;
    private readonly HashSet<Key> _heldKeys = [];
    private Dispatcher? _dispatcher;

    public KeyboardShortcuts(MainViewModel viewModel, IAppLog log)
    {
        _viewModel = viewModel;
        _log = log;
    }

    /// Installs the router on the window (once).
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _dispatcher = window.Dispatcher;
        window.PreviewKeyDown += OnPreviewKeyDown;
        window.PreviewKeyUp += OnPreviewKeyUp;
        window.Deactivated += (_, _) => _heldKeys.Clear();
    }

    /// Primary gesture of an action, e.g. "Ctrl+Shift+S".
    internal static string GestureText(ShortcutAction action)
    {
        foreach (var shortcut in Map)
        {
            if (shortcut.Action == action)
            {
                return FormatGesture(shortcut.Key, shortcut.Modifiers);
            }
        }

        return "";
    }

    internal static Shortcut? Match(Key key, ModifierKeys modifiers)
    {
        foreach (var shortcut in Map)
        {
            if (shortcut.Key == key && shortcut.Modifiers == modifiers)
            {
                return shortcut;
            }
        }

        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shortcut = Match(key, e.KeyboardDevice.Modifiers);
        if (shortcut is null || !shortcut.HandledByWindow)
        {
            return;
        }

        e.Handled = true;
        var isRepeat = e.IsRepeat || !_heldKeys.Add(key);
        if (isRepeat && !shortcut.AllowRepeat)
        {
            return;
        }

        var action = shortcut.Action;
        (_dispatcher ?? Dispatcher.CurrentDispatcher).BeginInvoke(DispatcherPriority.Input, () => Execute(action));
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
        {
            // The key-up of the main key may not be forwarded by WebView2 once its modifier is released first.
            _heldKeys.Clear();
        }
        else
        {
            _heldKeys.Remove(key);
        }
    }

    private void Execute(ShortcutAction action)
    {
        var vm = _viewModel;
        var command = action switch
        {
            ShortcutAction.Open => vm.OpenCommand,
            ShortcutAction.CloseTab => vm.CloseActiveTabCommand,
            ShortcutAction.NextTab => vm.NextTabCommand,
            ShortcutAction.PreviousTab => vm.PreviousTabCommand,
            ShortcutAction.Find => vm.FindCommand,
            ShortcutAction.Print => vm.PrintCommand,
            ShortcutAction.ExportPdf => vm.ExportPdfCommand,
            ShortcutAction.ZoomIn => vm.ZoomInCommand,
            ShortcutAction.ZoomOut => vm.ZoomOutCommand,
            ShortcutAction.ZoomReset => vm.ZoomResetCommand,
            ShortcutAction.Reload => vm.ReloadCommand,
            ShortcutAction.ToggleToc => vm.ToggleTocCommand,
            _ => null,
        };

        if (command is null)
        {
            return;
        }

        _log.Write(AppLogLevel.Debug, Category, $"Shortcut {action}");
        command.Execute(null);
    }

    private static string Describe(string label, ShortcutAction action)
    {
        var gesture = GestureText(action);
        return gesture.Length == 0 ? label : $"{label} ({gesture})";
    }

    private static string FormatGesture(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(key switch
        {
            Key.OemPlus or Key.Add => "Plus",
            Key.OemMinus or Key.Subtract => "Minus",
            Key.D0 or Key.NumPad0 => "0",
            Key.PageDown => "Page Down",
            Key.PageUp => "Page Up",
            Key.Escape => "Esc",
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }
}
