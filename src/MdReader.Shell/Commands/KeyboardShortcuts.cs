namespace MdReader.Shell.Commands;

/// What a shortcut does. FindNext / FindPrevious / CloseFind are listed for gesture text only: they are not handled by the
/// window router but by the document view / find bar (WP6), because they depend on find-bar state that IDocumentTab
/// doesn't expose. Unhandled keys keep tunneling from MainWindow down to the DocumentView.
public enum ShortcutAction
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
    ToggleSplitView,
    Save,
}

/// <summary>
/// The keys the §4.12 table uses, named independently of any UI framework. Each shell translates its own key enum into
/// this one (<c>MdReader.App.Commands.WpfKeys</c> for WPF); a key that isn't in the table maps to <see cref="None"/>.
/// </summary>
public enum ShortcutKey
{
    None = 0,

    /// Any of the eight modifier keys. Their key-up clears the held-key set (see <see cref="KeyboardShortcutRouter"/>).
    Modifier,

    B,
    E,
    F,
    O,
    P,
    S,
    W,
    D0,
    NumPad0,
    Escape,
    Tab,
    PageUp,
    PageDown,
    F3,
    F4,
    F5,
    Plus,             // OemPlus
    Minus,            // OemMinus
    NumPadAdd,
    NumPadSubtract,
}

/// <summary>Modifier keys, as flags. Matched exactly, so AltGr (Ctrl+Alt) never triggers a Ctrl row.</summary>
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// The shortcut map of ARCHITECTURE §4.12 and its matcher. Framework-free: the shell translates its own key events into
/// <see cref="ShortcutKey"/> / <see cref="ShortcutModifiers"/> and hands them to <see cref="KeyboardShortcutRouter"/>.
/// No InputBindings; tooltips and menu gesture text read from this map.
/// </summary>
public static class KeyboardShortcuts
{
    public sealed record Shortcut(ShortcutKey Key, ShortcutModifiers Modifiers, ShortcutAction Action, bool AllowRepeat,
                                  bool HandledByWindow = true);

    private const ShortcutModifiers Ctrl = ShortcutModifiers.Control;
    private const ShortcutModifiers CtrlShift = ShortcutModifiers.Control | ShortcutModifiers.Shift;

    /// Every row of the §4.12 table. The first row of each action is its primary gesture (shown in tooltips/menus).
    public static IReadOnlyList<Shortcut> Map { get; } =
    [
        new(ShortcutKey.O, Ctrl, ShortcutAction.Open, AllowRepeat: false),
        new(ShortcutKey.W, Ctrl, ShortcutAction.CloseTab, AllowRepeat: false),
        new(ShortcutKey.F4, Ctrl, ShortcutAction.CloseTab, AllowRepeat: false),
        new(ShortcutKey.Tab, Ctrl, ShortcutAction.NextTab, AllowRepeat: true),
        new(ShortcutKey.PageDown, Ctrl, ShortcutAction.NextTab, AllowRepeat: true),
        new(ShortcutKey.Tab, CtrlShift, ShortcutAction.PreviousTab, AllowRepeat: true),
        new(ShortcutKey.PageUp, Ctrl, ShortcutAction.PreviousTab, AllowRepeat: true),
        new(ShortcutKey.F, Ctrl, ShortcutAction.Find, AllowRepeat: false),
        new(ShortcutKey.F3, ShortcutModifiers.None, ShortcutAction.FindNext, AllowRepeat: true, HandledByWindow: false),
        new(ShortcutKey.F3, ShortcutModifiers.Shift, ShortcutAction.FindPrevious, AllowRepeat: true, HandledByWindow: false),
        new(ShortcutKey.Escape, ShortcutModifiers.None, ShortcutAction.CloseFind, AllowRepeat: false, HandledByWindow: false),
        new(ShortcutKey.P, Ctrl, ShortcutAction.Print, AllowRepeat: false),
        new(ShortcutKey.S, CtrlShift, ShortcutAction.ExportPdf, AllowRepeat: false),
        new(ShortcutKey.Plus, Ctrl, ShortcutAction.ZoomIn, AllowRepeat: true),
        new(ShortcutKey.Plus, CtrlShift, ShortcutAction.ZoomIn, AllowRepeat: true),
        new(ShortcutKey.NumPadAdd, Ctrl, ShortcutAction.ZoomIn, AllowRepeat: true),
        new(ShortcutKey.Minus, Ctrl, ShortcutAction.ZoomOut, AllowRepeat: true),
        new(ShortcutKey.Minus, CtrlShift, ShortcutAction.ZoomOut, AllowRepeat: true),
        new(ShortcutKey.NumPadSubtract, Ctrl, ShortcutAction.ZoomOut, AllowRepeat: true),
        new(ShortcutKey.D0, Ctrl, ShortcutAction.ZoomReset, AllowRepeat: false),
        new(ShortcutKey.NumPad0, Ctrl, ShortcutAction.ZoomReset, AllowRepeat: false),
        new(ShortcutKey.F5, ShortcutModifiers.None, ShortcutAction.Reload, AllowRepeat: false),
        new(ShortcutKey.B, Ctrl, ShortcutAction.ToggleToc, AllowRepeat: false),
        new(ShortcutKey.E, Ctrl, ShortcutAction.ToggleSplitView, AllowRepeat: false),
        new(ShortcutKey.S, Ctrl, ShortcutAction.Save, AllowRepeat: false),
    ];

    /// Tooltip text keyed by AutomationId, e.g. ToolTips["OpenButton"] == "Open (Ctrl+O)". Used from XAML via x:Static.
    public static IReadOnlyDictionary<string, string> ToolTips { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OpenButton"] = Describe("Open", ShortcutAction.Open),
        ["RecentButton"] = "Recent files",
        ["TocToggleButton"] = Describe("Table of contents", ShortcutAction.ToggleToc),
        ["FindButton"] = Describe("Find", ShortcutAction.Find),
        ["ExportPdfButton"] = Describe("Export PDF", ShortcutAction.ExportPdf),
        ["SplitViewButton"] = Describe("Split view", ShortcutAction.ToggleSplitView),
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
        ["SaveMenuItem"] = GestureText(ShortcutAction.Save),
        ["ReloadMenuItem"] = GestureText(ShortcutAction.Reload),
        ["PrintMenuItem"] = GestureText(ShortcutAction.Print),
        ["ExportPdfMenuItem"] = GestureText(ShortcutAction.ExportPdf),
    };

    /// Primary gesture of an action, e.g. "Ctrl+Shift+S".
    public static string GestureText(ShortcutAction action)
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

    /// The row for a key press, or null. Modifiers must match exactly.
    public static Shortcut? Match(ShortcutKey key, ShortcutModifiers modifiers)
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

    private static string Describe(string label, ShortcutAction action)
    {
        var gesture = GestureText(action);
        return gesture.Length == 0 ? label : $"{label} ({gesture})";
    }

    private static string FormatGesture(ShortcutKey key, ShortcutModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ShortcutModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ShortcutModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ShortcutModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(key switch
        {
            ShortcutKey.Plus or ShortcutKey.NumPadAdd => "Plus",
            ShortcutKey.Minus or ShortcutKey.NumPadSubtract => "Minus",
            ShortcutKey.D0 or ShortcutKey.NumPad0 => "0",
            ShortcutKey.PageDown => "Page Down",
            ShortcutKey.PageUp => "Page Up",
            ShortcutKey.Escape => "Esc",
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }
}
