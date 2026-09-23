using System.Collections.Frozen;
using MdReader.Shell.Documents;
using MdReader.Core.Diagnostics;
using MdReader.Core.Theming;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Edge;

/// <summary>
/// Every WebView2 hardening setting and event filter from ARCHITECTURE §8.4, applied once per WebView right after
/// <c>EnsureCoreWebView2Async</c> and before the first navigation.
/// </summary>
/// <remarks>
/// Every handler is fail-closed: it first sets the restrictive outcome (Cancel / Handled / Deny) and only then decides
/// whether to relax it, and it catches and logs every exception, so a bug can never turn into an allowed navigation,
/// popup, download or permission.
/// </remarks>
public static class WebViewSecurity
{
    private const string Category = "WebViewSecurity";

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
    public static void Apply(CoreWebView2 core, AppTheme theme, IAppLog log, Action<Uri, bool> routeLink)
    {
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

        ApplyPreferredColorScheme(core, theme, log);

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

    /// <summary>
    /// The colour the WebView paints before the page has drawn anything (no white flash, §10). It lives on the control
    /// in WPF and on the controller everywhere else, so the shell sets it; this is only the value.
    /// </summary>
    public static System.Drawing.Color PageBackgroundColor(AppTheme theme)
    {
        (byte r, byte g, byte b) = ThemePalette.PageBackground(theme);
        return System.Drawing.Color.FromArgb(255, r, g, b);
    }

    /// <summary>The shared profile's PreferredColorScheme, which drives the page's UA styles. UI thread only.</summary>
    public static void ApplyPreferredColorScheme(CoreWebView2 core, AppTheme theme, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            core.Profile.PreferredColorScheme = theme == AppTheme.Dark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Warning, Category, "Couldn't apply the theme to the WebView.", ex);
        }
    }

    // The URL and log-text helpers below live in the shared shell (PageOrigin, LogText): they are pure string work the
    // macOS backend needs verbatim. They are re-exposed here so the §8.4 handlers read the way they always have.

    /// True only for https://app.mdreader.example/index.html with any query (and any fragment).
    public static bool IsPageUrl(string? value) => PageOrigin.IsPageUrl(value);

    /// §7.1 rule 7: compare scheme + host of <c>new Uri(source)</c>, never the raw string.
    public static bool IsAppOrigin(string? source) => PageOrigin.IsAppOrigin(source);

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
