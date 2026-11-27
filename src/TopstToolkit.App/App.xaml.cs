using System.Windows;
using System.Windows.Threading;
using TopstToolkit.App.Dev;
using TopstToolkit.App.Services;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App;

/// <summary>
/// Start-up. Normal run: one window. Development: <c>TopstToolkit.exe --shots &lt;folder&gt; [--demo | --run &lt;model&gt;]</c>
/// renders the screens to PNG files without showing a window, for checking them against the mockups (see Dev\ScreenCapture).
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Started by ssh as its SSH_ASKPASS (보드 › 처음 연결, Core.Board.BoardKeys): print the board's password that the
        // app put in this process's environment, and nothing else — no window, no settings.
        if (Environment.GetEnvironmentVariable(TopstToolkit.Core.Board.BoardKeys.AskpassVariable) is { } secret)
        {
            using (var stdout = Console.OpenStandardOutput())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(secret + "\n");
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        // --install-components …: Setup (right after copying the app) or the app itself (with administrator rights, for a
        // components folder in Program Files) installs components without a window (InstallHelper, design §7.2)
        if (e.Args.Contains(InstallHelper.Switch))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try
            {
                code = await Task.Run(() => InstallHelper.RunAsync(e.Args));
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TOPST-Toolkit-helper.error.txt"), $"{DateTime.Now:s} {ex}\n");
                code = 3;
            }
            Shutdown(code);
            return;
        }

        // --prepare-user: Setup runs it as the user who installs (not elevated) to make 내 문서\TOPST Toolkit
        if (e.Args.Contains(InstallHelper.PrepareUserSwitch))
        {
            Shutdown(TopstToolkit.Core.Workspace.SaveFolders.EnsureCreated(TopstToolkit.Core.Settings.AppSettings.Load().SaveRoot) ? 0 : 1);
            return;
        }

        DispatcherUnhandledException += OnUnhandled;

        // --startup-check <file>: the double-click folder's check (build\publish-app.ps1) — which .NET this exe runs on,
        // written to the file. No window: nothing may appear on the user's screen while the app is tested.
        var checkAt = Array.IndexOf(e.Args, "--startup-check");
        if (checkAt >= 0 && checkAt + 1 < e.Args.Length)
        {
            System.IO.File.WriteAllLines(e.Args[checkAt + 1],
                [typeof(object).Assembly.Location, typeof(Window).Assembly.Location, Environment.Version.ToString()]);
            Shutdown(0);
            return;
        }

        var shotsAt = Array.IndexOf(e.Args, "--shots");
        if (shotsAt >= 0 && shotsAt + 1 < e.Args.Length)
        {
            string? Arg(string name) => Array.IndexOf(e.Args, name) is var at and >= 0 && at + 1 < e.Args.Length ? e.Args[at + 1] : null;
            var exitCode = await ScreenCapture.RunAsync(e.Args[shotsAt + 1], demo: e.Args.Contains("--demo"), Arg("--run"), Arg("--force-output"), Arg("--prefix"), Arg("--project"), Arg("--check"),
                components: e.Args.Contains("--components"), zooModel: Arg("--zoo"), board: Arg("--board"), boardVideo: Arg("--board-video"),
                intro: Arg("--intro"), update: Arg("--update"));
            Shutdown(exitCode);
            return;
        }

        AppServices services;
        try
        {
            // --workspace <folder>: keep a trial run away from the real projects; --settings <file>: and away from the real settings
            string? Option(string name) => Array.IndexOf(e.Args, name) is var at and >= 0 && at + 1 < e.Args.Length ? e.Args[at + 1] : null;
            services = AppServices.Create(Option("--workspace"), Option("--settings"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TOPST Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // 내 문서\TOPST Toolkit: Setup made it for whoever installed the app; anyone else on the PC gets it here
        _ = Task.Run(() => TopstToolkit.Core.Workspace.SaveFolders.EnsureCreated(services.Settings.SaveRoot));

        var shell = new ShellViewModel(services);
        var window = new MainWindow { DataContext = shell };
        MainWindow = window;
        window.Show();
        window.Host.PlayIntro(); // the start-up animation; the app is ready underneath it
        _ = shell.Home.CheckEngineAsync(); // the app is usable while the engine imports (2–5 s)
        _ = shell.Update.StartAsync(); // how the last update ended, then a quiet look for a newer version (design §7.5)
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show("예상하지 못한 오류가 났어요.\n\n" + e.Exception.Message, "TOPST Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
