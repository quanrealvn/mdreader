using System.IO;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Paths;
using MdReader.Shell.Services;
using MdReader.Ui.Interop;

namespace MdReader.Ui.Services;

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

        return Win32Dialogs.OpenFiles(AvaloniaAppHost.GetOwnerHandle(), "Open Markdown file",
                                      MarkdownFileTypes.OpenFileDialogFilter, initialDirectory);
    }

    public string? ShowSavePdfDialog(string suggestedFullPath)
    {
        if (_options.IsTestMode)
        {
            return null;
        }

        return Win32Dialogs.SaveFile(AvaloniaAppHost.GetOwnerHandle(), "Export as PDF", "PDF document (*.pdf)|*.pdf", "pdf",
                                     Path.GetDirectoryName(suggestedFullPath), Path.GetFileName(suggestedFullPath));
    }

    public SaveChangesChoice ConfirmSaveChanges(string fileName)
    {
        if (_options.IsTestMode)
        {
            return SaveChangesChoice.Save;   // §4.7: no modal UI in tests, and nothing the user typed is thrown away
        }

        return Ask($"Save changes to {fileName}?", NativeMethods.MB_YESNOCANCEL, defaultSecondButton: false) switch
        {
            MessageBoxAnswer.Yes => SaveChangesChoice.Save,
            MessageBoxAnswer.No => SaveChangesChoice.DontSave,
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
                   NativeMethods.MB_YESNO, defaultSecondButton: true) == MessageBoxAnswer.Yes;
    }

    public void ShowError(string title, string message)
    {
        _log.Write(AppLogLevel.Warning, Category, $"{title}: {message}");
        if (_options.IsTestMode)
        {
            return;
        }

        Win32Dialogs.Show(AvaloniaAppHost.GetOwnerHandle(), title, message, NativeMethods.MB_OK, NativeMethods.MB_ICONERROR);
    }

    private static MessageBoxAnswer Ask(string message, uint buttons, bool defaultSecondButton) =>
        Win32Dialogs.Show(AvaloniaAppHost.GetOwnerHandle(), "MdReader", message, buttons, NativeMethods.MB_ICONWARNING,
                          defaultSecondButton);
}
