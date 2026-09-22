namespace MdReader.Core.Settings;

// Owned by WP4 (ARCHITECTURE §2.2, §4.6).
// Applied by JsonSettingsStore.Load and every SettingsCoordinator.Update.
public static class AppSettingsNormalizer
{
    /// Clamp Zoom (ZoomLevels.Clamp; NaN → Default), dedupe/trim RecentFiles (rooted paths only, max 10),
    /// drop Window if Width/Height < 200 or any value is NaN/∞, undefined ReadingStyle → Colorful,
    /// Session: fully-qualified paths only, trimmed, case-insensitive unique, max SessionState.MaxFiles, ActiveFile one of
    /// them (else null); unknown SchemaVersion → defaults.
    public static AppSettings Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            return new AppSettings();
        }

        return settings with
        {
            Zoom = double.IsNaN(settings.Zoom) ? ZoomLevels.Default : ZoomLevels.Clamp(settings.Zoom),
            Window = NormalizeWindow(settings.Window),
            RecentFiles = NormalizeRecentFiles(settings.RecentFiles),
            ReadingStyle = Enum.IsDefined(settings.ReadingStyle) ? settings.ReadingStyle : ReadingStyle.Colorful,
            Session = NormalizeSession(settings.Session),
        };
    }

    private static SessionState? NormalizeSession(SessionState? session)
    {
        if (session is null)
        {
            return null;
        }

        // Files can be null at runtime despite the annotation (a positional record deserialized without "files").
        var files = new List<string>(Math.Min(session.Files?.Count ?? 0, SessionState.MaxFiles));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in session.Files ?? [])
        {
            if (files.Count >= SessionState.MaxFiles)
            {
                break;
            }

            var path = raw?.Trim();
            if (!string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && seen.Add(path))
            {
                files.Add(path);
            }
        }

        var active = session.ActiveFile?.Trim();
        var activeFile = string.IsNullOrEmpty(active)
            ? null
            : files.Find(f => string.Equals(f, active, StringComparison.OrdinalIgnoreCase));
        return new SessionState(files, activeFile);
    }

    private static WindowPlacement? NormalizeWindow(WindowPlacement? window)
    {
        if (window is null)
        {
            return null;
        }

        if (window.Width < 200 || window.Height < 200)
        {
            return null;
        }

        if (!IsFinite(window.Left) || !IsFinite(window.Top) || !IsFinite(window.Width) || !IsFinite(window.Height))
        {
            return null;
        }

        return window;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static IReadOnlyList<string> NormalizeRecentFiles(IReadOnlyList<string>? recentFiles)
    {
        // A hand-edited settings.json (or `Update(s => s with { RecentFiles = null! })`) can legally produce a null
        // reference here at runtime despite the non-nullable annotation; treat it the same as an empty list.
        if (recentFiles is null)
        {
            return [];
        }

        var result = new List<string>(Math.Min(recentFiles.Count, RecentFiles.MaxCount));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in recentFiles)
        {
            if (result.Count >= RecentFiles.MaxCount)
            {
                break;
            }

            var path = raw?.Trim();
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                continue;
            }

            if (seen.Add(path))
            {
                result.Add(path);
            }
        }

        return result;
    }
}
