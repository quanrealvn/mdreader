using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using MdReader.Core.Diagnostics;
using MdReader.Core.Protocol;
using MdReader.Core.Theming;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MdReader.App.Documents;

/// <summary>
/// Every WebView2 hardening setting and event filter from ARCHITECTURE §8.4, applied once per WebView right after
/// <c>EnsureCoreWebView2Async</c> and before the first navigation.
/// </summary>
/// <remarks>
/// Every handler is fail-closed: it first sets the restrictive outcome (Cancel / Handled / Deny) and only then decides
/// whether to relax it, and it catches and logs every exception, so a bug can never turn into an allowed navigation,
/// popup, download or permission.
/// </remarks>
internal static class WebViewSecurity
{
    private const string Category = "WebViewSecurity";
    private const int MaxLoggedUriChars = 300;

    /// Context-menu items that survive the filter (CoreWebView2ContextMenuItem.Name, lower-camel English label).
    private static readonly FrozenSet<string> AllowedContextMenuItems = FrozenSet.ToFrozenSet(
        [
            "copy", "selectAll", "copyLinkLocation", "copyImage",
#if DEBUG
            "inspectElement",
#endif
        ],
        StringComparer.Ordinal);

#if DEBUG
    private static readonly HashSet<string> LoggedContextMenuItemNames = new(StringComparer.Ordinal);
#endif

    /// <summary>Applies the §8.4 table to one WebView. UI thread only.</summary>
    /// <param name="routeLink">A user-initiated navigation or new-window request to an http(s) or <c>file:</c> URL; the
    /// bool is "new tab". It MUST go through the LinkClassifier: the URL is author-controlled (a click the page script
    /// missed, a dragged link), so it must never reach Path/File APIs directly — <c>Path.GetFullPath</c> on a UNC path
    /// with <c>~</c> touches the network (NTLM). The classifier gates UNC lexically and returns a normalized path.</param>
    public static void Apply(WebView2 webView, CoreWebView2 core, AppTheme theme, IAppLog log, Action<Uri, bool> routeLink)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(routeLink);

        CoreWebView2Settings settings = core.Settings;
#if DEBUG
        settings.AreDevToolsEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.IsReputationCheckingRequired = false;
        settings.IsWebMessageEnabled = true;
        settings.IsScriptEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;   // filtered by ContextMenuRequested below

        webView.AllowExternalDrop = true;
        ApplyTheme(webView, core, theme, log);

        core.ContextMenuRequested += (_, e) => OnContextMenuRequested(e, log);
        core.NavigationStarting += (_, e) => OnNavigationStarting(e, log, routeLink);
        core.NewWindowRequested += (_, e) => OnNewWindowRequested(e, log, routeLink);
        core.FrameNavigationStarting += (_, e) => Guard(log, nameof(CoreWebView2.FrameNavigationStarting), () =>
        {
            e.Cancel = true;
            log.Write(AppLogLevel.Info, Category, $"Blocked frame navigation to {Shorten(e.Uri)}.");
        });
        core.PermissionRequested += (_, e) => Guard(log, nameof(CoreWebView2.PermissionRequested), () =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            log.Write(AppLogLevel.Info, Category, $"Denied permission {e.PermissionKind} for {Shorten(e.Uri)}.");
        });
        core.DownloadStarting += (_, e) => Guard(log, nameof(CoreWebView2.DownloadStarting), () =>
        {
            e.Cancel = true;
            e.Handled = true;
            log.Write(AppLogLevel.Info, Category, "Blocked a download.");
        });
        core.LaunchingExternalUriScheme += (_, e) => Guard(log, nameof(CoreWebView2.LaunchingExternalUriScheme), () =>
        {
            e.Cancel = true;
            log.Write(AppLogLevel.Info, Category, $"Blocked external URI scheme launch: {Shorten(e.Uri)}.");
        });
        core.BasicAuthenticationRequested += (_, e) => Guard(log, nameof(CoreWebView2.BasicAuthenticationRequested), () =>
        {
            e.Cancel = true;
            log.Write(AppLogLevel.Info, Category, $"Cancelled a basic authentication request from {Shorten(e.Uri)}.");
        });
        core.ClientCertificateRequested += (_, e) => Guard(log, nameof(CoreWebView2.ClientCertificateRequested), () =>
        {
            e.Cancel = true;
            e.Handled = true;
            log.Write(AppLogLevel.Info, Category, $"Cancelled a client certificate request from {Shorten(e.Host)}.");
        });
        core.ScreenCaptureStarting += (_, e) => Guard(log, nameof(CoreWebView2.ScreenCaptureStarting), () =>
        {
            e.Cancel = true;
            e.Handled = true;
            log.Write(AppLogLevel.Info, Category, "Blocked a screen capture request.");
        });

        // Beyond the §8.4 table (defense in depth): "Save page as" is reachable through the browser's Ctrl+S when
        // accelerator keys are on (DEBUG); the context menu never offers it.
        core.SaveAsUIShowing += (_, e) => Guard(log, nameof(CoreWebView2.SaveAsUIShowing), () =>
        {
            e.Cancel = true;
            log.Write(AppLogLevel.Info, Category, "Blocked the Save As dialog.");
        });
    }

    /// <summary>DefaultBackgroundColor (no white flash) + the shared profile's PreferredColorScheme. UI thread only.</summary>
    public static void ApplyTheme(WebView2 webView, CoreWebView2? core, AppTheme theme, IAppLog log)
    {
        try
        {
            (byte r, byte g, byte b) = ThemePalette.PageBackground(theme);
            webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, r, g, b);
            if (core is not null)
            {
                core.Profile.PreferredColorScheme = theme == AppTheme.Dark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Warning, Category, "Couldn't apply the theme to the WebView.", ex);
        }
    }

    /// True only for https://app.mdreader.example/index.html with any query (and any fragment).
    public static bool IsPageUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !IsAppOrigin(uri))
        {
            return false;
        }

        return string.Equals(uri.AbsolutePath, "/index.html", StringComparison.Ordinal);
    }

    /// §7.1 rule 7: compare scheme + host of <c>new Uri(source)</c>, never the raw string.
    public static bool IsAppOrigin(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && IsAppOrigin(uri);

    /// Untrusted URL/href text for a log line: escaped (see <see cref="ForLog"/>) and capped at 300 chars.
    public static string Shorten(string? value) => ForLog(value, MaxLoggedUriChars);

    /// <summary>
    /// Makes untrusted text (document-derived web log messages, hrefs, URLs, parser errors) safe for ONE log line:
    /// CR/LF/tab and every other control character (C0, DEL, C1), line/paragraph separators and bidi overrides are
    /// escaped, and the result is capped, so content can never forge or disguise log lines.
    /// </summary>
    public static string ForLog(string? value, int maxChars = 2000)
    {
        if (value is null)
        {
            return "(null)";
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxChars) + 8);
        foreach (char c in value)
        {
            if (builder.Length >= maxChars)
            {
                builder.Append('…');
                break;
            }

            switch (c)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(c) || IsInvisibleFormatting(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// Line/paragraph separators (U+2028, U+2029) and bidi embedding/override/isolate controls (U+202A..U+202E,
    /// U+2066..U+2069): not char.IsControl, but they break lines or visually reorder a log line. Numeric on purpose,
    /// so the source file stays plain ASCII.
    private static bool IsInvisibleFormatting(char c) =>
        c == (char)0x2028 || c == (char)0x2029
        || (c >= (char)0x202A && c <= (char)0x202E)
        || (c >= (char)0x2066 && c <= (char)0x2069);

    private static bool IsAppOrigin(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, ProtocolConstants.AppHost, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    private static void OnNavigationStarting(CoreWebView2NavigationStartingEventArgs e, IAppLog log, Action<Uri, bool> routeLink)
    {
        try
        {
            e.Cancel = true;
            string target = e.Uri;
            if (IsPageUrl(target))
            {
                e.Cancel = false;
                return;
            }

            bool userInitiated = e.IsUserInitiated;
            log.Write(AppLogLevel.Info, Category, $"Blocked navigation to {Shorten(target)} (user initiated: {userInitiated}).");
            if (!userInitiated || !Uri.TryCreate(target, UriKind.Absolute, out Uri? uri))
            {
                return;
            }

            // file: = a file dropped before the page's drop handler ran, or a link the page script missed. Both go
            // through the classifier: Markdown opens, UNC outside the document's share is blocked before any I/O.
            if (uri.IsFile || IsHttp(uri))
            {
                routeLink(uri, false);
            }
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            log.Write(AppLogLevel.Error, Category, "NavigationStarting handler failed; the navigation was cancelled.", ex);
        }
    }

    private static void OnNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e, IAppLog log, Action<Uri, bool> routeLink)
    {
        try
        {
            e.Handled = true;   // never open a popup window
            string target = e.Uri;
            bool userInitiated = e.IsUserInitiated;
            log.Write(AppLogLevel.Info, Category, $"Blocked new window for {Shorten(target)} (user initiated: {userInitiated}).");
            if (userInitiated
                && Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
                && (IsHttp(uri) || uri.IsFile))
            {
                routeLink(uri, true);   // classifier → external / open (activate = false) / blocked
            }
        }
        catch (Exception ex)
        {
            e.Handled = true;
            log.Write(AppLogLevel.Error, Category, "NewWindowRequested handler failed; the request was blocked.", ex);
        }
    }

    private static void OnContextMenuRequested(CoreWebView2ContextMenuRequestedEventArgs e, IAppLog log)
    {
        try
        {
            IList<CoreWebView2ContextMenuItem> items = e.MenuItems;
#if DEBUG
            LogContextMenuItemNames(items, log);
#endif
            for (int i = items.Count - 1; i >= 0; i--)
            {
                CoreWebView2ContextMenuItem item = items[i];
                if (item.Kind == CoreWebView2ContextMenuItemKind.Separator)
                {
                    continue;
                }

                if (item.Kind == CoreWebView2ContextMenuItemKind.Submenu || !AllowedContextMenuItems.Contains(item.Name))
                {
                    items.RemoveAt(i);
                }
            }

            // Separators: no leading, trailing or doubled ones.
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i].Kind != CoreWebView2ContextMenuItemKind.Separator)
                {
                    continue;
                }

                bool leading = i == 0;
                bool trailing = i == items.Count - 1;
                bool doubled = i > 0 && items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator;
                if (leading || trailing || doubled)
                {
                    items.RemoveAt(i);
                }
            }

            if (items.Count == 0)
            {
                e.Handled = true;   // nothing left: show no menu at all
            }
        }
        catch (Exception ex)
        {
            e.Handled = true;   // fail closed: no menu rather than an unfiltered one
            log.Write(AppLogLevel.Error, Category, "ContextMenuRequested handler failed; the menu was suppressed.", ex);
        }
    }

#if DEBUG
    // Risk R4: the item names are only observable with a real right-click. Log every list that contains a name we
    // haven't seen yet in this process, so the allow-list can be confirmed from the log.
    private static void LogContextMenuItemNames(IList<CoreWebView2ContextMenuItem> items, IAppLog log)
    {
        var names = new List<string>(items.Count);
        bool hasNewName = false;
        lock (LoggedContextMenuItemNames)
        {
            foreach (CoreWebView2ContextMenuItem item in items)
            {
                string name = item.Kind == CoreWebView2ContextMenuItemKind.Separator ? "|" : item.Name;
                names.Add(name);
                if (item.Kind != CoreWebView2ContextMenuItemKind.Separator)
                {
                    hasNewName |= LoggedContextMenuItemNames.Add(name);
                }
            }
        }

        if (hasNewName)
        {
            log.Write(AppLogLevel.Info, Category, "Context menu items (R4): " + string.Join(", ", names));
        }
    }
#endif

    private static bool IsHttp(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static void Guard(IAppLog log, string eventName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Error, Category, $"{eventName} handler failed.", ex);
        }
    }
}
