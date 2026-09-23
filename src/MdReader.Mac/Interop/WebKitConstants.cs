namespace MdReader.Mac.Interop;

/// <summary>The enumerations WebKit and AppKit pass across the delegate boundary, by value.</summary>
internal static class WebKitConstants
{
    internal const nint NavigationActionPolicyCancel = 0;
    internal const nint NavigationActionPolicyAllow = 1;

    internal const nint NavigationResponsePolicyCancel = 0;
    internal const nint NavigationResponsePolicyAllow = 1;

    internal const nint PermissionDecisionDeny = 0;

    /// <summary>Fails the challenge outright: no prompt, no credential, no NTLM/Negotiate answer.</summary>
    internal const nint AuthChallengeCancel = 2;

    /// <summary><c>WKAudiovisualMediaTypeAll</c>: nothing plays without the user asking for it.</summary>
    internal const nint AudiovisualMediaTypeAll = ~0;

    internal const nint DragOperationNone = 0;
    internal const nint DragOperationCopy = 1;

    internal const nint EventModifierFlagControl = 1 << 18;
    internal const nint EventModifierFlagCommand = 1 << 20;

    /// <summary>The middle mouse button, which the page also treats as "open in a new tab".</summary>
    internal const nint MiddleButtonNumber = 2;

    internal const string DarkAppearanceName = "NSAppearanceNameDarkAqua";
    internal const string LightAppearanceName = "NSAppearanceNameAqua";

    /// <summary>The name the page posts to: <c>window.webkit.messageHandlers.mdreader</c>.</summary>
    internal const string ScriptMessageHandlerName = "mdreader";
}
