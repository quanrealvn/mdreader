using System.Runtime.InteropServices;

namespace MdReader.Ui.Interop;

/// <summary>What the user answered to a message box.</summary>
internal enum MessageBoxAnswer { Ok, Yes, No, Cancel }

/// <summary>
/// The synchronous common dialogs the shared shell asks for (<c>IDialogService</c>): a message box and the file
/// pickers. With a Common-Controls v6 manifest and no hook or template, <c>GetOpenFileNameW</c> and
/// <c>GetSaveFileNameW</c> show the modern Explorer dialogs, which is what the WPF shell's
/// <c>Microsoft.Win32.OpenFileDialog</c> ends up calling as well.
/// </summary>
internal static class Win32Dialogs
{
    private const int MultiSelectBufferChars = 64 * 1024;
    private const int SingleFileBufferChars = 1024;

    internal static MessageBoxAnswer Show(nint owner, string caption, string message, uint buttons, uint icon,
                                          bool defaultSecondButton = false)
    {
        uint type = buttons | icon | (defaultSecondButton ? NativeMethods.MB_DEFBUTTON2 : 0)
                    | (owner == 0 ? NativeMethods.MB_TASKMODAL : 0);
        return NativeMethods.MessageBoxW(owner, message, caption, type) switch
        {
            NativeMethods.IDOK => MessageBoxAnswer.Ok,
            NativeMethods.IDYES => MessageBoxAnswer.Yes,
            NativeMethods.IDNO => MessageBoxAnswer.No,
            _ => MessageBoxAnswer.Cancel,
        };
    }

    /// <summary>Multi-select open dialog. Empty when the user cancelled or the dialog couldn't be shown.</summary>
    internal static IReadOnlyList<string> OpenFiles(nint owner, string title, string filter, string? initialDirectory)
    {
        nint filterPtr = AllocFilter(filter);
        nint titlePtr = Marshal.StringToHGlobalUni(title);
        nint directoryPtr = string.IsNullOrEmpty(initialDirectory) ? 0 : Marshal.StringToHGlobalUni(initialDirectory);
        nint buffer = Marshal.AllocHGlobal(MultiSelectBufferChars * sizeof(char));
        try
        {
            unsafe
            {
                ((char*)buffer)[0] = '\0';
            }

            var ofn = CreateStruct(owner, filterPtr, titlePtr, directoryPtr, buffer, MultiSelectBufferChars,
                NativeMethods.OFN_EXPLORER | NativeMethods.OFN_ALLOWMULTISELECT | NativeMethods.OFN_FILEMUSTEXIST
                | NativeMethods.OFN_PATHMUSTEXIST | NativeMethods.OFN_HIDEREADONLY | NativeMethods.OFN_NOCHANGEDIR);
            return NativeMethods.GetOpenFileNameW(ref ofn) ? ReadSelection(buffer) : [];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Free(filterPtr, titlePtr, directoryPtr);
        }
    }

    /// <summary>Save dialog with the overwrite prompt. Null when the user cancelled.</summary>
    internal static string? SaveFile(nint owner, string title, string filter, string defaultExtension, string? initialDirectory,
                                     string suggestedFileName)
    {
        nint filterPtr = AllocFilter(filter);
        nint titlePtr = Marshal.StringToHGlobalUni(title);
        nint directoryPtr = string.IsNullOrEmpty(initialDirectory) ? 0 : Marshal.StringToHGlobalUni(initialDirectory);
        nint extensionPtr = Marshal.StringToHGlobalUni(defaultExtension);
        nint buffer = Marshal.AllocHGlobal(SingleFileBufferChars * sizeof(char));
        try
        {
            WriteString(buffer, SingleFileBufferChars, suggestedFileName);
            var ofn = CreateStruct(owner, filterPtr, titlePtr, directoryPtr, buffer, SingleFileBufferChars,
                NativeMethods.OFN_EXPLORER | NativeMethods.OFN_OVERWRITEPROMPT | NativeMethods.OFN_PATHMUSTEXIST
                | NativeMethods.OFN_HIDEREADONLY | NativeMethods.OFN_NOREADONLYRETURN | NativeMethods.OFN_NOCHANGEDIR);
            ofn.lpstrDefExt = extensionPtr;
            if (!NativeMethods.GetSaveFileNameW(ref ofn))
            {
                return null;
            }

            string path = Marshal.PtrToStringUni(buffer) ?? "";
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(extensionPtr);
            Free(filterPtr, titlePtr, directoryPtr);
        }
    }

    private static NativeMethods.OPENFILENAMEW CreateStruct(nint owner, nint filter, nint title, nint directory, nint buffer,
                                                            int bufferChars, int flags) => new()
    {
        lStructSize = Marshal.SizeOf<NativeMethods.OPENFILENAMEW>(),
        hwndOwner = owner,
        lpstrFilter = filter,
        nFilterIndex = 1,
        lpstrFile = buffer,
        nMaxFile = bufferChars,
        lpstrInitialDir = directory,
        lpstrTitle = title,
        Flags = flags,
    };

    /// <summary>
    /// A multi-select result is either one full path, or the folder followed by the file names, each NUL-terminated and
    /// the list closed by an empty entry.
    /// </summary>
    private static IReadOnlyList<string> ReadSelection(nint buffer)
    {
        var parts = new List<string>();
        int offset = 0;
        while (offset < MultiSelectBufferChars)
        {
            string? part = Marshal.PtrToStringUni(buffer + (offset * sizeof(char)));
            if (string.IsNullOrEmpty(part))
            {
                break;
            }

            parts.Add(part);
            offset += part.Length + 1;
        }

        return parts.Count switch
        {
            0 => [],
            1 => [parts[0]],
            _ => [.. parts.Skip(1).Select(name => Path.Combine(parts[0], name))],
        };
    }

    /// <summary>The Win32 filter is NUL-separated and NUL-NUL-terminated; the shared filter uses '|' as the separator.</summary>
    private static nint AllocFilter(string filter)
    {
        string native = filter.Replace('|', '\0') + "\0\0";
        return Marshal.StringToHGlobalUni(native);
    }

    private static void WriteString(nint buffer, int bufferChars, string value)
    {
        unsafe
        {
            var span = new Span<char>((void*)buffer, bufferChars);
            span.Clear();
            value.AsSpan(0, Math.Min(value.Length, bufferChars - 1)).CopyTo(span);
        }
    }

    private static void Free(nint filter, nint title, nint directory)
    {
        Marshal.FreeHGlobal(filter);
        Marshal.FreeHGlobal(title);
        if (directory != 0)
        {
            Marshal.FreeHGlobal(directory);
        }
    }
}
