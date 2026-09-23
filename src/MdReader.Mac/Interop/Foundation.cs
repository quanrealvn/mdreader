using System.Runtime.InteropServices;

namespace MdReader.Mac.Interop;

/// <summary>The handful of Foundation types the backend passes across the boundary.</summary>
internal static unsafe class Foundation
{
    private const uint NSUTF8StringEncoding = 4;

    /// <summary>An autoreleased NSString. Null and empty both produce an empty string, never nil.</summary>
    internal static nint String(string? value)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        fixed (byte* bytes = utf8)
        {
            nint allocated = ObjC.Send(ObjC.RequireClass("NSString"), ObjC.Selector("alloc"));
            nint result = ObjC.Send(allocated, ObjC.Selector("initWithBytes:length:encoding:"),
                                    (nint)bytes, (nint)utf8.Length, (nint)NSUTF8StringEncoding);
            return ObjC.Send(result, ObjC.Selector("autorelease"));
        }
    }

    /// <summary>The managed text of an NSString, or null for nil.</summary>
    internal static string? ToManagedString(nint nsString)
    {
        if (nsString == 0)
        {
            return null;
        }

        nint utf8 = ObjC.Send(nsString, ObjC.Selector("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>An autoreleased NSURL for a string, or 0 when the string isn't a URL.</summary>
    internal static nint Url(string value) =>
        ObjC.Send(ObjC.RequireClass("NSURL"), ObjC.Selector("URLWithString:"), String(value));

    /// <summary>An autoreleased file NSURL for a local path.</summary>
    internal static nint FileUrl(string path, bool isDirectory) =>
        ObjC.Send(ObjC.RequireClass("NSURL"), ObjC.Selector("fileURLWithPath:isDirectory:"),
                  String(path), isDirectory ? 1 : 0);

    internal static string? UrlAbsoluteString(nint url) =>
        url == 0 ? null : ToManagedString(ObjC.Send(url, ObjC.Selector("absoluteString")));

    /// <summary>The file-system path of a <c>file:</c> NSURL, or null for anything else.</summary>
    internal static string? UrlFileSystemPath(nint url)
    {
        if (url == 0 || ObjC.SendBool(url, ObjC.Selector("isFileURL")) == 0)
        {
            return null;
        }

        return ToManagedString(ObjC.Send(url, ObjC.Selector("path")));
    }

    /// <summary>An autoreleased NSData over a copy of the bytes.</summary>
    internal static nint Data(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes)
        {
            nint data = ObjC.Send(ObjC.RequireClass("NSData"), ObjC.Selector("dataWithBytes:length:"),
                                  (nint)p, (nint)bytes.Length);
            return data;
        }
    }

    /// <summary>Copies an NSData's bytes out, or null for nil.</summary>
    internal static byte[]? ToManagedBytes(nint nsData)
    {
        if (nsData == 0)
        {
            return null;
        }

        nint length = (nint)ObjC.SendUInt(nsData, ObjC.Selector("length"));
        var buffer = new byte[(int)length];
        if (length == 0)
        {
            return buffer;
        }

        nint bytes = ObjC.Send(nsData, ObjC.Selector("bytes"));
        Marshal.Copy(bytes, buffer, 0, (int)length);
        return buffer;
    }

    internal static nint Number(long value) =>
        ObjC.Send(ObjC.RequireClass("NSNumber"), ObjC.Selector("numberWithLongLong:"), (nint)value);

    /// <summary>An autoreleased NSDictionary from parallel key/value arrays (both autoreleased NSObjects).</summary>
    internal static nint Dictionary(ReadOnlySpan<nint> keys, ReadOnlySpan<nint> values)
    {
        nint keyArray = Array(keys);
        nint valueArray = Array(values);
        return ObjC.Send(ObjC.RequireClass("NSDictionary"), ObjC.Selector("dictionaryWithObjects:forKeys:"),
                         valueArray, keyArray);
    }

    /// <summary>An autoreleased NSArray over the given objects.</summary>
    internal static nint Array(ReadOnlySpan<nint> items)
    {
        fixed (nint* p = items)
        {
            return ObjC.Send(ObjC.RequireClass("NSArray"), ObjC.Selector("arrayWithObjects:count:"),
                             (nint)p, (nint)items.Length);
        }
    }

    internal static int ArrayCount(nint array) => array == 0 ? 0 : (int)ObjC.SendUInt(array, ObjC.Selector("count"));

    internal static nint ArrayItem(nint array, int index) =>
        ObjC.SendIndexed(array, ObjC.Selector("objectAtIndex:"), (nuint)index);

    /// <summary>An NSError's <c>localizedDescription</c>, or null when there is no error.</summary>
    internal static string? ErrorDescription(nint error) =>
        error == 0 ? null : ToManagedString(ObjC.Send(error, ObjC.Selector("localizedDescription")));

    /// <summary>Writes NSData to a file, returning false with the error text when it fails.</summary>
    internal static bool WriteData(nint data, string path, out string? error)
    {
        nint errorSlot = 0;
        byte ok = ObjC.SendBool(data, ObjC.Selector("writeToFile:options:error:"),
                                String(path), 1 /* NSDataWritingAtomic */, (nint)(&errorSlot));
        error = ok != 0 ? null : (ErrorDescription(errorSlot) ?? "The file couldn't be written.");
        return ok != 0;
    }
}
