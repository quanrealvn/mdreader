using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MdReader.Mac.Interop;

/// <summary>
/// The Objective-C runtime, by hand. Everything the WKWebView backend needs is three libraries' worth of C functions
/// plus <c>objc_msgSend</c>, so there is no binding library in the dependency graph and no generated surface between
/// us and WebKit: the seam is ours to read and to fix.
/// </summary>
/// <remarks>
/// <para><b>objc_msgSend.</b> It is declared once per argument/return shape rather than as one variadic entry point.
/// On arm64 a variadic call puts its arguments in different registers than a normal one, so calling the variadic
/// declaration would pass garbage; every declaration below is therefore an exact, non-variadic prototype.</para>
/// <para><b>BOOL.</b> Objective-C's BOOL is a signed char on every platform macOS runs on, so it crosses this boundary
/// as <see cref="byte"/>; nothing here marshals <see cref="bool"/>.</para>
/// <para><b>Threading.</b> AppKit and WebKit are main-thread only, and so is everything in this namespace unless a
/// member says otherwise.</para>
/// </remarks>
internal static unsafe partial class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    private static readonly ConcurrentDictionary<string, nint> Classes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, nint> Selectors = new(StringComparer.Ordinal);

    /// <summary>The class with this name, or 0 when the runtime doesn't have it (an older macOS).</summary>
    internal static nint GetClass(string name) => Classes.GetOrAdd(name, static n =>
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(n + "\0");
        fixed (byte* p = utf8)
        {
            return objc_getClass(p);
        }
    });

    /// <summary>The class with this name; throws when the runtime doesn't have it.</summary>
    internal static nint RequireClass(string name) =>
        GetClass(name) is var handle && handle != 0
            ? handle
            : throw new PlatformNotSupportedException($"The Objective-C class '{name}' isn't available on this system.");

    internal static nint Selector(string name) => Selectors.GetOrAdd(name, static n =>
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(n + "\0");
        fixed (byte* p = utf8)
        {
            return sel_registerName(p);
        }
    });

    internal static nint GetProtocol(string name)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = utf8)
        {
            return objc_getProtocol(p);
        }
    }

    // ----- Messaging -----

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2, nint arg3, nint arg4);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector, nint arg1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector, nint arg1, nint arg2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidBool(nint receiver, nint selector, byte arg1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidDouble(nint receiver, nint selector, double arg1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidLong(nint receiver, nint selector, long arg1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial byte SendBool(nint receiver, nint selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial byte SendBool(nint receiver, nint selector, nint arg1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial byte SendBool(nint receiver, nint selector, nint arg1, nint arg2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial byte SendBool(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

    /// <summary>
    /// <c>initWithFrame:configuration:</c>. A CGRect is four doubles, which arm64 passes in d0–d3 and x86-64 passes on
    /// the stack; the runtime's own struct marshalling gets both right, so the rectangle crosses by value here rather
    /// than through a pointer.
    /// </summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendRectObject(nint receiver, nint selector, CGRect frame, nint arg2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidRect(nint receiver, nint selector, CGRect frame);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial double SendDouble(nint receiver, nint selector);

    /// <summary><c>+[NSColor colorWithSRGBRed:green:blue:alpha:]</c>: four CGFloats, which are doubles on 64-bit.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendFourDoubles(nint receiver, nint selector, double a, double b, double c, double d);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial long SendLong(nint receiver, nint selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nuint SendUInt(nint receiver, nint selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIndexed(nint receiver, nint selector, nuint index);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidPointerCount(nint receiver, nint selector, nint items, nuint count);

    // ----- Object lifetime -----

    internal static nint New(nint cls) => Send(Send(cls, Selector("alloc")), Selector("init"));

    internal static nint Retain(nint obj) => obj == 0 ? 0 : Send(obj, Selector("retain"));

    internal static void Release(nint obj)
    {
        if (obj != 0)
        {
            SendVoid(obj, Selector("release"));
        }
    }

    internal static bool RespondsTo(nint obj, string selector) =>
        obj != 0 && SendBool(obj, Selector("respondsToSelector:"), Selector(selector)) != 0;

    /// <summary>An autorelease pool around <paramref name="body"/>: every call into AppKit needs one.</summary>
    internal static void WithPool(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        nint pool = New(RequireClass("NSAutoreleasePool"));
        try
        {
            body();
        }
        finally
        {
            SendVoid(pool, Selector("drain"));
        }
    }

    internal static T WithPool<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        nint pool = New(RequireClass("NSAutoreleasePool"));
        try
        {
            return body();
        }
        finally
        {
            SendVoid(pool, Selector("drain"));
        }
    }

    // ----- Raw runtime -----

    [LibraryImport(LibObjC)]
    private static partial nint objc_getClass(byte* name);

    [LibraryImport(LibObjC)]
    private static partial nint objc_getProtocol(byte* name);

    [LibraryImport(LibObjC)]
    private static partial nint sel_registerName(byte* name);

    [LibraryImport(LibObjC)]
    internal static partial nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);

    [LibraryImport(LibObjC)]
    internal static partial void objc_registerClassPair(nint cls);

    [LibraryImport(LibObjC)]
    internal static partial byte class_addMethod(nint cls, nint selector, nint implementation, byte* types);

    [LibraryImport(LibObjC)]
    internal static partial byte class_addProtocol(nint cls, nint protocol);

    [LibraryImport(LibObjC)]
    internal static partial nint class_getSuperclass(nint cls);

    [LibraryImport(LibObjC)]
    internal static partial nint object_getClass(nint obj);

    /// <summary>The receiver/class pair <c>objc_msgSendSuper</c> takes instead of a plain receiver.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SuperTarget
    {
        internal nint Receiver;
        internal nint SuperClass;
    }

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSendSuper")]
    internal static partial void SendSuperVoid(ref SuperTarget target, nint selector, nint arg1, nint arg2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSendSuper")]
    internal static partial nint SendSuper(ref SuperTarget target, nint selector, nint arg1);
}

/// <summary>Core Graphics' rectangle, in the layout AppKit and WebKit expect.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    internal double X;
    internal double Y;
    internal double Width;
    internal double Height;

    internal CGRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}
