using MdReader.Ui.WebView;
using MdReader.Core.Diagnostics;

namespace MdReader.Ui.Spike;

/// <summary>
/// Answers the phase-1 keyboard question without an interactive desktop: it posts WM_KEYDOWN/WM_KEYUP to the window
/// that owns keyboard focus while the page has it (the innermost WebView2 child HWND), and then to Avalonia's own
/// top-level HWND, and lets the two logging paths — Avalonia's tunnelled <c>KeyDown</c> and WebView2's
/// <c>AcceleratorKeyPressed</c> — say which side saw what.
/// </summary>
/// <remarks>
/// Enabled with <c>MDREADER_SPIKE_KEY_PROBE=1</c>; it is a diagnostic, not a feature. Synthetic posts carry no
/// modifier state, so the log shows the key, not the chord.
/// </remarks>
internal static class KeyProbe
{
    private const string Category = "KeyProbe";
    internal const string EnvironmentVariable = "MDREADER_SPIKE_KEY_PROBE";

    public static bool IsEnabled => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    public static async Task RunAsync(nint avaloniaWindow, nint webViewHostWindow, IAppLog log)
    {
        log.Write(AppLogLevel.Info, Category,
            $"Avalonia top level 0x{avaloniaWindow:X} ({NativeMethods.GetClassName(avaloniaWindow)}); "
            + $"web view host 0x{webViewHostWindow:X} ({NativeMethods.GetClassName(webViewHostWindow)}).");

        foreach (nint hwnd in Descendants(webViewHostWindow))
        {
            log.Write(AppLogLevel.Info, Category, $"Posting focus + Tab/Ctrl/F/Esc to 0x{hwnd:X} ({NativeMethods.GetClassName(hwnd)}).");
            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_SETFOCUS, 0, 0);
            foreach (int key in new[] { NativeMethods.VK_TAB, NativeMethods.VK_CONTROL, NativeMethods.VK_F, NativeMethods.VK_ESCAPE })
            {
                Post(hwnd, key);
            }

            await Task.Delay(250);
        }

        log.Write(AppLogLevel.Info, Category, "Posting F to the Avalonia top level (control: the shell's own key path).");
        Post(avaloniaWindow, NativeMethods.VK_F);
        await Task.Delay(500);
        log.Write(AppLogLevel.Info, Category, "Probe finished.");
    }

    private static List<nint> Descendants(nint root)
    {
        const uint GW_CHILD = 5;
        const uint GW_HWNDNEXT = 2;
        var found = new List<nint>();
        var queue = new Queue<nint>();
        queue.Enqueue(root);
        while (queue.Count > 0 && found.Count < 16)
        {
            for (nint child = NativeMethods.GetWindow(queue.Dequeue(), GW_CHILD); child != 0; child = NativeMethods.GetWindow(child, GW_HWNDNEXT))
            {
                found.Add(child);
                queue.Enqueue(child);
            }
        }

        return found;
    }

    private static void Post(nint hwnd, int virtualKey)
    {
        NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYDOWN, virtualKey, 0);
        NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYUP, virtualKey, unchecked((nint)0xC000_0001));
    }
}
