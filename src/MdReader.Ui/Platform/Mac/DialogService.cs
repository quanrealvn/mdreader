using System.IO;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Paths;
using MdReader.Mac;
using MdReader.Shell.Services;

namespace MdReader.Ui.Services;

/// <summary>
/// Common file dialogs and alerts on macOS. In test mode every method is a no-op (no modal UI), as §4.7 requires.
/// </summary>
/// <remarks>
/// These run through AppKit's own modal panels rather than Avalonia's <c>StorageProvider</c> for the reason the
/// Windows shell calls the Win32 dialogs: <see cref="IDialogService"/> is synchronous, because "save changes?" is
/// answered while a window is closing, where nothing can be awaited.
/// </remarks>
public sealed class DialogService : IDialogService
{
    private const string Category = "Dialogs";

    private readonly CommandLineOptions _options;
    private readonly IAppLog _log;

    public DialogService(CommandLineOptions options, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _options = options;
        _log = log;
    }

    public IReadOnlyList<string> ShowOpenDialog(string? initialDirectory)
    {
        if (_options.IsTestMode)
        {
            return [];
        }

        return MacAppKit.OpenFiles("Open Markdown file", MarkdownFileTypes.Extensions, initialDirectory);
    }

    public string? ShowSavePdfDialog(string suggestedFullPath)
    {
        if (_options.IsTestMode)
        {
            return null;
        }

        return MacAppKit.SaveFile("Export as PDF", "pdf", Path.GetDirectoryName(suggestedFullPath),
                                  Path.GetFileName(suggestedFullPath));
    }

    public SaveChangesChoice ConfirmSaveChanges(string fileName)
    {
        if (_options.IsTestMode)
        {
            return SaveChangesChoice.Save;   // §4.7: no modal UI in tests, and nothing the user typed is thrown away
        }

        // Apple's order: the default action first, the destructive one next, Cancel last (where Escape lands).
        return MacAppKit.Ask("MdReader", $"Save changes to {fileName}?", isError: false, "Save", "Don't Save", "Cancel") switch
        {
            0 => SaveChangesChoice.Save,
            1 => SaveChangesChoice.DontSave,
            _ => SaveChangesChoice.Cancel,
        };
    }

    public bool ConfirmOverwriteChangedFile(string fileName)
    {
        if (_options.IsTestMode)
        {
            return true;
        }

        return MacAppKit.Ask("MdReader",
            $"{fileName} changed on disk since you started editing. Overwrite it with your version?",
            isError: false, "Cancel", "Overwrite") == 1;
    }

    public void ShowError(string title, string message)
    {
        _log.Write(AppLogLevel.Warning, Category, $"{title}: {message}");
        if (!_options.IsTestMode)
        {
            MacAppKit.ShowMessage(title, message, isError: true);
        }
    }
}
