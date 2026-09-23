using MdReader.Shell.Commands;

namespace MdReader.Ui.Commands;

/// <summary>
/// The macOS half of the key routing. Nothing is ever forwarded out of the web view here — the native menu bar
/// dispatches every shortcut as a key equivalent before the page's first responder is offered the key — so the only
/// caller of <see cref="CurrentModifiers"/> never runs, and there is no keyboard to poll.
/// </summary>
public sealed partial class AvaloniaShortcutRouter
{
    public static partial ShortcutModifiers CurrentModifiers() => ShortcutModifiers.None;
}
