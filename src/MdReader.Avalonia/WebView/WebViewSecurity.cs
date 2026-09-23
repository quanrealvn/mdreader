using System.Globalization;
using System.Text;
using MdReader.Core.Diagnostics;
using MdReader.Core.Protocol;
using MdReader.Core.Theming;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Avalonia.WebView;

/// <summary>
/// The ARCHITECTURE §8.4 hardening, applied to the spike's single web view right after the controller is created and
/// before the first navigation. It is deliberately a near-copy of <c>MdReader.App.Documents.WebViewSecurity</c>: the
/// point of phase 1 is to find out how much of that file is WPF-shaped. The answer, in this file, is "the two lines
/// that touch the WPF control" — everything else works against <see cref="CoreWebView2"/> and
/// <see cref="CoreWebView2Controller"/>, so it belongs in the shared shell layer.
/// </summary>
/// <remarks>
/// Every handler is fail-closed: it sets the restrictive outcome first (Cancel / Handled / Deny), only then decides
/// whether to relax it, and catches everything, so a bug can never become an allowed navigation, popup or download.
/// Phase-1 scope: the items the spike needs, plus the free ones. The context-menu filter is intentionally left out.
/// </remarks>
internal static class WebViewSecurity
{
    private const string Category = "WebViewSecurity";
    private const int MaxLoggedUriChars = 300;

    /// <summary>UI thread only. <paramref name="routeLink"/> gets user-initiated http(s)/file: targets; the bool is "new tab".</summary>
    public static void Apply(CoreWebView2Controller controller, AppTheme theme, IAppLog log, Action<Uri, bool> routeLink)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(routeLink);

        CoreWebView2 core = controller.CoreWebView2;
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

        ApplyTheme(controller, theme, log);

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
        core.SaveAsUIShowing += (_, e) => Guard(log, nameof(CoreWebView2.SaveAsUIShowing), () =>
        {
            e.Cancel = true;
            log.Write(AppLogLevel.Info, Category, "Blocked the Save As dialog.");
        });
    }

    /// <summary>
    /// No white flash while the page loads, and the shared profile's colour scheme.
    /// In WPF this reads <c>WebView2.DefaultBackgroundColor</c>; the controller carries the same property, which is why
    /// the WPF control is not needed here.
    /// </summary>
    public static void ApplyTheme(CoreWebView2Controller controller, AppTheme theme, IAppLog log)
    {
        try
        {
            (byte r, byte g, byte b) = ThemePalette.PageBackground(theme);
            controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, r, g, b);
            controller.CoreWebView2.Profile.PreferredColorScheme = theme == AppTheme.Dark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Warning, Category, "Couldn't apply the theme to the web view.", ex);
        }
    }

    /// <summary>True only for https://app.mdreader.example/index.html with any query (and any fragment).</summary>
    public static bool IsPageUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && IsAppOrigin(uri)
        && string.Equals(uri.AbsolutePath, "/index.html", StringComparison.Ordinal);

    /// <summary>§7.1 rule 7: compare scheme + host of <c>new Uri(source)</c>, never the raw string.</summary>
    public static bool IsAppOrigin(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && IsAppOrigin(uri);

    /// <summary>Untrusted URL/href text for one log line: escaped and capped.</summary>
    public static string Shorten(string? value) => ForLog(value, MaxLoggedUriChars);

    /// <summary>
    /// Makes untrusted text safe for ONE log line: CR/LF/tab, control characters, line/paragraph separators and bidi
    /// overrides are escaped and the result is capped, so content can never forge or disguise a log line.
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
            if (IsPageUrl(e.Uri))
            {
                e.Cancel = false;
                return;
            }

            bool userInitiated = e.IsUserInitiated;
            log.Write(AppLogLevel.Info, Category, $"Blocked navigation to {Shorten(e.Uri)} (user initiated: {userInitiated}).");
            if (userInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) && (uri.IsFile || IsHttp(uri)))
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
            bool userInitiated = e.IsUserInitiated;
            log.Write(AppLogLevel.Info, Category, $"Blocked a new window for {Shorten(e.Uri)} (user initiated: {userInitiated}).");
            if (userInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) && (IsHttp(uri) || uri.IsFile))
            {
                routeLink(uri, true);
            }
        }
        catch (Exception ex)
        {
            e.Handled = true;
            log.Write(AppLogLevel.Error, Category, "NewWindowRequested handler failed; the request was blocked.", ex);
        }
    }

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
