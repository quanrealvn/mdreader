using Avalonia.Input;
using MdReader.Shell.Commands;

namespace MdReader.Ui.Commands;

/// <summary>
/// The menu gesture text of <see cref="KeyboardShortcuts.MenuGestures"/> as Avalonia key gestures, so a menu item can
/// show "Ctrl+Shift+S" without a second copy of the §4.12 table. Display only: the keys themselves are routed by
/// <see cref="AvaloniaShortcutRouter"/>, never by a menu.
/// </summary>
public static class MenuGestures
{
    public static IReadOnlyDictionary<string, KeyGesture> ByAutomationId { get; } = Build();

    private static Dictionary<string, KeyGesture> Build()
    {
        var gestures = new Dictionary<string, KeyGesture>(StringComparer.Ordinal);
        foreach ((string id, string text) in KeyboardShortcuts.MenuGestures)
        {
            if (text.Length == 0)
            {
                continue;   // an action with no gesture: the menu item simply shows none
            }

            gestures[id] = KeyGesture.Parse(text);
        }

        return gestures;
    }
}
