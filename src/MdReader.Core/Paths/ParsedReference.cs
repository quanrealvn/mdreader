namespace MdReader.Core.Paths;

public enum ReferenceKind { Empty, Fragment, Http, Https, Mailto, File, WindowsAbsolute, Unc, RootRelative, Relative, OtherScheme, Invalid }

public sealed record ParsedReference(
    ReferenceKind Kind,
    string Raw,
    string? DecodedPath,   // percent-decoded (UTF-8), '/'→'\' for local kinds
    string? Query,         // raw, without '?'
    string? Fragment,      // percent-decoded, without '#'
    Uri? AbsoluteUri);     // for Http/Https/Mailto/File
