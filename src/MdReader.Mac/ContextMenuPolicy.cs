using System.Collections.Frozen;

namespace MdReader.Mac;

/// <summary>
/// The context-menu filter of ARCHITECTURE §8.4 for WebKit's own menu. WebView2 raises an event with a mutable item
/// list; WKWebView on macOS offers <c>-[NSView willOpenMenu:withEvent:]</c> and identifies its items with
/// <c>WKMenuItemIdentifier</c> strings, so the same allow-list applies to the same four actions.
/// </summary>
/// <remarks>
/// <para>Fail closed: an item is removed unless its identifier is on the list, so an identifier a future WebKit adds
/// is hidden until someone decides it belongs. Items with no identifier at all (submenus WebKit builds from services
/// or spelling) are removed for the same reason.</para>
/// <para>"Select all" has no counterpart here: WebKit's macOS menu doesn't offer it, so the menu is one item shorter
/// than on Windows. ⌘A still selects everything.</para>
/// </remarks>
public static class ContextMenuPolicy
{
    /// <summary>The identifiers that survive, matching Windows' <c>copy</c>, <c>copyLinkLocation</c> and
    /// <c>copyImage</c> (plus <c>inspectElement</c> in Debug).</summary>
    public static FrozenSet<string> AllowedIdentifiers { get; } = FrozenSet.ToFrozenSet(
        [
            "WKMenuItemIdentifierCopy",
            "WKMenuItemIdentifierCopyLink",
            "WKMenuItemIdentifierCopyImage",
#if DEBUG
            "WKMenuItemIdentifierInspectElement",
#endif
        ],
        StringComparer.Ordinal);

    /// <summary>True when the item with this identifier may stay. Separators are decided by the caller.</summary>
    public static bool IsAllowed(string? identifier) =>
        identifier is not null && AllowedIdentifiers.Contains(identifier);

    /// <summary>
    /// The indices to remove from a menu, highest first so a caller can delete them in order. Items are described by
    /// identifier, with null for a separator (and for an item that has none, which is removed either way — the two
    /// are told apart by <paramref name="separators"/>).
    /// </summary>
    /// <param name="identifiers">One entry per menu item, in order.</param>
    /// <param name="separators">True where the item at the same index is a separator.</param>
    public static IReadOnlyList<int> IndicesToRemove(IReadOnlyList<string?> identifiers, IReadOnlyList<bool> separators)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(separators);
        if (identifiers.Count != separators.Count)
        {
            throw new ArgumentException("There must be one separator flag per menu item.", nameof(separators));
        }

        var keep = new bool[identifiers.Count];
        for (var i = 0; i < identifiers.Count; i++)
        {
            keep[i] = separators[i] || IsAllowed(identifiers[i]);
        }

        // Separators only earn their place between two surviving items: no leading, trailing or doubled ones, the
        // same tidy-up the Windows filter does after removing items.
        var lastKept = -1;
        for (var i = 0; i < keep.Length; i++)
        {
            if (!keep[i])
            {
                continue;
            }

            if (separators[i])
            {
                if (lastKept < 0 || separators[lastKept])
                {
                    keep[i] = false;
                    continue;
                }
            }

            lastKept = i;
        }

        if (lastKept >= 0 && separators[lastKept])
        {
            keep[lastKept] = false;
        }

        // Nothing but separators left (or nothing at all): show no menu rather than an empty frame.
        var anyItem = false;
        for (var i = 0; i < keep.Length; i++)
        {
            anyItem |= keep[i] && !separators[i];
        }

        var remove = new List<int>(identifiers.Count);
        for (int i = identifiers.Count - 1; i >= 0; i--)
        {
            if (!keep[i] || !anyItem)
            {
                remove.Add(i);
            }
        }

        return remove;
    }
}
