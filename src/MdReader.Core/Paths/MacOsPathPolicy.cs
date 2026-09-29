namespace MdReader.Core.Paths;

/// <summary>
/// macOS path rules: POSIX, plus the four traps that are specific to it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Case.</b> APFS and HFS+ are case-insensitive but case-preserving by default, so comparison ignores case
/// while the stored spelling is kept. A volume formatted case-sensitive is the rarer configuration and only makes
/// containment checks slightly generous.</item>
/// <item><b><c>/Volumes</c>.</b> Every mount other than the boot volume appears as <c>/Volumes/&lt;name&gt;</c>. That is a
/// volume boundary, so the resource-root walk stops there the way it stops at <c>C:\</c> on Windows — otherwise one
/// <c>.git</c> in <c>/Volumes</c> would map every mounted disk.</item>
/// <item><b>Network.</b> <c>/net/&lt;host&gt;</c> and <c>/Network/Servers/&lt;host&gt;</c> are autofs trigger paths: merely
/// touching them makes the OS resolve the name and mount a share, which is this platform's NTLM leak
/// (ARCHITECTURE §8.2). They are treated exactly like a UNC share: reachable only from a document on the same host.</item>
/// <item><b>Resource forks.</b> The fork of <c>x.png</c> can be read as <c>x.png/..namedfork/rsrc</c>, and on non-HFS
/// volumes it is stored beside the file as <c>._x.png</c>. Both are the macOS answer to NTFS alternate data streams,
/// so both are forbidden. <c>/.vol/&lt;device&gt;/&lt;inode&gt;</c> addresses any file on a volume by inode, bypassing the
/// path it was reached by; that is the local device namespace and is forbidden too.</item>
/// </list>
/// ':' is deliberately *not* forbidden: it is legal in a macOS file name (the Finder shows it as '/'), so the Windows
/// alternate-data-stream rule must not apply here.
/// </remarks>
internal sealed class MacOsPathPolicy : PosixPathPolicy
{
    private const string VolumesRoot = "/Volumes";
    private const string AutomountRoot = "/net";
    private const string NetworkRoot = "/Network";
    private const string NetworkServersRoot = "/Network/Servers";

    /// <summary>The directory that exposes a file's resource fork, and the prefix of an AppleDouble sidecar.</summary>
    private const string NamedForkDirectory = "..namedfork";
    private const string AppleDoublePrefix = "._";

    public override string Name => "macOS";

    public override bool IsCaseSensitive => false;

    public override bool IsReservedName(ReadOnlySpan<char> segment) =>
        segment.Equals(NamedForkDirectory, StringComparison.OrdinalIgnoreCase)
        || segment.StartsWith(AppleDoublePrefix, StringComparison.Ordinal);

    public override bool IsNetworkPath(string? path) =>
        IsUnder(path, AutomountRoot) || IsUnder(path, NetworkRoot);

    public override string? GetNetworkRoot(string? normalizedFullPath)
    {
        if (normalizedFullPath is null)
        {
            return null;
        }

        var host = FirstComponentUnder(normalizedFullPath, AutomountRoot);
        if (!host.IsEmpty)
        {
            return AutomountRoot + "/" + host.ToString();
        }

        host = FirstComponentUnder(normalizedFullPath, NetworkServersRoot);
        return host.IsEmpty ? null : NetworkServersRoot + "/" + host.ToString();
    }

    public override string? GetVolumeRoot(string? normalizedFullPath)
    {
        if (!IsFullyQualified(normalizedFullPath))
        {
            return null;
        }

        if (GetNetworkRoot(normalizedFullPath) is { } network)
        {
            return network;
        }

        var volume = FirstComponentUnder(normalizedFullPath!, VolumesRoot);
        return volume.IsEmpty ? "/" : VolumesRoot + "/" + volume.ToString();
    }

    protected override string[] ForbiddenRoots { get; } = ["/dev", "/.vol"];
}
