using System.IO;
using System.Windows;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Paths;
using MdReader.Shell.Services;
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

    public SaveChangesChoice ConfirmSaveChanges(string fileName)
    {
        if (_options.IsTestMode)
        {
            return SaveChangesChoice.Save;   // §4.7: no modal UI in tests, and nothing the user typed is thrown away
        }

        var answer = Ask($"Save changes to {fileName}?", MessageBoxButton.YesNoCancel, MessageBoxResult.Yes);
        return answer switch
        {
            MessageBoxResult.Yes => SaveChangesChoice.Save,
            MessageBoxResult.No => SaveChangesChoice.DontSave,
            _ => SaveChangesChoice.Cancel,
        };
    }

    public bool ConfirmOverwriteChangedFile(string fileName)
    {
        if (_options.IsTestMode)
        {
            return true;
        }

        return Ask($"{fileName} changed on disk since you started editing. Overwrite it with your version?",
            MessageBoxButton.YesNo, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static MessageBoxResult Ask(string message, MessageBoxButton buttons, MessageBoxResult defaultResult)
    {
        var owner = GetOwner();
        return owner is not null
            ? MessageBox.Show(owner, message, "MdReader", buttons, MessageBoxImage.Warning, defaultResult)
            : MessageBox.Show(message, "MdReader", buttons, MessageBoxImage.Warning, defaultResult);
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
