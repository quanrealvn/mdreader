using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using MdReader.Avalonia.Spike;
using MdReader.Avalonia.Views;

namespace MdReader.Avalonia;

/// <summary>
/// The spike builds its UI in C# rather than XAML on purpose: it keeps the diff about the WebView2 seam, and the real
/// port's views become .axaml when the shared shell layer is extracted.
/// </summary>
internal sealed class App(SpikeContext context) : Application
{
    private readonly SpikeContext _context = context;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _context.Lifetime = desktop;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow(_context);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
