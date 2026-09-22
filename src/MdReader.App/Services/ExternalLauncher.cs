using System.ComponentModel;
using System.Diagnostics;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;

namespace MdReader.App.Services;

/// Opens http(s) links in the user's default browser. Every URI is re-validated here, independent of LinkClassifier.
public sealed class ExternalLauncher : IExternalLauncher
{
    private const string Category = "ExternalLauncher";

    private readonly CommandLineOptions _options;
    private readonly IAppLog _log;

    public ExternalLauncher(CommandLineOptions options, IAppLog log)
    {
        _options = options;
        _log = log;
    }

    public bool TryOpen(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!IsAllowed(uri))
        {
            _log.Write(AppLogLevel.Warning, Category, $"Refused to launch a non-http(s) URI: {uri.OriginalString}");
            return false;
        }

        var target = uri.AbsoluteUri;
        if (_options.IsTestMode)
        {
            // Tests must never open windows in the user's browser; the routing decision is still observable in the log.
            _log.Write(AppLogLevel.Info, Category, $"Test mode: not launching {target}");
            return true;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            _log.Write(AppLogLevel.Info, Category, $"Launched {target}");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _log.Write(AppLogLevel.Warning, Category, $"Couldn't launch {target}", ex);
            return false;
        }
    }

    private static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrEmpty(uri.Host)
        && string.IsNullOrEmpty(uri.UserInfo);
}
