using MdReader.Shell.Commands;

namespace MdReader.Ui.Commands;

/// <summary>Which of <c>MainViewModel</c>'s commands a menu item runs.</summary>
public enum ShellCommand
{
    Open,
    CloseTab,
    Save,
    Reload,
    Print,
    ExportPdf,
    Find,
    FindNext,
    FindPrevious,
    ToggleToc,
    ToggleSplitView,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    CycleTheme,
    NextTab,
    PreviousTab,

    /// The shell's own About window; not a view-model command.
    About,
}

/// <summary>One row of a menu: a command, or a separator when <paramref name="Command"/> is null.</summary>
/// <param name="Gesture">The §4.12 action whose primary gesture this item shows, or null for no gesture.</param>
public sealed record ShellMenuItem(string Header, ShellCommand? Command, ShortcutAction? Gesture = null)
{
    public static ShellMenuItem Separator { get; } = new("-", null);

    public bool IsSeparator => Command is null;
}

public sealed record ShellMenu(string Header, IReadOnlyList<ShellMenuItem> Items);

/// <summary>
/// The macOS menu bar, as data. macOS expects a real menu bar and gives it a job Windows gives the toolbar: it is
/// where the keyboard shortcuts live, because AppKit offers a key equivalent to the menu before the focused view —
/// which is what makes ⌘F work while focus is inside the web view, where Avalonia never sees a key at all.
/// </summary>
/// <remarks>
/// <para>Every command the Windows More ▾ menu has (Save, Reload, Print, Export as PDF, About) is here, and so is
/// every command the toolbar has, because a toolbar button is not a keyboard route on macOS.</para>
/// <para>The gestures are not written out: each item names the §4.12 action it belongs to and the gesture text comes
/// from <see cref="KeyboardShortcuts"/>, translated to ⌘ by <see cref="MacGestures"/>. There is one shortcut table
/// for the whole application, whatever the platform spells it with.</para>
/// </remarks>
public static class ShellMenuModel
{
    /// <summary>The application menu's title on macOS; AppKit fills in Services, Hide and Quit itself.</summary>
    public const string ApplicationMenuHeader = "MdReader";

    public static IReadOnlyList<ShellMenu> MacMenuBar { get; } =
    [
        new(ApplicationMenuHeader,
        [
            new("About MdReader", ShellCommand.About),
        ]),
        new("File",
        [
            new("Open…", ShellCommand.Open, ShortcutAction.Open),
            ShellMenuItem.Separator,
            new("Save", ShellCommand.Save, ShortcutAction.Save),
            new("Reload", ShellCommand.Reload, ShortcutAction.Reload),
            ShellMenuItem.Separator,
            new("Print…", ShellCommand.Print, ShortcutAction.Print),
            new("Export as PDF…", ShellCommand.ExportPdf, ShortcutAction.ExportPdf),
            ShellMenuItem.Separator,
            new("Close Tab", ShellCommand.CloseTab, ShortcutAction.CloseTab),
        ]),
        new("Edit",
        [
            new("Find…", ShellCommand.Find, ShortcutAction.Find),
            new("Find Next", ShellCommand.FindNext, ShortcutAction.FindNext),
            new("Find Previous", ShellCommand.FindPrevious, ShortcutAction.FindPrevious),
        ]),
        new("View",
        [
            new("Table of Contents", ShellCommand.ToggleToc, ShortcutAction.ToggleToc),
            new("Split View", ShellCommand.ToggleSplitView, ShortcutAction.ToggleSplitView),
            ShellMenuItem.Separator,
            new("Zoom In", ShellCommand.ZoomIn, ShortcutAction.ZoomIn),
            new("Zoom Out", ShellCommand.ZoomOut, ShortcutAction.ZoomOut),
            new("Actual Size", ShellCommand.ZoomReset, ShortcutAction.ZoomReset),
            ShellMenuItem.Separator,
            new("Switch Theme", ShellCommand.CycleTheme),
        ]),
        new("Window",
        [
            new("Next Tab", ShellCommand.NextTab, ShortcutAction.NextTab),
            new("Previous Tab", ShellCommand.PreviousTab, ShortcutAction.PreviousTab),
        ]),
    ];

    /// <summary>The commands the Windows More ▾ flyout offers, which the macOS menu bar must not drop.</summary>
    public static IReadOnlyList<ShellCommand> MoreMenuCommands { get; } =
    [
        ShellCommand.Save,
        ShellCommand.Reload,
        ShellCommand.Print,
        ShellCommand.ExportPdf,
        ShellCommand.About,
    ];
}

/// <summary>
/// The §4.12 shortcut table, spelled the way macOS spells it: the primary modifier is ⌘ rather than Ctrl, and the
/// function-key rows become the ⌘-based equivalents Mac users expect (⌘G for "find next", ⌘R for "reload").
/// </summary>
/// <remarks>
/// The table itself is not duplicated. Each action is looked up in <see cref="KeyboardShortcuts"/> and its primary
/// gesture is translated; only the handful of rows where a Mac convention differs from the Windows key are listed
/// below, and each says why.
/// </remarks>
public static class MacGestures
{
    /// <summary>The Avalonia gesture text for an action's menu item, or null when it has none.</summary>
    public static string? For(ShortcutAction action) => Overrides.TryGetValue(action, out string? text)
        ? text
        : Translate(KeyboardShortcuts.GestureText(action));

    /// <summary>
    /// Rows where the Mac convention is a different key, not just a different modifier. Everything else is the §4.12
    /// row with Ctrl read as ⌘.
    /// </summary>
    private static readonly Dictionary<ShortcutAction, string> Overrides = new()
    {
        // F5 has no meaning on a Mac keyboard; ⌘R is what every Mac application reloads with.
        [ShortcutAction.Reload] = "Cmd+R",

        // F3 likewise: find-next is ⌘G, and find-previous ⇧⌘G.
        [ShortcutAction.FindNext] = "Cmd+G",
        [ShortcutAction.FindPrevious] = "Cmd+Shift+G",

        // Ctrl+Tab is the tab-switching gesture on both platforms; on macOS it stays Ctrl, not ⌘, because ⌘Tab
        // belongs to the application switcher.
        [ShortcutAction.NextTab] = "Ctrl+Tab",
        [ShortcutAction.PreviousTab] = "Ctrl+Shift+Tab",
    };

    /// <summary>
    /// Reads a §4.12 gesture with Ctrl standing for the platform's primary modifier, and spells its key the way
    /// Avalonia's <c>KeyGesture.Parse</c> does: the table is written for people ("Plus", "0", "Esc"), the parser
    /// wants <c>Key</c> member names.
    /// </summary>
    private static string? Translate(string gesture)
    {
        if (gesture.Length == 0)
        {
            return null;
        }

        int lastPlus = gesture.LastIndexOf('+');
        string modifiers = lastPlus < 0 ? string.Empty : gesture[..(lastPlus + 1)];
        string key = lastPlus < 0 ? gesture : gesture[(lastPlus + 1)..];
        return modifiers.Replace("Ctrl+", "Cmd+", StringComparison.Ordinal) + KeyName(key);
    }

    private static string KeyName(string key) => key switch
    {
        "Plus" => "OemPlus",
        "Minus" => "OemMinus",
        "0" => "D0",
        "Esc" => "Escape",
        "Page Down" => "PageDown",
        "Page Up" => "PageUp",
        _ => key,
    };
}
