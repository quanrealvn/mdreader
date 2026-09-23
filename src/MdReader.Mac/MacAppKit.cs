using MdReader.Mac.Interop;

namespace MdReader.Mac;

/// <summary>
/// The AppKit the shell needs outside the web view: alerts, the open and save panels, the pasteboard and bringing the
/// application forward. All of it is main-thread only, and all of it is modal where the contract it serves is
/// synchronous — <c>IDialogService</c> answers "save changes?" while a window is closing, where nothing can be
/// awaited, which is exactly what <c>runModal</c> is for.
/// </summary>
public static class MacAppKit
{
    private const nint AlertStyleWarning = 0;
    private const nint AlertStyleCritical = 2;
    private const nint FirstButtonResponse = 1000;
    private const nint ModalResponseOk = 1;

    /// <summary>An alert with one OK button.</summary>
    public static void ShowMessage(string title, string message, bool isError) => ObjC.WithPool(() =>
    {
        nint alert = NewAlert(title, message, isError);
        ObjC.Send(alert, ObjC.Selector("addButtonWithTitle:"), Foundation.String("OK"));
        ObjC.SendLong(alert, ObjC.Selector("runModal"));
        ObjC.Release(alert);
    });

    /// <summary>An alert whose buttons are given in order; returns the index of the one that was pressed.</summary>
    /// <remarks>The first button is the default one and the last is bound to Escape, which is AppKit's own
    /// convention, so "Cancel" belongs last.</remarks>
    public static int Ask(string title, string message, bool isError, params string[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        if (buttons.Length == 0)
        {
            throw new ArgumentException("An alert needs at least one button.", nameof(buttons));
        }

        return ObjC.WithPool(() =>
        {
            nint alert = NewAlert(title, message, isError);
            foreach (string button in buttons)
            {
                ObjC.Send(alert, ObjC.Selector("addButtonWithTitle:"), Foundation.String(button));
            }

            nint response = (nint)ObjC.SendLong(alert, ObjC.Selector("runModal"));
            ObjC.Release(alert);
            int index = (int)(response - FirstButtonResponse);
            return index >= 0 && index < buttons.Length ? index : buttons.Length - 1;
        });
    }

    /// <summary>The open panel, multi-select, limited to the given file extensions (without the dot).</summary>
    public static IReadOnlyList<string> OpenFiles(string title, IReadOnlyList<string> extensions, string? directory)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        return ObjC.WithPool(() =>
        {
            nint panel = ObjC.Send(ObjC.RequireClass("NSOpenPanel"), ObjC.Selector("openPanel"));
            ObjC.SendVoid(panel, ObjC.Selector("setMessage:"), Foundation.String(title));
            ObjC.SendVoidBool(panel, ObjC.Selector("setCanChooseFiles:"), 1);
            ObjC.SendVoidBool(panel, ObjC.Selector("setCanChooseDirectories:"), 0);
            ObjC.SendVoidBool(panel, ObjC.Selector("setAllowsMultipleSelection:"), 1);
            ObjC.SendVoidBool(panel, ObjC.Selector("setAllowsOtherFileTypes:"), 1);
            SetDirectory(panel, directory);
            SetAllowedFileTypes(panel, extensions);

            if ((nint)ObjC.SendLong(panel, ObjC.Selector("runModal")) != ModalResponseOk)
            {
                return (IReadOnlyList<string>)[];
            }

            nint urls = ObjC.Send(panel, ObjC.Selector("URLs"));
            int count = Foundation.ArrayCount(urls);
            var paths = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                if (Foundation.UrlFileSystemPath(Foundation.ArrayItem(urls, i)) is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }

            return paths;
        });
    }

    /// <summary>The save panel; null when the user cancelled.</summary>
    public static string? SaveFile(string title, string extension, string? directory, string? fileName) =>
        ObjC.WithPool(() =>
        {
            nint panel = ObjC.Send(ObjC.RequireClass("NSSavePanel"), ObjC.Selector("savePanel"));
            ObjC.SendVoid(panel, ObjC.Selector("setMessage:"), Foundation.String(title));
            SetDirectory(panel, directory);
            if (!string.IsNullOrEmpty(fileName))
            {
                ObjC.SendVoid(panel, ObjC.Selector("setNameFieldStringValue:"), Foundation.String(fileName));
            }

            SetAllowedFileTypes(panel, [extension]);
            return (nint)ObjC.SendLong(panel, ObjC.Selector("runModal")) == ModalResponseOk
                ? Foundation.UrlFileSystemPath(ObjC.Send(panel, ObjC.Selector("URL")))
                : null;
        });

    /// <summary>Replaces the general pasteboard's contents with plain text.</summary>
    public static bool TrySetClipboardText(string text) => ObjC.WithPool(() =>
    {
        nint pasteboard = ObjC.Send(ObjC.RequireClass("NSPasteboard"), ObjC.Selector("generalPasteboard"));
        if (pasteboard == 0)
        {
            return false;
        }

        ObjC.SendLong(pasteboard, ObjC.Selector("clearContents"));
        return ObjC.SendBool(pasteboard, ObjC.Selector("setString:forType:"),
                             Foundation.String(text), Foundation.String("public.utf8-plain-text")) != 0;
    });

    /// <summary>
    /// Brings the application forward. On Windows the forwarding process has to grant permission first; macOS lets an
    /// application that was just handed a document activate itself, which is what <c>activateIgnoringOtherApps:</c>
    /// means here.
    /// </summary>
    public static void ActivateApplication() => ObjC.WithPool(() =>
    {
        nint application = ObjC.Send(ObjC.RequireClass("NSApplication"), ObjC.Selector("sharedApplication"));
        if (application != 0)
        {
            ObjC.SendVoidBool(application, ObjC.Selector("activateIgnoringOtherApps:"), 1);
        }
    });

    private static nint NewAlert(string title, string message, bool isError)
    {
        nint alert = ObjC.New(ObjC.RequireClass("NSAlert"));
        ObjC.SendVoid(alert, ObjC.Selector("setMessageText:"), Foundation.String(title));
        ObjC.SendVoid(alert, ObjC.Selector("setInformativeText:"), Foundation.String(message));
        ObjC.SendVoidLong(alert, ObjC.Selector("setAlertStyle:"), isError ? AlertStyleCritical : AlertStyleWarning);
        return alert;
    }

    private static void SetDirectory(nint panel, string? directory)
    {
        if (!string.IsNullOrEmpty(directory))
        {
            ObjC.SendVoid(panel, ObjC.Selector("setDirectoryURL:"), Foundation.FileUrl(directory, isDirectory: true));
        }
    }

    /// <summary>
    /// <c>allowedFileTypes</c> is deprecated in favour of <c>allowedContentTypes</c> (macOS 11+), which needs
    /// UniformTypeIdentifiers; the extension list still works and keeps this to one framework.
    /// </summary>
    private static void SetAllowedFileTypes(nint panel, IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0 || !ObjC.RespondsTo(panel, "setAllowedFileTypes:"))
        {
            return;
        }

        var items = new nint[extensions.Count];
        for (var i = 0; i < extensions.Count; i++)
        {
            items[i] = Foundation.String(extensions[i].TrimStart('.'));
        }

        ObjC.SendVoid(panel, ObjC.Selector("setAllowedFileTypes:"), Foundation.Array(items));
    }
}
