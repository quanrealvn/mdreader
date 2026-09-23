using System.Runtime.InteropServices;
using MdReader.Core.Diagnostics;
using MdReader.Shell.Documents;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Edge;

/// <summary>
/// Find, print, PDF export and capture on a <see cref="CoreWebView2"/>: the capability half of
/// <see cref="IWebViewChannel"/> that needs nothing but the core object, so every WebView2-backed shell shares it.
/// UI thread only.
/// </summary>
public sealed class CoreWebView2Operations : IDisposable
{
    private const string Category = "WebViewFind";

    // HRESULTs meaning "the installed runtime doesn't implement the Find API".
    private const int EPointerNotImplemented = unchecked((int)0x80004001);   // E_NOTIMPL
    private const int ENoInterface = unchecked((int)0x80004002);             // E_NOINTERFACE
    private const int RegDbClassNotRegistered = unchecked((int)0x80040154);  // REGDB_E_CLASSNOTREG

    private readonly Func<CoreWebView2?> _core;
    private readonly IAppLog _log;
    private CoreWebView2Find? _find;
    private FindSession? _session;
    private bool _disposed;

    public CoreWebView2Operations(Func<CoreWebView2?> core, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(log);
        _core = core;
        _log = log;
    }

    /// <summary>
    /// Stops the running session and starts a new one for <paramref name="term"/>; null when there is no live page.
    /// Everything up to the native <c>StartAsync</c> runs synchronously, so the caller's bookkeeping stays in step.
    /// </summary>
    public async Task<FindSession?> StartFindAsync(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        CoreWebView2? core = _disposed ? null : _core();
        if (core is null)
        {
            return null;
        }

        CoreWebView2Find find = Translate(() => EnsureFind(core));
        FindSession session = _session ??= new FindSession();
        Translate(find.Stop);
        if (term.Length == 0)
        {
            return session;
        }

        CoreWebView2FindOptions options = Translate(core.Environment.CreateFindOptions);
        options.FindTerm = term;
        options.IsCaseSensitive = false;
        options.ShouldMatchWord = false;
        options.SuppressDefaultFindDialog = true;
        options.ShouldHighlightAllMatches = true;
        await find.StartAsync(options);
        PushCounters();
        return session;
    }

    public void FindNext() => Translate(() =>
    {
        _find?.FindNext();
    });

    public void FindPrevious() => Translate(() =>
    {
        _find?.FindPrevious();
    });

    /// Ends the native find session (clears highlights). Safe to call at any time; never throws.
    public void StopFind()
    {
        try
        {
            _find?.Stop();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "Find.Stop failed.", ex);
        }

        PushCounters();
    }

    public Task ShowPrintUiAsync()
    {
        CoreWebView2 core = RequireCore();
        core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        return Task.CompletedTask;
    }

    public Task<bool> PrintToPdfAsync(string pdfPath)
    {
        CoreWebView2 core = RequireCore();
        CoreWebView2PrintSettings settings = core.Environment.CreatePrintSettings();
        settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false;
        return core.PrintToPdfAsync(pdfPath, settings);
    }

    public Task CapturePreviewAsync(Stream pngDestination)
    {
        ArgumentNullException.ThrowIfNull(pngDestination);
        CoreWebView2 core = RequireCore();
        return core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, pngDestination);
    }

    /// The CoreWebView2 is going away: drop the find session with it.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_find is { } find)
        {
            _find = null;
            try
            {
                find.MatchCountChanged -= OnCountersChanged;
                find.ActiveMatchIndexChanged -= OnCountersChanged;
            }
            catch (Exception ex)
            {
                _log.Write(AppLogLevel.Debug, Category, "Unsubscribing from the find session failed.", ex);
            }
        }

        _session = null;
    }

    private CoreWebView2 RequireCore() =>
        (_disposed ? null : _core()) ?? throw new InvalidOperationException("The document's WebView isn't ready.");

    private CoreWebView2Find EnsureFind(CoreWebView2 core)
    {
        if (_find is null)
        {
            CoreWebView2Find find = core.Find;
            find.MatchCountChanged += OnCountersChanged;
            find.ActiveMatchIndexChanged += OnCountersChanged;
            _find = find;
        }

        return _find;
    }

    private void OnCountersChanged(object? sender, object e) => PushCounters();

    private void PushCounters()
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            session.UpdateCounters(_find?.MatchCount ?? 0, _find?.ActiveMatchIndex ?? 0);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "Reading the match count failed.", ex);
        }
    }

    /// Only "this runtime has no Find API" is permanent (R15); it is surfaced as FindNotSupportedException so the shell's
    /// find bar can disable itself. Anything else (a renderer that crashed or was busy, a transient COM error) is left
    /// as it is and retried by the caller.
    private static T Translate<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (IsUnsupported(ex))
        {
            throw new FindNotSupportedException("The installed WebView2 Runtime has no Find API.", ex);
        }
    }

    private static void Translate(Action action) => Translate<object?>(() =>
    {
        action();
        return null;
    });

    private static bool IsUnsupported(Exception ex) =>
        ex is NotImplementedException
        || (ex is COMException or InvalidCastException && ex.HResult is EPointerNotImplemented or ENoInterface or RegDbClassNotRegistered);
}
