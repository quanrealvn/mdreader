using System.Runtime.InteropServices;

namespace MdReader.App.Interop;

/// user32 / dwmapi P/Invoke used by the shell (source-generated marshalling).
internal static partial class NativeMethods
{
    // ShowWindow
    internal const int SW_RESTORE = 9;

    // MonitorFromRect
    internal const uint MONITOR_DEFAULTTONULL = 0;

    // DwmSetWindowAttribute
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_CAPTION_COLOR = 35;
    internal const int DWMWA_TEXT_COLOR = 36;

    // Clipboard
    internal const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromRect(in RECT lprc, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, in int pvAttribute, int cbAttribute);

    /// COLORREF (0x00BBGGRR) from RGB components.
    internal static int ToColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
