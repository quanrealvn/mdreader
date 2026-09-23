using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MdReader.Avalonia.WebView;

/// <summary>
/// The Win32 surface the Avalonia ↔ WebView2 seam needs: a child HWND of our own that Avalonia can reparent and size,
/// and whose messages we can read. Avalonia's <c>NativeControlHost</c> hands us a parent <c>IPlatformHandle</c> and
/// expects an HWND back; <c>CoreWebView2Environment.CreateCoreWebView2ControllerAsync</c> needs exactly such an HWND.
/// </summary>
internal static unsafe partial class NativeMethods
{
    internal const uint WM_SIZE = 0x0005;
    internal const uint WM_SETFOCUS = 0x0007;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_DPICHANGED_AFTERPARENT = 0x02E3;
    internal const uint WM_WINDOWPOSCHANGED = 0x0047;

    internal const uint WS_CHILD = 0x4000_0000;
    internal const uint WS_VISIBLE = 0x1000_0000;
    internal const uint WS_CLIPCHILDREN = 0x0200_0000;
    internal const uint WS_CLIPSIBLINGS = 0x0400_0000;

    internal const uint CS_HREDRAW = 0x0002;
    internal const uint CS_VREDRAW = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    private const string WindowClassName = "MdReaderAvaloniaWebViewHost";

    private static nint s_classNamePtr;
    private static bool s_classRegistered;
    private static readonly Lock RegistrationGate = new();

    /// <summary>
    /// Creates the child HWND that hosts the WebView2 controller. It belongs to our own window class, so its WndProc
    /// (and therefore WM_SIZE, WM_SETFOCUS and the post-DPI-change notification) is ours.
    /// </summary>
    internal static nint CreateHostWindow(nint parent, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> wndProc)
    {
        EnsureClassRegistered(wndProc);
        nint hwnd = CreateWindowExW(
            dwExStyle: 0,
            lpClassName: WindowClassName,
            lpWindowName: string.Empty,
            dwStyle: WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            x: 0, y: 0, nWidth: 1, nHeight: 1,
            hWndParent: parent,
            hMenu: 0,
            hInstance: GetModuleHandleW(0),
            lpParam: 0);

        return hwnd != 0
            ? hwnd
            : throw new InvalidOperationException($"CreateWindowExW failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    internal static (int Width, int Height) GetClientSize(nint hwnd)
    {
        RECT rect = default;
        return GetClientRect(hwnd, ref rect)
            ? (Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top))
            : (0, 0);
    }

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    /// <summary>
    /// CoreWebView2AcceleratorKeyPressedEventArgs carries the virtual key but not the modifier state, so the shell has
    /// to read the modifiers itself to recognise a chord such as Ctrl+F.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetKeyState")]
    internal static partial short GetKeyState(int virtualKey);

    internal const int VK_CONTROL = 0x11;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_TAB = 0x09;
    internal const int VK_ESCAPE = 0x1B;
    internal const int VK_F = 0x46;

    internal static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindow")]
    internal static partial nint GetWindow(nint hwnd, uint command);

    /// <summary>Walks to the innermost first child of <paramref name="hwnd"/> (WebView2 nests several HWNDs).</summary>
    internal static nint GetDeepestChild(nint hwnd)
    {
        const uint GW_CHILD = 5;
        nint current = hwnd;
        for (nint child = GetWindow(current, GW_CHILD); child != 0; child = GetWindow(current, GW_CHILD))
        {
            current = child;
        }

        return current;
    }

    internal static string GetClassName(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int length;
        fixed (char* p = buffer)
        {
            length = GetClassNameW(hwnd, p, buffer.Length);
        }

        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    private static partial int GetClassNameW(nint hwnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, ref RECT lpRect);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandleW(nint lpModuleName);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureClassRegistered(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> wndProc)
    {
        lock (RegistrationGate)
        {
            if (s_classRegistered)
            {
                return;
            }

            // Never freed on purpose: the class lives for the process, and Windows keeps the pointer.
            s_classNamePtr = Marshal.StringToHGlobalUni(WindowClassName);
            var wndClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = (nint)wndProc,
                hInstance = GetModuleHandleW(0),
                lpszClassName = s_classNamePtr,
            };

            if (RegisterClassExW(ref wndClass) == 0)
            {
                const int ErrorClassAlreadyExists = 1410;
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorClassAlreadyExists)
                {
                    throw new InvalidOperationException($"RegisterClassExW failed (Win32 error {error}).");
                }
            }

            s_classRegistered = true;
        }
    }
}
