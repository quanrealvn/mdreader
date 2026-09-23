using System.Windows.Threading;
using MdReader.Shell.Threading;

namespace MdReader.App.Threading;

/// <see cref="IUiDispatcher"/> over the WPF <see cref="Dispatcher"/>.
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// Must be constructed on the UI thread (Program does, right after the App object exists).
    public WpfUiDispatcher()
        : this(Dispatcher.CurrentDispatcher)
    {
    }

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public Dispatcher Dispatcher => _dispatcher;

    public bool HasShutdownStarted => _dispatcher.HasShutdownStarted;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void VerifyAccess() => _dispatcher.VerifyAccess();

    public void Post(Action action) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);

    public void PostInput(Action action) => _dispatcher.BeginInvoke(DispatcherPriority.Input, action);

    public void PostBackground(Action action) => _dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new WpfUiTimer(_dispatcher, interval, tick);

    private sealed class WpfUiTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        internal WpfUiTimer(Dispatcher dispatcher, TimeSpan interval, Action tick)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = interval };
            _timer.Tick += (_, _) => tick();
        }

        public bool IsEnabled => _timer.IsEnabled;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}
