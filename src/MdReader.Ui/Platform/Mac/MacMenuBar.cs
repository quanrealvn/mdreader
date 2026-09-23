using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using MdReader.Core.Diagnostics;
using MdReader.Shell.ViewModels;
using MdReader.Ui.Commands;
using MdReader.Ui.Views;

namespace MdReader.Ui.Platform.Mac;

/// <summary>
/// Turns <see cref="ShellMenuModel.MacMenuBar"/> into Avalonia's <see cref="NativeMenu"/>, which the macOS backend
/// installs as the application's real menu bar.
/// </summary>
/// <remarks>
/// Attached to the <c>Application</c> rather than to the window, so the menu bar exists whether or not a window is
/// open — a Mac application with no menu bar looks broken. AppKit adds the standard Services, Hide, Hide Others,
/// Show All and Quit items to the first menu itself, so only "About MdReader" is listed there.
/// </remarks>
internal static class MacMenuBar
{
    private const string Category = "Menu";

    /// <summary>Builds the menu bar and attaches it to <paramref name="application"/>.</summary>
    /// <param name="showAbout">The shell's own About window; the only item that isn't a view-model command.</param>
    internal static void Attach(Avalonia.Application application, MainViewModel viewModel, Action showAbout, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(showAbout);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            NativeMenu.SetMenu(application, Build(viewModel, showAbout, log));
        }
        catch (Exception ex)
        {
            // A missing menu bar is ugly but not fatal: every command is still on the toolbar.
            log.Write(AppLogLevel.Warning, Category, "Couldn't install the macOS menu bar.", ex);
        }
    }

    internal static NativeMenu Build(MainViewModel viewModel, Action showAbout, IAppLog log)
    {
        var bar = new NativeMenu();
        foreach (ShellMenu menu in ShellMenuModel.MacMenuBar)
        {
            var items = new NativeMenu();
            foreach (ShellMenuItem item in menu.Items)
            {
                items.Add(item.IsSeparator ? new NativeMenuItemSeparator() : BuildItem(item, viewModel, showAbout, log));
            }

            bar.Add(new NativeMenuItem(menu.Header) { Menu = items });
        }

        return bar;
    }

    private static NativeMenuItem BuildItem(ShellMenuItem item, MainViewModel viewModel, Action showAbout, IAppLog log)
    {
        var menuItem = new NativeMenuItem(item.Header)
        {
            Command = Resolve(item.Command!.Value, viewModel, showAbout),
        };

        if (item.Gesture is { } action && MacGestures.For(action) is { } gesture)
        {
            try
            {
                menuItem.Gesture = KeyGesture.Parse(gesture);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                // The item still works from the menu; only its key equivalent is missing.
                log.Write(AppLogLevel.Warning, Category, $"'{gesture}' isn't a key gesture ({item.Header}).", ex);
            }
        }

        return menuItem;
    }

    /// <summary>
    /// The one place a menu item is bound to what it does. Toggles are commands rather than checked items on purpose:
    /// the toolbar's toggle buttons are already bound to the same view-model properties, so a second source of truth
    /// for the check mark would be a second thing to keep in step.
    /// </summary>
    private static ICommand Resolve(ShellCommand command, MainViewModel viewModel, Action showAbout) => command switch
    {
        ShellCommand.Open => viewModel.OpenCommand,
        ShellCommand.CloseTab => viewModel.CloseActiveTabCommand,
        ShellCommand.Save => viewModel.SaveCommand,
        ShellCommand.Reload => viewModel.ReloadCommand,
        ShellCommand.Print => viewModel.PrintCommand,
        ShellCommand.ExportPdf => viewModel.ExportPdfCommand,
        ShellCommand.Find => viewModel.FindCommand,
        ShellCommand.FindNext => new FindCommand(viewModel, backwards: false),
        ShellCommand.FindPrevious => new FindCommand(viewModel, backwards: true),
        ShellCommand.ToggleToc => viewModel.ToggleTocCommand,
        ShellCommand.ToggleSplitView => viewModel.ToggleSplitViewCommand,
        ShellCommand.ZoomIn => viewModel.ZoomInCommand,
        ShellCommand.ZoomOut => viewModel.ZoomOutCommand,
        ShellCommand.ZoomReset => viewModel.ZoomResetCommand,
        ShellCommand.CycleTheme => viewModel.CycleThemeCommand,
        ShellCommand.NextTab => viewModel.NextTabCommand,
        ShellCommand.PreviousTab => viewModel.PreviousTabCommand,
        ShellCommand.About => new DelegateCommand(showAbout),
        _ => new DelegateCommand(() => { }),
    };

    /// <summary>
    /// Find next / previous belong to the active tab's find bar, which the view model doesn't reach (§4.12 leaves
    /// those rows to the document view), so the menu opens the find bar and lets it take it from there.
    /// </summary>
    private sealed class FindCommand(MainViewModel viewModel, bool backwards) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => viewModel.FindCommand.CanExecute(parameter);

        public void Execute(object? parameter)
        {
            if (viewModel.ActiveTab is DocumentTabViewModel { View: IFindableView view })
            {
                view.FindFromMenu(backwards);
            }
            else
            {
                viewModel.FindCommand.Execute(parameter);
            }
        }
    }

    private sealed class DelegateCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
