using MdReader.Core.Diagnostics;
using MdReader.Shell.Threading;
using MdReader.Shell.ViewModels;

namespace MdReader.Shell.Commands;

/// <summary>
/// The single key router of ARCHITECTURE §4.12, with no UI framework in it. The shell installs itself on its window's
/// tunneling key events and forwards them here; the shell's own key enum is translated to <see cref="ShortcutKey"/>
/// first (WPF: <c>MdReader.App.Commands.WpfKeys</c>).
/// </summary>
/// <remarks>
/// - The tunneling key-down sees keys from the shell's focus and keys the web view forwards from its accelerator hook.
/// - Modifiers must match exactly (so AltGr = Ctrl+Alt never triggers Ctrl rows).
/// - A match returns true (the shell marks the event handled) and defers the action with
///   <see cref="IUiDispatcher.PostInput"/>: the browser process is blocked while a forwarded key is being handled, so no
///   web-view API may be called synchronously here.
/// - Auto-repeat is honored only for rows marked repeat. Keys forwarded by the web view never carry IsRepeat, so a key
///   that is still held since its last handled press (no key-up seen yet) also counts as a repeat.
/// </remarks>
public sealed class KeyboardShortcutRouter
{
    private const string Category = "Keyboard";

    private readonly MainViewModel _viewModel;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLog _log;
    private readonly HashSet<ShortcutKey> _heldKeys = [];

    public KeyboardShortcutRouter(MainViewModel viewModel, IUiDispatcher dispatcher, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(log);
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <summary>
    /// Handles a key press. True means the shell must mark its event handled; the action itself runs deferred.
    /// Rows with <c>HandledByWindow: false</c> are left alone so they keep tunneling to the document view.
    /// </summary>
    public bool HandleKeyDown(ShortcutKey key, ShortcutModifiers modifiers, bool isRepeat)
    {
        var shortcut = KeyboardShortcuts.Match(key, modifiers);
        if (shortcut is null || !shortcut.HandledByWindow)
        {
            return false;
        }

        var repeated = isRepeat || !_heldKeys.Add(key);
        if (repeated && !shortcut.AllowRepeat)
        {
            return true;   // handled, but nothing runs
        }

        var action = shortcut.Action;
        _dispatcher.PostInput(() => Execute(action));
        return true;
    }

    /// <summary>A key was released. <see cref="ShortcutKey.Modifier"/> clears everything (see <see cref="Reset"/>).</summary>
    public void HandleKeyUp(ShortcutKey key)
    {
        if (key == ShortcutKey.Modifier)
        {
            // The key-up of the main key may not be forwarded by the web view once its modifier is released first.
            _heldKeys.Clear();
        }
        else
        {
            _heldKeys.Remove(key);
        }
    }

    /// <summary>The window lost activation: forget every held key.</summary>
    public void Reset() => _heldKeys.Clear();

    private void Execute(ShortcutAction action)
    {
        var vm = _viewModel;
        var command = action switch
        {
            ShortcutAction.Open => vm.OpenCommand,
            ShortcutAction.CloseTab => vm.CloseActiveTabCommand,
            ShortcutAction.NextTab => vm.NextTabCommand,
            ShortcutAction.PreviousTab => vm.PreviousTabCommand,
            ShortcutAction.Find => vm.FindCommand,
            ShortcutAction.Print => vm.PrintCommand,
            ShortcutAction.ExportPdf => vm.ExportPdfCommand,
            ShortcutAction.ZoomIn => vm.ZoomInCommand,
            ShortcutAction.ZoomOut => vm.ZoomOutCommand,
            ShortcutAction.ZoomReset => vm.ZoomResetCommand,
            ShortcutAction.Reload => vm.ReloadCommand,
            ShortcutAction.ToggleToc => vm.ToggleTocCommand,
            ShortcutAction.ToggleSplitView => vm.ToggleSplitViewCommand,
            ShortcutAction.Save => vm.SaveCommand,
            _ => null,
        };

        if (command is null)
        {
            return;
        }

        _log.Write(AppLogLevel.Debug, Category, $"Shortcut {action}");
        command.Execute(null);
    }
}
