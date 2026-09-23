using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MdReader.Ui.Views;

/// <summary>
/// A menu item that can be activated through UI Automation. Avalonia's <see cref="MenuItemAutomationPeer"/> exposes no
/// control pattern at all, so a screen reader — or the UI tests, which drive both shells through the §4.12 automation
/// ids — can read a menu item but never run it. The WPF shell's menu items carry the Invoke pattern, so every menu item
/// in this shell is one of these.
/// </summary>
public class InvokableMenuItem : MenuItem
{
    protected override AutomationPeer OnCreateAutomationPeer() => new InvokablePeer(this);

    private sealed class InvokablePeer(MenuItem owner) : MenuItemAutomationPeer(owner), IInvokeProvider
    {
        /// The same event a click raises: the menu closes and the item's command runs.
        public void Invoke() => Owner.RaiseEvent(new RoutedEventArgs(ClickEvent));
    }
}
