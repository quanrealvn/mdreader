using Avalonia.Controls;

namespace MdReader.Ui.Services;

/// <summary>
/// The macOS half of the theme (§10, step 2). There is no DWM: the title bar, the traffic lights and the window's
/// own chrome follow the effective <c>NSAppearance</c>, which Avalonia derives from the application's
/// <c>ThemeVariant</c> — already set one step earlier. So step 2 of §10 is a no-op here, and deliberately so rather
/// than by omission.
/// </summary>
public sealed partial class AvaloniaThemeWindows
{
    private partial void ApplyWindowFrame(Window window)
    {
    }
}
