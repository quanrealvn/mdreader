using Avalonia.Data.Converters;

namespace MdReader.Ui.Views;

/// <summary>
/// Converters for the automation surface of ARCHITECTURE §4.12. Avalonia has no data triggers, so the few automation
/// properties that follow the view model rather than a visual state are bound through these instead.
/// </summary>
public static class AutomationValues
{
    /// A tab's unsaved marker: ItemStatus is "unsaved" while the editor buffer differs from the file, and unset otherwise.
    public static FuncValueConverter<bool, string?> UnsavedItemStatus { get; } = new(dirty => dirty ? "unsaved" : null);
}
