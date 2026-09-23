namespace MdReader.Mac.Interop;

/// <summary>
/// Builds an Objective-C class at run time whose methods are C# function pointers. Every delegate WebKit expects
/// (<c>WKNavigationDelegate</c>, <c>WKUIDelegate</c>, <c>WKScriptMessageHandler</c>, <c>WKURLSchemeHandler</c>) is
/// one of these, so nothing in this backend needs a compiled Objective-C shim.
/// </summary>
/// <remarks>
/// A class name may exist only once per process, so every class below is built exactly once, from a static
/// initializer, and kept for the life of the process. Instances carry no managed state: the method bodies look their
/// owner up by instance pointer, the same way the Windows shell's native host window finds itself from its HWND.
/// </remarks>
internal sealed unsafe class ObjCClassBuilder
{
    private readonly nint _class;

    internal ObjCClassBuilder(string name, string superclassName)
    {
        nint superclass = ObjC.RequireClass(superclassName);
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = utf8)
        {
            _class = ObjC.objc_allocateClassPair(superclass, p, 0);
        }

        if (_class == 0)
        {
            throw new InvalidOperationException($"The Objective-C class '{name}' couldn't be created (the name is already taken).");
        }
    }

    /// <param name="typeEncoding">The method's Objective-C type encoding, e.g. <c>v@:@@</c> for
    /// <c>-(void)webView:(id)a startURLSchemeTask:(id)b</c>.</param>
    internal void AddMethod(string selector, string typeEncoding, nint implementation)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(typeEncoding + "\0");
        fixed (byte* p = utf8)
        {
            if (ObjC.class_addMethod(_class, ObjC.Selector(selector), implementation, p) == 0)
            {
                throw new InvalidOperationException($"Couldn't add '{selector}' to the Objective-C class.");
            }
        }
    }

    /// <summary>
    /// Declares conformance. WebKit dispatches on <c>respondsToSelector:</c>, but <c>setURLSchemeHandler:</c> and the
    /// delegate setters assert conformance in debug builds of WebKit, and a missing protocol is cheap to add.
    /// </summary>
    internal void AddProtocol(string name)
    {
        nint protocol = ObjC.GetProtocol(name);
        if (protocol != 0)
        {
            ObjC.class_addProtocol(_class, protocol);
        }
    }

    internal nint Register()
    {
        ObjC.objc_registerClassPair(_class);
        return _class;
    }
}
