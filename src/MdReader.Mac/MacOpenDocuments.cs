using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MdReader.Core.Diagnostics;
using MdReader.Mac.Interop;

namespace MdReader.Mac;

/// <summary>
/// The macOS side of single instance (ARCHITECTURE §4.5): LaunchServices never starts a second copy of a bundled
/// application, it activates the running one and hands it the files, so there is no mutex and no pipe — only these
/// application-delegate messages to answer.
/// </summary>
/// <remarks>
/// <para><b>Who handles what.</b> <c>application:openURLs:</c> is the modern message and the toolkit's own
/// application delegate already implements it; the shell subscribes to the event the toolkit raises for it rather
/// than fighting over the selector. What the toolkit does not implement is the older pair,
/// <c>application:openFile:</c> and <c>application:openFiles:</c>, which AppKit still sends when the app is opened
/// by an <c>odoc</c> Apple Event (a script, an older Finder path, <c>open -a</c> in some shapes). Those are added
/// here, and <b>only if the delegate's class doesn't already implement them</b>, so this never replaces behaviour
/// that belongs to someone else.</para>
/// <para>AppKit prefers <c>application:openURLs:</c> when it is implemented, so adding the older pair cannot make a
/// normal open arrive twice.</para>
/// </remarks>
public static unsafe class MacOpenDocuments
{
    private const string Category = "SingleInstance";

    private static readonly ConcurrentDictionary<nint, Action<IReadOnlyList<string>>> Handlers = new();

    /// <summary>
    /// Adds the legacy open-document messages to the running application delegate's class when they are missing.
    /// Safe to call more than once; the second call finds the methods present and does nothing.
    /// </summary>
    /// <param name="deliver">Called on the main thread with the absolute paths AppKit handed over.</param>
    /// <returns>True when at least one message was installed.</returns>
    public static bool InstallLegacyHandlers(Action<IReadOnlyList<string>> deliver, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        ArgumentNullException.ThrowIfNull(log);

        return ObjC.WithPool(() =>
        {
            nint application = ObjC.Send(ObjC.RequireClass("NSApplication"), ObjC.Selector("sharedApplication"));
            nint applicationDelegate = application == 0 ? 0 : ObjC.Send(application, ObjC.Selector("delegate"));
            if (applicationDelegate == 0)
            {
                log.Write(AppLogLevel.Warning, Category,
                    "There is no application delegate yet; the legacy open-document messages weren't installed.");
                return false;
            }

            Handlers[applicationDelegate] = deliver;
            nint cls = ObjC.object_getClass(applicationDelegate);
            var installed = false;
            installed |= TryAdd(cls, applicationDelegate, "application:openFile:", "c@:@@",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, byte>)&OpenFile, log);
            installed |= TryAdd(cls, applicationDelegate, "application:openFiles:", "v@:@@",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&OpenFiles, log);
            return installed;
        });
    }

    private static bool TryAdd(nint cls, nint instance, string selector, string types, nint implementation, IAppLog log)
    {
        if (ObjC.RespondsTo(instance, selector))
        {
            log.Write(AppLogLevel.Debug, Category, $"The application delegate already implements {selector}.");
            return false;
        }

        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(types + "\0");
        fixed (byte* p = utf8)
        {
            if (ObjC.class_addMethod(cls, ObjC.Selector(selector), implementation, p) == 0)
            {
                log.Write(AppLogLevel.Warning, Category, $"Couldn't install {selector} on the application delegate.");
                return false;
            }
        }

        log.Write(AppLogLevel.Debug, Category, $"Installed {selector} on the application delegate.");
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OpenFile(nint self, nint selector, nint application, nint path)
    {
        try
        {
            if (Foundation.ToManagedString(path) is { Length: > 0 } file && Handlers.TryGetValue(self, out var deliver))
            {
                deliver([file]);
                return 1;
            }
        }
        catch (Exception)
        {
            // Nothing may unwind into AppKit. The caller's own logging has already recorded whatever it could.
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OpenFiles(nint self, nint selector, nint application, nint paths)
    {
        // NSApplicationDelegateReplySuccess / …Failure: AppKit waits for this before the launch is considered done.
        const nint ReplySuccess = 0;
        const nint ReplyFailure = 1;
        nint reply = ReplyFailure;
        try
        {
            int count = Foundation.ArrayCount(paths);
            var files = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                if (Foundation.ToManagedString(Foundation.ArrayItem(paths, i)) is { Length: > 0 } file)
                {
                    files.Add(file);
                }
            }

            if (files.Count > 0 && Handlers.TryGetValue(self, out var deliver))
            {
                deliver(files);
                reply = ReplySuccess;
            }
        }
        catch (Exception)
        {
            // See OpenFile.
        }
        finally
        {
            ObjC.SendVoidLong(application, ObjC.Selector("replyToOpenOrPrint:"), reply);
        }
    }
}
