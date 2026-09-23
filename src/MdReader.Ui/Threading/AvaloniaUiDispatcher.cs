using Avalonia.Threading;
using MdReader.Shell.Threading;

namespace MdReader.Ui.Threading;

/// <see cref="IUiDispatcher"/> over the Avalonia <see cref="Dispatcher"/>.
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;
    private volatile bool _shutdownStarted;

    /// Must be constructed on the UI thread (Program does, right after Avalonia is initialized).
    public AvaloniaUiDispatcher()
        : this(Dispatcher.UIThread)
    {
    }

    public AvaloniaUiDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _dispatcher.ShutdownStarted += (_, _) => _shutdownStarted = true;
    }

    public Dispatcher Dispatcher => _dispatcher;

    /// Avalonia's dispatcher only announces the shutdown; it exposes no flag, so the event is latched here.
    public bool HasShutdownStarted => _shutdownStarted;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void VerifyAccess() => _dispatcher.VerifyAccess();

    public void Post(Action action) => _dispatcher.Post(action, DispatcherPriority.Normal);

    public void PostInput(Action action) => _dispatcher.Post(action, DispatcherPriority.Input);

    public void PostBackground(Action action) => _dispatcher.Post(action, DispatcherPriority.Background);

    /// Avalonia's DispatcherOperation is awaitable but exposes no Task, so the completion is bridged here.
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(
            () =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            },
            DispatcherPriority.Normal);
        return completion.Task;
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new AvaloniaUiTimer(interval, tick);

    private sealed class AvaloniaUiTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        internal AvaloniaUiTimer(TimeSpan interval, Action tick)
        {
            _timer = new DispatcherTimer(interval, DispatcherPriority.Normal, (_, _) => tick());
        }

        public bool IsEnabled => _timer.IsEnabled;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}
