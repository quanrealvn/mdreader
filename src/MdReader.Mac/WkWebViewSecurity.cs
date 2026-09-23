using MdReader.Core.Diagnostics;
using MdReader.Core.Theming;
using MdReader.Mac.Interop;
using MdReader.Shell.Documents;

namespace MdReader.Mac;

/// <summary>
/// ARCHITECTURE §8.4 for WKWebView: the same table the Windows backend applies to a <c>CoreWebView2</c>, mapped onto
/// WebKit's configuration, preferences and delegates, and applied once per web view before the first navigation.
/// </summary>
/// <remarks>
/// <para>What is set here is only the half that is a property. The half that is an event — navigation, new windows,
/// permissions, downloads, credentials, script dialogs, the context menu — is the delegate in
/// <see cref="Interop.WkNative"/>, which is fail-closed in the same way the Windows handlers are: the restrictive
/// outcome first, and every exception caught.</para>
/// <para>Rows of the table WebKit answers by construction rather than by setting: there is no host-object surface, no
/// status bar, no password autosave, no form autofill, no <c>SaveAs</c> UI and no external-scheme launcher in a
/// <c>WKWebView</c> on macOS, so those rows need no code. The rows that have no WebKit equivalent at all are listed
/// in the port notes rather than silently dropped.</para>
/// </remarks>
public static class WkWebViewSecurity
{
    private const string Category = "WebViewSecurity";

    /// <summary>Applies the configuration half: preferences, media, the data store and the two handlers.</summary>
    /// <param name="configuration">A fresh <c>WKWebViewConfiguration</c>.</param>
    /// <param name="handler">The object that is both the scheme handler and the script message handler.</param>
    public static void ApplyToConfiguration(nint configuration, nint handler, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        nint preferences = ObjC.Send(configuration, ObjC.Selector("preferences"));

        // No window.open behind the user's back; a user-activated one still reaches createWebViewWith…, which routes
        // it as a link and returns nil.
        ObjC.SendVoidBool(preferences, ObjC.Selector("setJavaScriptCanOpenWindowsAutomatically:"), 0);

        // The Windows row is IsReputationCheckingRequired = false: only local app pages are ever navigated, and the
        // check would mean a network call at startup.
        TrySet(preferences, "setFraudulentWebsiteWarningEnabled:", 0, log, "fraudulent website warnings");

#if DEBUG
        TrySetValueForKey(preferences, "developerExtrasEnabled", true, log);
#else
        TrySetValueForKey(preferences, "developerExtrasEnabled", false, log);
#endif

        // Nothing plays on its own; the page has media-src 'none' anyway.
        ObjC.SendVoidLong(configuration, ObjC.Selector("setMediaTypesRequiringUserActionForPlayback:"),
                          WebKitConstants.AudiovisualMediaTypeAll);
        TrySet(configuration, "setAllowsAirPlayForMediaPlayback:", 0, log, "AirPlay");

        // Content may run script (the page is ours); the CSP is what keeps document content from doing so.
        nint webpagePreferences = ObjC.Send(configuration, ObjC.Selector("defaultWebpagePreferences"));
        if (webpagePreferences != 0 && ObjC.RespondsTo(webpagePreferences, "setAllowsContentJavaScript:"))
        {
            ObjC.SendVoidBool(webpagePreferences, ObjC.Selector("setAllowsContentJavaScript:"), 1);
        }

        // The two virtual hosts. WKURLSchemeHandler refuses every scheme WebKit handles itself, https included, so
        // both hosts live under our own scheme (ProtocolConstants.MacScheme) and one handler serves both.
        ObjC.SendVoid(configuration, ObjC.Selector("setURLSchemeHandler:forURLScheme:"),
                      handler, Foundation.String(MdReader.Core.Protocol.ProtocolConstants.WebScheme));

        nint controller = ObjC.Send(configuration, ObjC.Selector("userContentController"));
        ObjC.SendVoid(controller, ObjC.Selector("addScriptMessageHandler:name:"),
                      handler, Foundation.String(WebKitConstants.ScriptMessageHandlerName));
    }

    /// <summary>Applies the view half: gestures, magnification, link previews, the inspector and the delegates.</summary>
    public static void ApplyToWebView(nint webView, nint handler, AppTheme theme, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        ObjC.SendVoid(webView, ObjC.Selector("setNavigationDelegate:"), handler);
        ObjC.SendVoid(webView, ObjC.Selector("setUIDelegate:"), handler);

        // The Windows row is IsSwipeNavigationEnabled = false. There is nothing to go back to in any case.
        ObjC.SendVoidBool(webView, ObjC.Selector("setAllowsBackForwardNavigationGestures:"), 0);

        // Pinch magnification is not page zoom: it would scale the page behind the shell's back and make the zoom
        // setting a lie. Zoom stays the shell's, through pageZoom.
        ObjC.SendVoidBool(webView, ObjC.Selector("setAllowsMagnification:"), 0);

        // No link preview popovers (they fetch the target).
        TrySet(webView, "setAllowsLinkPreview:", 0, log, "link previews");

        // macOS 13.3+ gates the inspector per view; older systems use the preferences key above.
#if DEBUG
        TrySet(webView, "setInspectable:", 1, log, "the web inspector");
#else
        TrySet(webView, "setInspectable:", 0, log, "the web inspector");
#endif

        ApplyAppearance(webView, theme, log);
    }

    /// <summary>
    /// The browser's own colour scheme, which drives the page's UA styles. WebView2 has a profile-wide
    /// <c>PreferredColorScheme</c>; WKWebView follows its view's <c>NSAppearance</c>, so the view is given one
    /// explicitly rather than inheriting the window's.
    /// </summary>
    public static void ApplyAppearance(nint webView, AppTheme theme, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            string name = theme == AppTheme.Dark ? WebKitConstants.DarkAppearanceName : WebKitConstants.LightAppearanceName;
            nint appearance = ObjC.Send(ObjC.RequireClass("NSAppearance"), ObjC.Selector("appearanceNamed:"),
                                        Foundation.String(name));
            ObjC.SendVoid(webView, ObjC.Selector("setAppearance:"), appearance);
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Warning, Category, "Couldn't apply the theme to the web view.", ex);
        }
    }

    /// <summary>The colour the view paints before the page has drawn anything (§10).</summary>
    public static void ApplyBackgroundColor(nint webView, byte red, byte green, byte blue, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            // macOS 12+: what shows before the page's first paint and behind an over-scroll. On older systems the
            // container view underneath is already painted the same colour, so the worst case is the right colour
            // arriving a frame later rather than a white flash.
            if (ObjC.RespondsTo(webView, "setUnderPageBackgroundColor:"))
            {
                ObjC.SendVoid(webView, ObjC.Selector("setUnderPageBackgroundColor:"), Color(red, green, blue));
            }
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Warning, Category, "Couldn't apply the page background colour.", ex);
        }
    }

    /// <summary>An autoreleased <c>NSColor</c> in sRGB, opaque.</summary>
    internal static nint Color(byte red, byte green, byte blue) =>
        ObjC.SendFourDoubles(ObjC.RequireClass("NSColor"), ObjC.Selector("colorWithSRGBRed:green:blue:alpha:"),
                             red / 255.0, green / 255.0, blue / 255.0, 1.0);

    // The URL and log-text helpers live in the shared shell (PageOrigin, LogText): pure string work both backends
    // need verbatim. They are re-exposed here so this file reads the way WebViewSecurity does.

    /// True only for the viewer page with any query (and any fragment).
    public static bool IsPageUrl(string? value) => PageOrigin.IsPageUrl(value);

    /// Untrusted URL/href text for a log line: escaped and capped at 300 chars.
    public static string Shorten(string? value) => LogText.Shorten(value);

    /// Makes untrusted text safe for ONE log line.
    public static string ForLog(string? value, int maxChars = 2000) => LogText.ForLog(value, maxChars);

    /// Web message JSON for a log line: escaped and capped, because the page's text is untrusted.
    public static string Describe(string? json)
    {
        if (json is null)
        {
            return "(null)";
        }

        const int Max = 120;
        return json.Length <= Max
            ? ForLog(json, Max)
            : $"{ForLog(json[..Max], Max)}… ({json.Length} chars)";
    }

    private static nint NumberTrue() => Foundation.Number(1);

    private static nint NumberFalse() => Foundation.Number(0);

    /// <summary>A BOOL setter that only some macOS versions have. Missing is logged, never fatal.</summary>
    private static void TrySet(nint target, string selector, byte value, IAppLog log, string what)
    {
        if (ObjC.RespondsTo(target, selector))
        {
            ObjC.SendVoidBool(target, ObjC.Selector(selector), value);
            return;
        }

        log.Write(AppLogLevel.Info, Category, $"This macOS version can't configure {what} ({selector}).");
    }

    private static void TrySetValueForKey(nint target, string key, bool value, IAppLog log)
    {
        try
        {
            ObjC.SendVoid(target, ObjC.Selector("setValue:forKey:"), value ? NumberTrue() : NumberFalse(),
                          Foundation.String(key));
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Info, Category, $"This macOS version has no '{key}' preference.", ex);
        }
    }
}
