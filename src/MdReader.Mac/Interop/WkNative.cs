using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MdReader.Mac.Interop;

/// <summary>
/// The Objective-C side of one web view: the delegate object WebKit talks to, and the <c>WKWebView</c> subclass that
/// filters the context menu and takes file drops.
/// </summary>
/// <remarks>
/// <para>Two classes are built, once per process, the first time a web view is created. Instances hold no managed
/// state; each one is registered here against its <see cref="IWkWebViewCallbacks"/> and looked up by pointer, the way
/// the Windows shell's native host window finds itself from its HWND.</para>
/// <para>Every entry point below is a C function WebKit calls. None of them may let an exception escape into
/// Objective-C, so each one catches everything and takes the closed outcome (cancel, deny, nil).</para>
/// </remarks>
internal static unsafe class WkNative
{
    private static readonly ConcurrentDictionary<nint, IWkWebViewCallbacks> Owners = new();
    private static readonly Lazy<nint> DelegateClass = new(BuildDelegateClass, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<nint> WebViewClass = new(BuildWebViewClass, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The delegate object for one web view: navigation, UI, script messages and the scheme handler in one.</summary>
    internal static nint CreateDelegate(IWkWebViewCallbacks owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        nint instance = ObjC.New(DelegateClass.Value);
        Owners[instance] = owner;
        return instance;
    }

    /// <summary>The web view itself, a subclass so the context menu and file drops stay ours.</summary>
    internal static nint CreateWebView(nint configuration, CGRect frame, IWkWebViewCallbacks owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        nint allocated = ObjC.Send(WebViewClass.Value, ObjC.Selector("alloc"));
        nint view = ObjC.SendRectObject(allocated, ObjC.Selector("initWithFrame:configuration:"), frame, configuration);
        Owners[view] = owner;
        return view;
    }

    /// <summary>Forgets an instance; called when the web view and its delegate are released.</summary>
    internal static void Forget(nint instance) => Owners.TryRemove(instance, out _);

    private static IWkWebViewCallbacks? Owner(nint instance) =>
        Owners.TryGetValue(instance, out IWkWebViewCallbacks? owner) ? owner : null;

    // ----------------------------------------------------------------------------------------------------------------
    // Class construction

    private static nint BuildDelegateClass()
    {
        var builder = new ObjCClassBuilder("MdrWebViewDelegate", "NSObject");
        builder.AddProtocol("WKNavigationDelegate");
        builder.AddProtocol("WKUIDelegate");
        builder.AddProtocol("WKScriptMessageHandler");
        builder.AddProtocol("WKURLSchemeHandler");

        builder.AddMethod("webView:decidePolicyForNavigationAction:decisionHandler:", "v@:@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DecideNavigationAction);
        builder.AddMethod("webView:decidePolicyForNavigationResponse:decisionHandler:", "v@:@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DecideNavigationResponse);
        builder.AddMethod("webView:didFinishNavigation:", "v@:@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidFinishNavigation);
        builder.AddMethod("webView:didFailNavigation:withError:", "v@:@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidFailNavigation);
        builder.AddMethod("webView:didFailProvisionalNavigation:withError:", "v@:@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidFailNavigation);
        builder.AddMethod("webViewWebContentProcessDidTerminate:", "v@:@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WebContentProcessDidTerminate);
        builder.AddMethod("webView:didReceiveAuthenticationChallenge:completionHandler:", "v@:@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidReceiveAuthenticationChallenge);
        builder.AddMethod("webView:navigationAction:didBecomeDownload:", "v@:@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidBecomeDownload);
        builder.AddMethod("webView:navigationResponse:didBecomeDownload:", "v@:@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidBecomeDownload);
        builder.AddMethod("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:", "@@:@@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)&CreateWebViewWith);
        builder.AddMethod("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:", "v@:@@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&RunAlertPanel);
        builder.AddMethod("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:", "v@:@@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&RunConfirmPanel);
        builder.AddMethod("webView:runJavaScriptTextInputPanelWithPrompt:defaultText:initiatedByFrame:completionHandler:", "v@:@@@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, void>)&RunTextInputPanel);
        builder.AddMethod("webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:", "v@:@@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&RunOpenPanel);
        builder.AddMethod("webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:", "v@:@@@q@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, void>)&RequestMediaCapturePermission);
        builder.AddMethod("webView:requestDeviceOrientationAndMotionPermissionForOrigin:initiatedByFrame:decisionHandler:", "v@:@@@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&RequestDeviceOrientationPermission);
        builder.AddMethod("userContentController:didReceiveScriptMessage:", "v@:@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidReceiveScriptMessage);
        builder.AddMethod("webView:startURLSchemeTask:", "v@:@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&StartSchemeTask);
        builder.AddMethod("webView:stopURLSchemeTask:", "v@:@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&StopSchemeTask);
        return builder.Register();
    }

    private static nint BuildWebViewClass()
    {
        var builder = new ObjCClassBuilder("MdrWebView", "WKWebView");
        builder.AddMethod("willOpenMenu:withEvent:", "v@:@@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&WillOpenMenu);
        builder.AddMethod("draggingEntered:", "Q@:@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&DraggingEntered);
        builder.AddMethod("draggingUpdated:", "Q@:@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&DraggingEntered);
        builder.AddMethod("prepareForDragOperation:", "c@:@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&PrepareForDragOperation);
        builder.AddMethod("performDragOperation:", "c@:@",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&PerformDragOperation);
        return builder.Register();
    }

    // ----------------------------------------------------------------------------------------------------------------
    // WKNavigationDelegate

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DecideNavigationAction(nint self, nint selector, nint webView, nint action, nint handler)
    {
        try
        {
            Owner(self)?.OnNavigationAction(action, handler);
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(DecideNavigationAction), ex);
            Blocks.Call(handler, WebKitConstants.NavigationActionPolicyCancel);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DecideNavigationResponse(nint self, nint selector, nint webView, nint response, nint handler)
    {
        try
        {
            Owner(self)?.OnNavigationResponse(response, handler);
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(DecideNavigationResponse), ex);
            Blocks.Call(handler, WebKitConstants.NavigationResponsePolicyCancel);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidFinishNavigation(nint self, nint selector, nint webView, nint navigation)
    {
        try
        {
            Owner(self)?.OnNavigationFinished();
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(DidFinishNavigation), ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidFailNavigation(nint self, nint selector, nint webView, nint navigation, nint error)
    {
        try
        {
            Owner(self)?.OnNavigationFailed(Foundation.ErrorDescription(error));
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(DidFailNavigation), ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WebContentProcessDidTerminate(nint self, nint selector, nint webView)
    {
        try
        {
            Owner(self)?.OnWebContentProcessTerminated();
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(WebContentProcessDidTerminate), ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidReceiveAuthenticationChallenge(nint self, nint selector, nint webView, nint challenge, nint handler)
    {
        try
        {
            Owner(self)?.OnAuthenticationChallenge(challenge);
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(DidReceiveAuthenticationChallenge), ex);
        }
        finally
        {
            // No credential, ever: neither a password prompt nor an ambient Negotiate/NTLM answer for a remote image.
            Blocks.Call(handler, WebKitConstants.AuthChallengeCancel, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidBecomeDownload(nint self, nint selector, nint webView, nint action, nint download)
    {
        try
        {
            Owner(self)?.OnDownloadStarted(download);
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(DidBecomeDownload), ex);
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // WKUIDelegate

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint CreateWebViewWith(nint self, nint selector, nint webView, nint configuration, nint action, nint features)
    {
        try
        {
            Owner(self)?.OnNewWindow(action);
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(CreateWebViewWith), ex);
        }

        return 0;   // nil: never a popup
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunAlertPanel(nint self, nint selector, nint webView, nint message, nint frame, nint handler)
    {
        Owner(self)?.OnScriptDialogSuppressed("alert");
        Blocks.Call(handler);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunConfirmPanel(nint self, nint selector, nint webView, nint message, nint frame, nint handler)
    {
        Owner(self)?.OnScriptDialogSuppressed("confirm");
        Blocks.Call(handler, 0);   // NO
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunTextInputPanel(nint self, nint selector, nint webView, nint prompt, nint defaultText, nint frame,
                                          nint handler)
    {
        Owner(self)?.OnScriptDialogSuppressed("prompt");
        Blocks.Call(handler, 0);   // nil
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunOpenPanel(nint self, nint selector, nint webView, nint parameters, nint frame, nint handler)
    {
        Owner(self)?.OnScriptDialogSuppressed("file picker");
        Blocks.Call(handler, 0);   // nil: no file is ever chosen from inside the page
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RequestMediaCapturePermission(nint self, nint selector, nint webView, nint origin, nint frame, nint type,
                                                      nint handler)
    {
        Owner(self)?.OnPermissionDenied($"media capture ({type})");
        Blocks.Call(handler, WebKitConstants.PermissionDecisionDeny);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RequestDeviceOrientationPermission(nint self, nint selector, nint webView, nint origin, nint frame,
                                                           nint handler)
    {
        Owner(self)?.OnPermissionDenied("device orientation and motion");
        Blocks.Call(handler, WebKitConstants.PermissionDecisionDeny);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // WKScriptMessageHandler and WKURLSchemeHandler

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidReceiveScriptMessage(nint self, nint selector, nint controller, nint message)
    {
        try
        {
            Owner(self)?.OnScriptMessage(message);
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(DidReceiveScriptMessage), ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void StartSchemeTask(nint self, nint selector, nint webView, nint task)
    {
        try
        {
            Owner(self)?.OnSchemeTaskStarted(task);
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(StartSchemeTask), ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void StopSchemeTask(nint self, nint selector, nint webView, nint task)
    {
        try
        {
            Owner(self)?.OnSchemeTaskStopped(task);
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(StopSchemeTask), ex);
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // The WKWebView subclass: context menu and file drops

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WillOpenMenu(nint self, nint selector, nint menu, nint @event)
    {
        var super = new ObjC.SuperTarget { Receiver = self, SuperClass = ObjC.class_getSuperclass(WebViewClass.Value) };
        try
        {
            ObjC.SendSuperVoid(ref super, ObjC.Selector("willOpenMenu:withEvent:"), menu, @event);
            FilterMenu(menu);
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(WillOpenMenu), ex);
            RemoveEveryItem(menu);   // fail closed: no menu rather than an unfiltered one
        }
    }

    private static void FilterMenu(nint menu)
    {
        int count = (int)ObjC.SendLong(menu, ObjC.Selector("numberOfItems"));
        var identifiers = new string?[count];
        var separators = new bool[count];
        for (var i = 0; i < count; i++)
        {
            nint item = ObjC.SendIndexed(menu, ObjC.Selector("itemAtIndex:"), (nuint)i);
            separators[i] = ObjC.SendBool(item, ObjC.Selector("isSeparatorItem")) != 0;
            identifiers[i] = separators[i] ? null : Foundation.ToManagedString(ObjC.Send(item, ObjC.Selector("identifier")));
        }

        foreach (int index in ContextMenuPolicy.IndicesToRemove(identifiers, separators))
        {
            ObjC.SendVoidLong(menu, ObjC.Selector("removeItemAtIndex:"), index);
        }
    }

    private static void RemoveEveryItem(nint menu)
    {
        for (int i = (int)ObjC.SendLong(menu, ObjC.Selector("numberOfItems")) - 1; i >= 0; i--)
        {
            ObjC.SendVoidLong(menu, ObjC.Selector("removeItemAtIndex:"), i);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint DraggingEntered(nint self, nint selector, nint info)
    {
        try
        {
            return DraggedFilePaths(info).Count > 0 ? WebKitConstants.DragOperationCopy : WebKitConstants.DragOperationNone;
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(DraggingEntered), ex);
            return WebKitConstants.DragOperationNone;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte PrepareForDragOperation(nint self, nint selector, nint info) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte PerformDragOperation(nint self, nint selector, nint info)
    {
        try
        {
            IReadOnlyList<string> paths = DraggedFilePaths(info);
            if (paths.Count == 0)
            {
                return 0;
            }

            Owner(self)?.OnFilesDropped(paths);
            return 1;
        }
        catch (Exception ex)
        {
            Owner(self)?.OnDelegateFailed(nameof(PerformDragOperation), ex);
            return 0;
        }
    }

    /// <summary>
    /// The file paths on a drag's pasteboard. WebView2 hands the host the dropped files as <c>CoreWebView2File</c>
    /// objects with real paths; WebKit gives JavaScript a <c>File</c> with no path at all, so the drop is taken here,
    /// before WebKit sees it, and the page's own drop handler never runs on macOS.
    /// </summary>
    private static IReadOnlyList<string> DraggedFilePaths(nint info)
    {
        nint pasteboard = ObjC.Send(info, ObjC.Selector("draggingPasteboard"));
        if (pasteboard == 0)
        {
            return [];
        }

        nint classes = Foundation.Array([ObjC.RequireClass("NSURL")]);
        nint urls = ObjC.Send(pasteboard, ObjC.Selector("readObjectsForClasses:options:"), classes, 0);
        int count = Foundation.ArrayCount(urls);
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            if (Foundation.UrlFileSystemPath(Foundation.ArrayItem(urls, i)) is { Length: > 0 } path)
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
