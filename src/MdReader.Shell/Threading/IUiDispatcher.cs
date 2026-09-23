using System.Runtime.CompilerServices;

namespace MdReader.Shell.Threading;

/// <summary>
/// The shell's UI thread, without naming a UI framework (ARCHITECTURE §5). WPF maps this onto
/// <c>System.Windows.Threading.Dispatcher</c>, Avalonia onto <c>Avalonia.Threading.Dispatcher</c>.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Throws when the caller is not on the UI thread.</summary>
    void VerifyAccess();

    /// <summary>True once the UI thread has started shutting down (nothing posted will run any more).</summary>
    bool HasShutdownStarted { get; }

    /// <summary>Queues <paramref name="action"/> on the UI thread at normal priority and returns immediately.</summary>
    void Post(Action action);

    /// <summary>
    /// Queues <paramref name="action"/> at input priority: it runs after the input event being handled has finished.
    /// This is the "defer the action" rule of §4.12 — the browser process is blocked while a key it forwarded is being
    /// handled, so no web-view API may be called synchronously from a key handler.
    /// </summary>
    void PostInput(Action action);

    /// <summary>
    /// Queues <paramref name="action"/> at background priority: pending input and rendering run first. Used while a
    /// payload is posted in parts, so a large document never blocks the window (§7.1).
    /// </summary>
    void PostBackground(Action action);

    /// <summary>Queues <paramref name="action"/> and completes when it has run.</summary>
    Task InvokeAsync(Action action);

    /// <summary>A repeating UI-thread timer (normal priority), stopped and started by the caller.</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>A UI-thread timer. Ticks on the UI thread until <see cref="Stop"/> is called.</summary>
public interface IUiTimer
{
    bool IsEnabled { get; }

    void Start();

    void Stop();
}

public static class UiDispatcherExtensions
{
    /// <summary>
    /// Continues on the UI thread after an <c>await</c> that left it, without relying on an ambient
    /// <see cref="SynchronizationContext"/>: sessions are created before the UI loop runs (CLI files, --capture), so
    /// there may not be one yet.
    /// </summary>
    public static UiYieldAwaitable ReturnToUiThread(this IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return new UiYieldAwaitable(dispatcher, background: false);
    }

    /// <summary>Lets the UI thread render and handle input before the continuation runs.</summary>
    public static UiYieldAwaitable YieldToBackground(this IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return new UiYieldAwaitable(dispatcher, background: true);
    }
}

/// <summary>Awaitable behind <see cref="UiDispatcherExtensions.ReturnToUiThread"/> and <c>YieldToBackground</c>.</summary>
public readonly struct UiYieldAwaitable(IUiDispatcher dispatcher, bool background) : INotifyCompletion
{
    public UiYieldAwaitable GetAwaiter() => this;

    /// <summary>A background yield always suspends; a plain return continues inline when already on the UI thread.</summary>
    public bool IsCompleted => !background && dispatcher.CheckAccess();

    public void OnCompleted(Action continuation)
    {
        if (background)
        {
            dispatcher.PostBackground(continuation);
        }
        else
        {
            dispatcher.Post(continuation);
        }
    }

    public void GetResult()
    {
    }
}
