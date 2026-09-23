using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;
using MdReader.Mac.Interop;
using MdReader.Shell.Services;

namespace MdReader.Mac;

/// <summary>
/// What every tab's web view shares: one <c>WKProcessPool</c> (so the tabs' web content lives in one pool of
/// processes, as WebView2's shared user data folder does) and one <c>WKWebsiteDataStore</c>.
/// </summary>
/// <remarks>
/// <para><b>The data store is non-persistent, deliberately.</b> WebView2 keeps a profile on disk because it has to;
/// WKWebView does not, and nothing the viewer page does needs to survive a restart — there are no cookies, no
/// logins, no local storage and no service workers (the CSP forbids workers, and <c>connect-src 'none'</c> forbids
/// the network). A non-persistent store means a remote badge can't leave anything behind either. The cost is that
/// remote images are re-fetched each run, and that <c>AppPaths.WebView2UserDataFolder</c> is unused on macOS. Giving
/// it a folder of its own would need <c>+[WKWebsiteDataStore dataStoreForIdentifier:]</c> (macOS 14+), which takes a
/// UUID rather than a path, so it would not be that folder anyway.</para>
/// </remarks>
public sealed class WkWebViewEnvironment
{
    private const string Category = "WebKit";

    private readonly IAppLog _log;
    private readonly nint _processPool;
    private readonly nint _dataStore;

    internal WkWebViewEnvironment(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _processPool = ObjC.Retain(ObjC.New(ObjC.RequireClass("WKProcessPool")));
        _dataStore = ObjC.Retain(ObjC.Send(ObjC.RequireClass("WKWebsiteDataStore"),
                                           ObjC.Selector("nonPersistentDataStore")));
    }

    /// <summary>
    /// A configuration for one web view, with the shared pieces in place and the §8.4 settings applied. The scheme
    /// handler and the script message handler are per view, because they route to that view's channel.
    /// </summary>
    public nint CreateConfiguration(nint handler)
    {
        nint configuration = ObjC.New(ObjC.RequireClass("WKWebViewConfiguration"));
        ObjC.SendVoid(configuration, ObjC.Selector("setProcessPool:"), _processPool);
        ObjC.SendVoid(configuration, ObjC.Selector("setWebsiteDataStore:"), _dataStore);
        WkWebViewSecurity.ApplyToConfiguration(configuration, handler, _log);
        return configuration;
    }
}

/// <summary>
/// The macOS counterpart of <c>WebView2EnvironmentProvider</c>. There is nothing to download and nothing to wait
/// for — WebKit ships with the system — so this is a one-line provider whose only real job is to fail loudly if the
/// frameworks aren't there, which on a Mac means the process isn't running as an application bundle.
/// </summary>
public sealed class WkWebViewEnvironmentProvider : IWebViewEnvironmentProvider
{
    private const string Category = "WebKit";

    private readonly IAppLog _log;
    private readonly IPerfRecorder _perf;
    private WkWebViewEnvironment? _environment;
    private Exception? _failure;

    public WkWebViewEnvironmentProvider(AppPaths paths, IAppLog log, IPerfRecorder perf)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(perf);
        _log = log;
        _perf = perf;
    }

    public void Start() => _ = TryCreate();

    public Task WhenReadyAsync()
    {
        WkWebViewEnvironment? environment = TryCreate();
        return environment is not null ? Task.CompletedTask : Task.FromException(_failure!);
    }

    /// <summary>The shared environment; throws with the platform's error if WebKit isn't usable.</summary>
    public WkWebViewEnvironment Get() => TryCreate() ?? throw _failure!;

    private WkWebViewEnvironment? TryCreate()
    {
        if (_environment is not null || _failure is not null)
        {
            return _environment;
        }

        try
        {
            _environment = ObjC.WithPool(() => new WkWebViewEnvironment(_log));
            _perf.Mark(PerfMarks.WebViewEnvironmentReady);
            _log.Write(AppLogLevel.Info, Category, "WebKit environment ready");
        }
        catch (Exception ex)
        {
            _failure = ex;
            _log.Write(AppLogLevel.Error, Category, "Couldn't create the WebKit environment", ex);
        }

        return _environment;
    }
}
