using MdReader.Core.Settings;
using MdReader.Ui.Interop;

namespace MdReader.Ui.Services;

/// <summary>
/// The on-screen check on Windows: the system DPI and the monitor APIs, exactly as the WPF shell does it, so both
/// shells accept and reject the same saved placements.
/// </summary>
public sealed partial class WindowPlacementService
{
    private static partial bool IsOnScreenCore(WindowPlacement placement)
    {
        uint dpi = NativeMethods.GetDpiForSystem();
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var rect = new NativeMethods.RECT(
            (int)Math.Round(placement.Left * scale),
            (int)Math.Round(placement.Top * scale),
            (int)Math.Round((placement.Left + placement.Width) * scale),
            (int)Math.Round((placement.Top + placement.Height) * scale));

        nint monitor = NativeMethods.MonitorFromRect(in rect, NativeMethods.MONITOR_DEFAULTTONULL);
        if (monitor == 0)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        NativeMethods.RECT work = info.rcWork;
        int visibleWidth = Math.Min(rect.Right, work.Right) - Math.Max(rect.Left, work.Left);
        int visibleHeight = Math.Min(rect.Bottom, work.Bottom) - Math.Max(rect.Top, work.Top);
        return visibleWidth >= MinVisibleWidthPx && visibleHeight >= MinVisibleHeightPx
               && rect.Top >= work.Top - 8;   // the title bar must be reachable
    }
}
