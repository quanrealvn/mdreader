namespace MdReader.Mac.Interop;

/// <summary>
/// What <see cref="WkNative"/>'s Objective-C entry points call. Everything is a raw pointer, because this is the
/// boundary: the implementation (<see cref="WkWebViewChannel"/>) is where pointers turn into decisions.
/// </summary>
internal interface IWkWebViewCallbacks
{
    void OnNavigationAction(nint navigationAction, nint decisionHandler);

    void OnNavigationResponse(nint navigationResponse, nint decisionHandler);

    void OnNavigationFinished();

    void OnNavigationFailed(string? error);

    void OnWebContentProcessTerminated();

    void OnAuthenticationChallenge(nint challenge);

    void OnDownloadStarted(nint download);

    void OnNewWindow(nint navigationAction);

    void OnScriptDialogSuppressed(string kind);

    void OnPermissionDenied(string what);

    void OnScriptMessage(nint message);

    void OnSchemeTaskStarted(nint task);

    void OnSchemeTaskStopped(nint task);

    void OnFilesDropped(IReadOnlyList<string> paths);

    /// <summary>A delegate method threw. It has already taken the closed outcome; this only records why.</summary>
    void OnDelegateFailed(string name, Exception exception);
}
