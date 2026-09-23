namespace MdReader.Ui.Views;

/// <summary>
/// A document view the find bar can be driven through from outside the view itself. The macOS menu bar needs it: §4.12
/// leaves "find next" and "find previous" to the document view, and on macOS those rows are ⌘G and ⇧⌘G in the Edit
/// menu rather than keys the view ever sees.
/// </summary>
internal interface IFindableView
{
    /// <summary>Find next (or previous), opening the find bar first if it isn't open yet.</summary>
    void FindFromMenu(bool backwards);
}
