using MdReader.Mac.Interop;

namespace MdReader.Mac;

/// <summary>
/// The few AppKit calls a shell needs to plant a web view in its window: a layer-backed container painted the page's
/// background colour, and the plumbing to keep the web view filling it. Everything here is main-thread only.
/// </summary>
/// <remarks>
/// The container exists so the background colour of §10 is on screen before the web view is created, exactly as the
/// Windows shell paints its native host window before the controller exists. It also gives the shell something
/// stable to hand Avalonia: the web view is created later and replaced on recovery, the container never is.
/// </remarks>
public static class MacNativeView
{
    /// <summary>A layer-backed <c>NSView</c>, retained, painted <paramref name="red"/>/<paramref name="green"/>/
    /// <paramref name="blue"/>. The caller releases it with <see cref="Release"/>.</summary>
    public static nint CreateContainer(byte red, byte green, byte blue) => ObjC.WithPool(() =>
    {
        nint view = ObjC.New(ObjC.RequireClass("NSView"));
        ObjC.SendVoidBool(view, ObjC.Selector("setWantsLayer:"), 1);
        SetBackgroundColor(view, red, green, blue);
        return view;
    });

    /// <summary>Repaints the container (a theme change before the page has drawn anything).</summary>
    public static void SetBackgroundColor(nint view, byte red, byte green, byte blue)
    {
        if (view == 0)
        {
            return;
        }

        ObjC.WithPool(() =>
        {
            nint layer = ObjC.Send(view, ObjC.Selector("layer"));
            if (layer != 0)
            {
                nint color = WkWebViewSecurity.Color(red, green, blue);
                ObjC.SendVoid(layer, ObjC.Selector("setBackgroundColor:"), ObjC.Send(color, ObjC.Selector("CGColor")));
            }
        });
    }

    /// <summary>Adds <paramref name="child"/> to <paramref name="container"/> and makes it follow its size.</summary>
    public static void AddFillingSubview(nint container, nint child) => ObjC.WithPool(() =>
    {
        ObjC.SendVoidRect(child, ObjC.Selector("setFrame:"), Bounds(container));

        // NSViewWidthSizable | NSViewHeightSizable: AppKit resizes the web view with the container, so there is no
        // per-frame work in the shell and no bounds bookkeeping like the Windows host's WM_SIZE handler.
        const nint WidthAndHeightSizable = (1 << 1) | (1 << 4);
        ObjC.SendVoidLong(child, ObjC.Selector("setAutoresizingMask:"), WidthAndHeightSizable);
        ObjC.SendVoid(container, ObjC.Selector("addSubview:"), child);
    });

    /// <summary>The view's bounds, or a 1×1 rectangle when there is no view yet.</summary>
    internal static CGRect Bounds(nint view) => view == 0 ? new CGRect(0, 0, 1, 1) : SendBounds(view);

    /// <summary>
    /// <c>WKWebView.pageZoom</c> (macOS 11+): what WebView2 calls <c>ZoomFactor</c>, and deliberately not
    /// <c>magnification</c>, which scales the rendered page instead of laying it out again.
    /// </summary>
    public static void SetPageZoom(nint webView, double zoomFactor)
    {
        if (webView != 0 && ObjC.RespondsTo(webView, "setPageZoom:"))
        {
            ObjC.WithPool(() => ObjC.SendVoidDouble(webView, ObjC.Selector("setPageZoom:"), zoomFactor));
        }
    }

    /// <summary>Moves keyboard focus into a view.</summary>
    public static void MakeFirstResponder(nint view) => ObjC.WithPool(() =>
    {
        nint window = view == 0 ? 0 : ObjC.Send(view, ObjC.Selector("window"));
        if (window != 0)
        {
            ObjC.Send(window, ObjC.Selector("makeFirstResponder:"), view);
        }
    });

    /// <summary>Takes a view out of its superview and releases it.</summary>
    public static void Release(nint view)
    {
        if (view == 0)
        {
            return;
        }

        ObjC.WithPool(() =>
        {
            ObjC.SendVoid(view, ObjC.Selector("removeFromSuperview"));
            ObjC.Release(view);
        });
    }

    /// <summary>
    /// <c>-[NSView bounds]</c> returns a CGRect. On arm64 a four-double struct comes back in d0–d3 and on x86-64
    /// through a hidden pointer; the runtime's struct marshalling handles both, so the rectangle is returned by value
    /// here rather than through <c>objc_msgSend_stret</c>, which doesn't exist on arm64 at all.
    /// </summary>
    private static CGRect SendBounds(nint view) => ObjCBounds.Bounds(view, ObjC.Selector("bounds"));
}

/// <summary>The one struct-returning message the backend sends.</summary>
internal static partial class ObjCBounds
{
    [System.Runtime.InteropServices.LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    internal static partial CGRect Bounds(nint receiver, nint selector);
}
