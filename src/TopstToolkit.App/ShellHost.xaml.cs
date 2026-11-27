using System.Windows;
using System.Windows.Controls;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Shells.Rail;
using TopstToolkit.Core.Settings;

namespace TopstToolkit.App;

public partial class ShellHost : UserControl
{
    public ShellHost()
    {
        InitializeComponent();
        SetLayout(AppSettings.LayoutRail);
    }

    /// <summary>
    /// Puts one screen layout into the window (decision D9, setting <c>ui.layout</c>). Phase 1 has the rail layout (A);
    /// the pipeline board (B) and focus (C) layouts become cases here when they are built.
    /// </summary>
    public void SetLayout(string layout)
    {
        LayoutHost.Content = layout switch
        {
            // AppSettings.LayoutPipeline => new Shells.Pipeline.PipelineShell(),
            // AppSettings.LayoutFocus => new Shells.Focus.FocusShell(),
            _ => new RailShell(),
        };
    }

    /// <summary>
    /// Plays the start-up animation over the app (about 3.2 s; a click or a key skips it). Not when Windows is set to
    /// show no animations (Settings › Accessibility › Visual effects).
    /// </summary>
    public void PlayIntro()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        Intro.Finished += (_, _) => ((Grid)Intro.Parent).Children.Remove(Intro);
        Intro.Play();
    }

    /// <summary>Screen captures: the animation stopped at <paramref name="seconds"/>.</summary>
    public void ShowIntroFrame(double seconds)
    {
        Intro.Visibility = Visibility.Visible;
        Intro.ShowFrame(seconds);
    }

    /// <summary>The window tells the title bar which caption button to show.</summary>
    public void SetMaximized(bool maximized)
    {
        MaxIcon.Glyph = maximized ? IconGlyph.ChromeRestore : IconGlyph.ChromeMaximize;
        MaxButton.Command = maximized ? SystemCommands.RestoreWindowCommand : SystemCommands.MaximizeWindowCommand;
        MaxButton.ToolTip = maximized ? "이전 크기로" : "최대화";
    }
}
