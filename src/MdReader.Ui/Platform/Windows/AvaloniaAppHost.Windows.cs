using MdReader.Ui.Interop;

namespace MdReader.Ui.Services;

/// <summary>The Windows half of <see cref="AvaloniaAppHost"/>: the same Win32 message boxes the WPF shell ends up in.</summary>
public sealed partial class AvaloniaAppHost
{
    private static partial void ShowErrorCore(string title, string message) =>
        Win32Dialogs.Show(GetOwnerHandle(), title, message, NativeMethods.MB_OK, NativeMethods.MB_ICONERROR);

    private static partial bool ConfirmErrorCore(string title, string message) =>
        Win32Dialogs.Show(GetOwnerHandle(), title, message, NativeMethods.MB_YESNO, NativeMethods.MB_ICONERROR)
        == MessageBoxAnswer.Yes;
}
