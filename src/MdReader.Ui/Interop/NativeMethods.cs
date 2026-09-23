using System.Runtime.InteropServices;

namespace MdReader.Ui.Interop;

/// <summary>
/// user32 / dwmapi / comdlg32 P/Invoke used by the Avalonia shell (source-generated marshalling).
/// </summary>
/// <remarks>
/// Avalonia gives no synchronous file dialog, message box or clipboard API — <c>StorageProvider</c> and
/// <c>IClipboard</c> are async, and <c>IDialogService.ConfirmSaveChanges</c> is answered while the window is closing,
/// where nothing can be awaited. Phase 1 therefore calls the same Win32 dialogs the WPF shell ends up in, so both
/// shells show the user the same thing. Phase 2 replaces this file per platform.
/// </remarks>
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

    // MessageBoxW
    internal const uint MB_OK = 0x0000;
    internal const uint MB_YESNO = 0x0004;
    internal const uint MB_YESNOCANCEL = 0x0003;
    internal const uint MB_ICONERROR = 0x0010;
    internal const uint MB_ICONWARNING = 0x0030;
    internal const uint MB_ICONINFORMATION = 0x0040;
    internal const uint MB_DEFBUTTON2 = 0x0100;
    internal const uint MB_TASKMODAL = 0x2000;
    internal const int IDOK = 1;
    internal const int IDYES = 6;
    internal const int IDNO = 7;

    // OPENFILENAMEW.Flags
    internal const int OFN_HIDEREADONLY = 0x0000_0004;
    internal const int OFN_NOCHANGEDIR = 0x0000_0008;
    internal const int OFN_ALLOWMULTISELECT = 0x0000_0200;
    internal const int OFN_PATHMUSTEXIST = 0x0000_0800;
    internal const int OFN_FILEMUSTEXIST = 0x0000_1000;
    internal const int OFN_OVERWRITEPROMPT = 0x0000_0002;
    internal const int OFN_EXPLORER = 0x0008_0000;
    internal const int OFN_NOREADONLYRETURN = 0x0000_8000;

    // Clipboard
    internal const uint CF_UNICODETEXT = 13;
    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const int ERROR_ACCESS_DENIED = 5;

    // Virtual keys, for the modifier state a forwarded WebView2 key doesn't carry.
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// Pointer fields only, so the struct stays blittable for source-generated marshalling.
    [StructLayout(LayoutKind.Sequential)]
    internal struct OPENFILENAMEW
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public nint lpstrFilter;
        public nint lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public nint lpstrFile;
        public int nMaxFile;
        public nint lpstrFileTitle;
        public int nMaxFileTitle;
        public nint lpstrInitialDir;
        public nint lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public nint lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromRect(in RECT lprc, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll", EntryPoint = "GetKeyState")]
    internal static partial short GetKeyState(int virtualKey);

    internal static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, int dwAttribute, in int pvAttribute, int cbAttribute);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    [LibraryImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSaveFileNameW(ref OPENFILENAMEW ofn);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalFree(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint hMem);

    /// COLORREF (0x00BBGGRR) from RGB components.
    internal static int ToColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
