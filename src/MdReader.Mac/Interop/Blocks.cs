using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MdReader.Mac.Interop;

/// <summary>
/// Objective-C blocks, by hand: the completion handlers WebKit hands us (navigation policy, permissions, panels) and
/// the ones we hand WebKit (<c>evaluateJavaScript:</c>, <c>createPDFWithConfiguration:</c>, <c>takeSnapshot…</c>,
/// <c>findString:</c>).
/// </summary>
/// <remarks>
/// <para>The blocks we create are marked <c>BLOCK_IS_GLOBAL</c>. A global block's <c>Block_copy</c> returns the same
/// pointer and its <c>Block_release</c> does nothing, so WebKit holding on to one can't move or free it; we own the
/// memory and free it when the handler runs. A handler WebKit never calls (a view torn down mid-flight) leaks one
/// 48-byte allocation, which is why the completion callbacks below are the only things allocated this way.</para>
/// <para>The blocks WebKit hands us are only valid for the duration of the call, so they are invoked synchronously,
/// never stored.</para>
/// </remarks>
internal static unsafe class Blocks
{
    private const int BlockIsGlobal = 1 << 28;
    private const int BlockHasSignature = 1 << 30;

    private static readonly ConcurrentDictionary<nint, object> Callbacks = new();
    private static readonly nint GlobalBlockClass = LoadGlobalBlockClass();
    private static readonly nint TwoArgumentDescriptor = CreateDescriptor("v@?@@");
    private static readonly nint OneArgumentDescriptor = CreateDescriptor("v@?@");
    private static long _nextToken;

    /// <summary>A <c>void (^)(id, NSError *)</c> block; the callback runs once and the block is freed.</summary>
    internal static nint ResultAndError(Action<nint, nint> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Create(callback, TwoArgumentDescriptor, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&InvokeResultAndError);
    }

    /// <summary>A <c>void (^)(id)</c> block; the callback runs once and the block is freed.</summary>
    internal static nint SingleResult(Action<nint> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Create(callback, OneArgumentDescriptor, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&InvokeSingleResult);
    }

    // ----- Calling a block WebKit gave us -----

    /// <summary><c>block()</c>.</summary>
    internal static void Call(nint block)
    {
        if (block != 0)
        {
            ((delegate* unmanaged[Cdecl]<nint, void>)InvokePointer(block))(block);
        }
    }

    /// <summary><c>block(argument)</c> — an enum, a BOOL, an object or nil.</summary>
    internal static void Call(nint block, nint argument)
    {
        if (block != 0)
        {
            ((delegate* unmanaged[Cdecl]<nint, nint, void>)InvokePointer(block))(block, argument);
        }
    }

    /// <summary><c>block(first, second)</c> — the authentication challenge's disposition and credential.</summary>
    internal static void Call(nint block, nint first, nint second)
    {
        if (block != 0)
        {
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)InvokePointer(block))(block, first, second);
        }
    }

    /// <summary>The <c>invoke</c> field: past <c>isa</c> (8), <c>flags</c> (4) and <c>reserved</c> (4).</summary>
    private static nint InvokePointer(nint block) => *(nint*)(block + 16);

    // ----- Creating a block -----

    private static nint Create(object callback, nint descriptor, nint invoke)
    {
        nint token = (nint)Interlocked.Increment(ref _nextToken);
        Callbacks[token] = callback;

        var block = (BlockLiteral*)NativeMemory.Alloc((nuint)sizeof(BlockLiteral));
        block->Isa = GlobalBlockClass;
        block->Flags = BlockIsGlobal | BlockHasSignature;
        block->Reserved = 0;
        block->Invoke = invoke;
        block->Descriptor = descriptor;
        block->Token = token;
        return (nint)block;
    }

    private static object? Take(nint block)
    {
        nint token = ((BlockLiteral*)block)->Token;
        Callbacks.TryRemove(token, out object? callback);
        NativeMemory.Free((void*)block);
        return callback;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void InvokeResultAndError(nint block, nint result, nint error)
    {
        object? callback = Take(block);
        if (callback is Action<nint, nint> typed)
        {
            typed(result, error);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void InvokeSingleResult(nint block, nint result)
    {
        object? callback = Take(block);
        if (callback is Action<nint> typed)
        {
            typed(result);
        }
    }

    private static nint CreateDescriptor(string signature)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(signature + "\0");
        var text = (byte*)NativeMemory.Alloc((nuint)utf8.Length);
        utf8.AsSpan().CopyTo(new Span<byte>(text, utf8.Length));

        var descriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)sizeof(BlockLiteral);
        descriptor->Signature = (nint)text;
        return (nint)descriptor;
    }

    private static nint LoadGlobalBlockClass()
    {
        nint libSystem = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        return NativeLibrary.GetExport(libSystem, "_NSConcreteGlobalBlock");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        internal nint Isa;
        internal int Flags;
        internal int Reserved;
        internal nint Invoke;
        internal nint Descriptor;

        /// Our own field, past the ABI's five. A global block is never copied, so it travels with the literal.
        internal nint Token;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        internal nuint Reserved;
        internal nuint Size;
        internal nint Signature;
    }
}
