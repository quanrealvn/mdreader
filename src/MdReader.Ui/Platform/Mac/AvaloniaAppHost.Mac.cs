using MdReader.Mac;

namespace MdReader.Ui.Services;

/// <summary>The macOS half of <see cref="AvaloniaAppHost"/>: AppKit alerts, which are application-modal already.</summary>
public sealed partial class AvaloniaAppHost
{
    private static partial void ShowErrorCore(string title, string message) =>
        MacAppKit.ShowMessage(title, message, isError: true);

    private static partial bool ConfirmErrorCore(string title, string message) =>
        MacAppKit.Ask(title, message, isError: true, "Yes", "No") == 0;
}
