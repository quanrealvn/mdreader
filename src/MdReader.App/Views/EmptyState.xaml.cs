using System.Windows.Controls;

namespace MdReader.App.Views;

/// Placeholder shown while no document is open. All behavior is bound to MainViewModel commands.
public partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
    }
}
