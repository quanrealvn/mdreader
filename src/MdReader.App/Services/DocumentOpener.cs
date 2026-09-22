using System.IO;
using System.Windows;
using MdReader.App.Documents;
using MdReader.App.ViewModels;
using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace MdReader.App.Services;

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
    private readonly SettingsCoordinator _settings;
    private readonly IAppLog _log;
    private ITabHost? _tabHost;
    private IStatusNotifier? _status;
    private int _lastDocId;

    public DocumentOpener(IServiceProvider services, SettingsCoordinator settings, IAppLog log)
    {
        _services = services;
        _settings = settings;
        _log = log;
    }

    private ITabHost TabHost => _tabHost ??= _services.GetRequiredService<ITabHost>();

    private IStatusNotifier Status => _status ??= _services.GetRequiredService<IStatusNotifier>();

    public void Open(string path, string? fragment = null, bool activate = true)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _log.Write(AppLogLevel.Warning, Category, "Open was called off the UI thread; marshalling it.");
            dispatcher.BeginInvoke(() => Open(path, fragment, activate));
            return;
        }

        try
        {
            OpenCore(path, fragment, activate);
        }
        catch (Exception ex)
        {
            // Never throws: bad files show an error view in their tab; this only covers unusable paths and bugs.
            _log.Write(AppLogLevel.Error, Category, $"Couldn't open '{WebViewSecurity.ForLog(path, 500)}'.", ex);
            TryShowStatus($"Couldn't open {SafeFileName(path)}.");
        }
    }

    private void OpenCore(string path, string? fragment, bool activate)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // Path.GetFullPath is lexical only while the path has no '~': with one it calls GetLongPathNameW, which touches
        // the network for UNC paths (NTLM) — and link targets come from Markdown content. Fully-qualified paths arrive
        // normalized from their sources (LinkClassifier, the CLI parser, the pipe client, dialogs, drops), so such a
        // path is used as is; everything else is still normalized for the OrdinalIgnoreCase tab reuse below.
        string fullPath = Path.IsPathFullyQualified(path) && path.Contains('~', StringComparison.Ordinal)
            ? path
            : Path.GetFullPath(path);
        string? scrollTo = string.IsNullOrEmpty(fragment) ? null : fragment;
        ITabHost host = TabHost;

        foreach (IDocumentTab existing in host.Tabs)
        {
            if (string.Equals(existing.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
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
