using System.IO;
using MdReader.Shell.Threading;
using MdReader.Shell.Documents;
using MdReader.Shell.ViewModels;
using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Paths;
using MdReader.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace MdReader.Shell.Services;

/// <summary>
/// The only entry point for opening documents (ARCHITECTURE §4.10, §4.11): reuses an existing tab for the same path,
/// otherwise creates a <see cref="DocumentSession"/> + <see cref="DocumentTabViewModel"/> via ActivatorUtilities and
/// appends it to the <see cref="ITabHost"/>. Maintains the recent-files list.
/// </summary>
/// <remarks>
/// <see cref="ITabHost"/> and <see cref="IStatusNotifier"/> are both implemented by MainViewModel, which itself may depend
/// on <see cref="IDocumentOpener"/>; they are resolved lazily on first use so the container never sees a cycle.
/// Consequently <see cref="Open"/> must not be called from MainViewModel's constructor.
/// </remarks>
public sealed class DocumentOpener : IDocumentOpener
{
    private const string Category = "DocumentOpener";

    private readonly IServiceProvider _services;
    private readonly IUiDispatcher _dispatcher;
    private readonly SettingsCoordinator _settings;
    private readonly IAppLog _log;
    private ITabHost? _tabHost;
    private IStatusNotifier? _status;
    private int _lastDocId;

    public DocumentOpener(IServiceProvider services, SettingsCoordinator settings, IUiDispatcher dispatcher, IAppLog log)
    {
        _services = services;
        _dispatcher = dispatcher;
        _settings = settings;
        _log = log;
    }

    private ITabHost TabHost => _tabHost ??= _services.GetRequiredService<ITabHost>();

    private IStatusNotifier Status => _status ??= _services.GetRequiredService<IStatusNotifier>();

    public void Open(string path, string? fragment = null, bool activate = true)
    {
        if (!_dispatcher.CheckAccess())
        {
            _log.Write(AppLogLevel.Warning, Category, "Open was called off the UI thread; marshalling it.");
            _dispatcher.Post(() => Open(path, fragment, activate));
            return;
        }

        try
        {
            OpenCore(path, fragment, activate);
        }
        catch (Exception ex)
        {
            // Never throws: bad files show an error view in their tab; this only covers unusable paths and bugs.
            _log.Write(AppLogLevel.Error, Category, $"Couldn't open '{LogText.ForLog(path, 500)}'.", ex);
            TryShowStatus($"Couldn't open {SafeFileName(path)}.");
        }
    }

    private void OpenCore(string path, string? fragment, bool activate)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // A fully qualified path is normalized lexically, through the platform's own rules: Path.GetFullPath would
        // expand 8.3 short names for any path containing '~', which reaches the network for a UNC path (NTLM) — and
        // link targets come from Markdown content. Anything else is relative to the process, which only Path.GetFullPath
        // can resolve, and is normalized there for the tab reuse below.
        PathPolicy policy = PathPolicy.Current;
        string fullPath = policy.NormalizeFullPath(path) ?? Path.GetFullPath(path);
        string? scrollTo = string.IsNullOrEmpty(fragment) ? null : fragment;
        ITabHost host = TabHost;

        foreach (IDocumentTab existing in host.Tabs)
        {
            if (string.Equals(existing.FilePath, fullPath, policy.Comparison))
            {
                if (activate)
                {
                    host.Activate(existing);
                }

                if (scrollTo is not null)
                {
                    existing.ScrollTo(scrollTo);
                }

                return;
            }
        }

        int docId = ++_lastDocId;
        _log.Write(AppLogLevel.Info, Category, $"Opening {fullPath} (doc {docId}).");
        DocumentSession? session = null;
        try
        {
            // The session starts load + render on the thread pool right away (§4.11 opening flow).
            session = ActivatorUtilities.CreateInstance<DocumentSession>(_services, new DocumentStartInfo(fullPath, docId, scrollTo));
            TrackRecentFile(session, fullPath);
            var tab = ActivatorUtilities.CreateInstance<DocumentTabViewModel>(_services, session);
            host.Add(tab, activate);
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    /// After the first successful load the path goes to the top of RecentFiles; a first load that finds nothing removes it.
    private void TrackRecentFile(DocumentSession session, string fullPath)
    {
        void OnLoadCompleted(object? sender, DocumentLoadCompletedEventArgs e)
        {
            if (e.Error is null)
            {
                session.LoadCompleted -= OnLoadCompleted;
                UpdateRecentFiles(fullPath, add: true);
            }
            else if (e.Error == DocumentLoadError.NotFound && e.IsFirstAttempt)
            {
                UpdateRecentFiles(fullPath, add: false);
            }
        }

        session.LoadCompleted += OnLoadCompleted;
    }

    private void UpdateRecentFiles(string fullPath, bool add)
    {
        try
        {
            _settings.Update(s => s with
            {
                RecentFiles = add ? RecentFiles.Add(s.RecentFiles, fullPath) : RecentFiles.Remove(s.RecentFiles, fullPath),
            });
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, $"Couldn't update the recent files for {fullPath}.", ex);
        }
    }

    private void TryShowStatus(string message)
    {
        try
        {
            Status.ShowStatus(message);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Couldn't show a status message.", ex);
        }
    }

    private static string SafeFileName(string? path)
    {
        try
        {
            string name = Path.GetFileName(path ?? "");
            return string.IsNullOrEmpty(name) ? "the file" : name;
        }
        catch (ArgumentException)
        {
            return "the file";
        }
    }
}
