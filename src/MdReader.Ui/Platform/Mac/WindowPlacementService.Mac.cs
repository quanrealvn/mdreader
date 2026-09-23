using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using MdReader.Core.Settings;

namespace MdReader.Ui.Services;

/// <summary>
/// The on-screen check on macOS. There is no per-monitor DPI to convert through the way Windows has: AppKit works in
/// points and Avalonia reports every screen's working area in those same units, so the saved rectangle is compared
/// with them directly.
/// </summary>
/// <remarks>
/// The menu bar and the Dock are already outside <c>Screen.WorkingArea</c>, so the "the title bar must be reachable"
/// rule the Windows check spells out is what the work area itself says here.
/// </remarks>
public sealed partial class WindowPlacementService
{
    private static partial bool IsOnScreenCore(WindowPlacement placement)
    {
        Screens? screens = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
            .MainWindow?.Screens;
        if (screens is null)
        {
            return true;   // no window yet to ask: keep the saved placement rather than throwing it away
        }

        var rect = new PixelRect(
            (int)Math.Round(placement.Left),
            (int)Math.Round(placement.Top),
            (int)Math.Round(placement.Width),
            (int)Math.Round(placement.Height));

        foreach (Screen screen in screens.All)
        {
            PixelRect work = screen.WorkingArea;
            int visibleWidth = Math.Min(rect.Right, work.Right) - Math.Max(rect.X, work.X);
            int visibleHeight = Math.Min(rect.Bottom, work.Bottom) - Math.Max(rect.Y, work.Y);
            if (visibleWidth >= MinVisibleWidthPx && visibleHeight >= MinVisibleHeightPx && rect.Y >= work.Y - 8)
            {
                return true;
            }
        }

        return false;
    }
}
