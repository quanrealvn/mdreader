using Avalonia;
using Avalonia.Markup.Xaml;

namespace MdReader.Ui;

/// The Avalonia application object. Program.Main owns the startup sequence, so nothing is created here.
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
