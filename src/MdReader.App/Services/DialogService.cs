using System.IO;
using System.Windows;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Paths;
using Microsoft.Win32;

namespace MdReader.App.Services;

/// Common file dialogs and error message boxes. In test mode every method is a no-op (no modal UI), as required by §4.7.
public sealed class DialogService : IDialogService
{
    private const string Category = "Dialogs";

    private readonly CommandLineOptions _options;
    private readonly IAppLog _log;

    public DialogService(CommandLineOptions options, IAppLog log)
    {
        _options = options;
        _log = log;
    }

    public IReadOnlyList<string> ShowOpenDialog(string? initialDirectory)
    {
        if (_options.IsTestMode)
        {
            return [];
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open Markdown file",
            Filter = MarkdownFileTypes.OpenFileDialogFilter,
            Multiselect = true,
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(GetOwner()) == true ? dialog.FileNames : [];
    }

    public string? ShowSavePdfDialog(string suggestedFullPath)
    {
        if (_options.IsTestMode)
        {
            return null;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export as PDF",
            Filter = "PDF document (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = Path.GetFileName(suggestedFullPath),
        };
        var directory = Path.GetDirectoryName(suggestedFullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            dialog.InitialDirectory = directory;
        }

        return dialog.ShowDialog(GetOwner()) == true ? dialog.FileName : null;
    }

    public void ShowError(string title, string message)
    {
        _log.Write(AppLogLevel.Warning, Category, $"{title}: {message}");
        if (_options.IsTestMode)
        {
            return;
        }

        var owner = GetOwner();
        if (owner is not null)
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// The main window once it is visible; dialogs are then modal to it and centered on it.
    internal static Window? GetOwner()
    {
        var window = Application.Current?.MainWindow;
        return window is { IsLoaded: true, IsVisible: true } ? window : null;
    }
}
