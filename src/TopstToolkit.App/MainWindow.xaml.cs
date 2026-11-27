using System.Windows;
using System.Windows.Input;

namespace TopstToolkit.App;

/// <summary>The one window: a frame for the title bar and the active screen layout (<see cref="ShellHost"/>).</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        StateChanged += (_, _) => ApplyWindowState();
        ApplyWindowState();
    }

    private void ApplyWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        // A maximized window with custom chrome hangs over the screen edge by the resize border; pad it back in.
        Frame.Padding = maximized
            ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + SystemParameters.FixedFrameVerticalBorderWidth + 1)
            : new Thickness(0);
        Host.SetMaximized(maximized);
    }
}
