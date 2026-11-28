using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TopstToolkit.App.Services;
using TopstToolkit.App.ViewModels;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Presets;
using TopstToolkit.Core.Projects;

namespace TopstToolkit.App.Dev;

/// <summary>
/// Development aid: renders the window content (title bar + layout) at 1280 × 800 and saves PNG files, with no window
/// shown. Two modes:
/// <list type="bullet">
/// <item><c>--demo</c>: a throw-away workspace with the three projects of the mockups; every screen once.</item>
/// <item><c>--run &lt;model&gt;</c>: the real thing — brings the model in and builds it through the same view models
/// the window uses, and captures each step with real data. Also writes <c>run.txt</c> with what happened.</item>
/// <item><c>--project &lt;id&gt;</c>: opens a project an earlier <c>--run</c> left in the capture workspace and captures
/// its screens again, without building (quick, for working on the look of a screen).</item>
/// <item><c>--check &lt;model&gt;</c>: brings the model in — which checks it (design §5.7) — and captures step 1 and
/// the graph "자를 곳" in the states a user can reach, without building (about ten seconds).</item>
/// </list>
/// </summary>
internal static class ScreenCapture
{
    private const int Width = 1280;
    private const int Height = 800;

    /// <summary>
    /// <c>TOPST_SHOTS_SCALE=2</c>: the stills at twice the pixels (2560 × 1600, text drawn sharp, for a web page); frames
    /// for animations stay at 1280 × 800. 1 when unset.
    /// </summary>
    private static readonly double StillScale =
        double.TryParse(Environment.GetEnvironmentVariable("TOPST_SHOTS_SCALE"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var scale) && scale is >= 1 and <= 3 ? scale : 1;

    /// <summary>
    /// <c>TOPST_SHOTS_FRAMES=&lt;seconds&gt;</c>: while a model is brought in and built (<c>--run</c>), a 1280 × 800 picture
    /// that often into <c>&lt;prefix&gt;-frames\</c>, to make an animation of the app at work from.
    /// </summary>
    private static readonly double FrameSeconds =
        double.TryParse(Environment.GetEnvironmentVariable("TOPST_SHOTS_FRAMES"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 0;

    /// <summary><c>TOPST_SHOTS_NOLOG=1</c>: the log panel stays closed in the frames (its lines show the capture's folders).</summary>
    private static readonly bool FramesWithoutLog = Environment.GetEnvironmentVariable("TOPST_SHOTS_NOLOG") == "1";

    /// <summary>Saves numbered 1280 × 800 frames while it runs (<see cref="FrameSeconds"/>); does nothing when that is 0.</summary>
    private sealed class FrameRecorder : IDisposable
    {
        private readonly DispatcherTimer? _timer;
        private int _count;

        public FrameRecorder(FrameworkElement host, string folder, ShellViewModel? shell = null)
        {
            if (FrameSeconds <= 0)
                return;
            Directory.CreateDirectory(folder);
            _count = Directory.GetFiles(folder, "*.png").Length;
            void Frame()
            {
                if (FramesWithoutLog && shell is not null)
                    shell.IsLogOpen = false;
                Save(host, Path.Combine(folder, $"{_count++:D4}.png"), scale: 1);
            }
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(FrameSeconds), DispatcherPriority.Background, (_, _) => Frame(), host.Dispatcher);
            Frame();
            _timer.Start();
        }

        public void Dispose() => _timer?.Stop();
    }

    /// <summary>No dialogs while capturing: questions are answered yes, pickers pick nothing, nothing is opened.</summary>
    private sealed class SilentDialogs : DialogService
    {
        /// <summary>What "pick a folder" answers; null = the user closed the picker.</summary>
        public string? Folder { get; set; }

        public override string? PickFile(string title, string filter) => null;
        public override IReadOnlyList<string> PickImages() => [];
        public override string? PickFolder(string title, string? initial = null) => Folder;
        public override bool Confirm(string title, string message) => true;
        public override void ShowInExplorer(string path) { }
        public override void OpenWithShell(string path) { }
        public override void OpenUrl(string url) { }
    }

    public static async Task<int> RunAsync(string outDir, bool demo, string? model, string? forceOutput, string? prefix, string? projectId = null,
        string? checkModel = null, bool components = false, string? zooModel = null, string? board = null, string? boardVideo = null,
        string? intro = null, string? update = null)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        try
        {
            // --update e2e hands over to a real Setup that starts the app again with the settings it finds: those must be a test's
            if (update == "e2e" && Environment.GetEnvironmentVariable(TopstToolkit.Core.Settings.AppSettings.PathVariable) is not { Length: > 0 })
                throw new InvalidOperationException("--update e2e installs for real: set TOPST_SETTINGS to a test settings file with its own WorkspaceRoot first.");
            if (update == "e2e")
                return await UpdateEndToEndAsync(outDir, prefix ?? "update");
            var workspace = Path.Combine(outDir, "_workspace");
            // the start-up animation plays over whatever an earlier capture left in the workspace (the guide: a real project)
            if (model is null && projectId is null && checkModel is null && intro is null && Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
            if (components && Environment.GetEnvironmentVariable(TopstToolkit.Core.Components.ComponentsRoot.EnvironmentVariable) is not { Length: > 0 })
                throw new InvalidOperationException("--components installs for real: set TOPST_COMPONENTS_ROOT to a test folder first, never the user's own components folder.");

            // its own settings file: a capture never writes into the user's settings (the model zoo saves a consent)
            var services = AppServices.Create(workspace, Path.Combine(outDir, "_settings.json"));
            services.Dialogs = new SilentDialogs();
            if (demo)
                SeedDemoProjects(services.Projects);

            var shell = new ShellViewModel(services);
            await shell.Home.CheckEngineAsync();
            var host = new ShellHost { DataContext = shell, Width = Width, Height = Height };

            if (model is not null)
                return await RunModelAsync(shell, services, host, outDir, Path.GetFullPath(model), forceOutput, prefix ?? "run");
            if (projectId is not null && board is not null)
                return await BoardAsync(shell, services, host, outDir, projectId, board, boardVideo is null ? null : Path.GetFullPath(boardVideo), prefix ?? "board");
            if (projectId is not null)
                return await ReopenAsync(shell, services, host, outDir, projectId, prefix ?? projectId);
            if (checkModel is not null)
                return await CheckModelAsync(shell, host, outDir, Path.GetFullPath(checkModel), prefix ?? "check");
            if (components)
                return await ComponentsAsync(shell, services, host, outDir, prefix ?? "components");
            if (zooModel is not null)
                return await ZooAsync(shell, services, host, outDir, zooModel, prefix ?? "zoo");
            if (update is not null)
                return await UpdateDemoAsync(shell, services, host, outDir, prefix ?? "update");

            // --demo --intro 0.25,1.2,...: the start-up animation stopped at those seconds, over the demo home screen
            if (intro is not null)
            {
                shell.Home.Refresh();
                host.Measure(new Size(Width, Height));
                host.Arrange(new Rect(0, 0, Width, Height));
                host.UpdateLayout();
                foreach (var part in intro.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var seconds = double.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
                    host.ShowIntroFrame(seconds);
                    Save(host, Path.Combine(outDir, $"{prefix ?? "intro"}-{seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}.png"));
                }
                return 0;
            }

            shell.Home.Refresh();
            Save(host, Path.Combine(outDir, "01-home.png"));
            if (shell.Home.Recent.FirstOrDefault() is { } first)
            {
                shell.OpenProject(first.File);
                var n = 2;
                foreach (var step in shell.Steps)
                {
                    shell.NavigateCommand.Execute(step);
                    await Settle(shell);
                    Save(host, Path.Combine(outDir, $"{n++:00}-{step.Key}.png"));
                }
                shell.NavigateCommand.Execute(shell.SettingsItem);
                await Settle(shell);
                Save(host, Path.Combine(outDir, $"{n++:00}-settings.png"));
                shell.NavigateTo("check");
                Save(host, Path.Combine(outDir, $"{n:00}-check.png"));
            }
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
            return 1;
        }
    }

    /// <summary>One model through the whole app, the way a user would go.</summary>
    private static async Task<int> RunModelAsync(ShellViewModel shell, AppServices services, ShellHost host, string outDir, string model, string? forceOutput, string prefix)
    {
        var log = new StringBuilder();
        void Note(string line)
        {
            log.AppendLine(line);
            File.WriteAllText(Path.Combine(outDir, prefix + ".txt"), log.ToString(), new UTF8Encoding(false));
        }
        string Shot(string name) => Path.Combine(outDir, $"{prefix}-{name}.png");

        // 1 모델
        shell.StartNewProject();
        Save(host, Shot("10-model-empty"));
        var modelPage = (ModelStepViewModel)shell.CurrentPage!;
        using (new FrameRecorder(host, Path.Combine(outDir, prefix + "-frames"), shell))
            await modelPage.ImportAsync(model);
        var project = shell.Project!;
        Note($"project   : {project.Id} · {project.Name}");
        Note($"preset    : {project.Preset} · task {project.Task} · numClass {project.Options.Convert.NumClass} · post {project.Options.Convert.PostProcess}");
        Note($"model     : error=[{modelPage.Error}] notes=[{string.Join(" | ", modelPage.Notes)}]");
        Save(host, Shot("11-model"));

        // 2 설정
        shell.NavigateTo("config");
        Save(host, Shot("20-config"));
        if (forceOutput is not null)
        {
            project.Options.Convert.ForceOutput = [.. forceOutput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            shell.MarkSettingsChanged();
        }

        // 3 데이터
        shell.NavigateTo("data");
        await Settle(shell);
        var data = (DataViewModel)shell.CurrentPage!;
        Note($"data      : {data.SummaryText} · test pictures {data.TestImages.Count} · warnings [{string.Join(" | ", data.Warnings)}] error=[{data.Error}]");
        Save(host, Shot("30-data"));

        // 4 빌드: one capture while the compiler runs, one at the end
        var build = shell.Build!;
        var midShot = false;
        var compile = build.Stages.First(s => s.Stage == BuildStage.Compile);
        void OnCompile(object? sender, PropertyChangedEventArgs e)
        {
            if (midShot || e.PropertyName != nameof(StageCardViewModel.IsRunning) || !compile.IsRunning)
                return;
            midShot = true;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (build.IsRunning)
                    Save(host, Shot("40-build-running"));
            };
            timer.Start();
        }
        compile.PropertyChanged += OnCompile;

        shell.NavigateTo("build");
        Save(host, Shot("39-build-before"));
        using (new FrameRecorder(host, Path.Combine(outDir, prefix + "-frames"), shell))
        {
            await shell.BuildCommand.ExecuteAsync(null);
            await Settle(shell);
        }
        Note($"build 1   : {build.StatusText} · {build.TimeText} · executed {build.ExecutedText} · reused {build.ReusedText} · memory {build.MemoryText}");
        Note($"            error=[{build.ErrorTitle}] [{build.ErrorDetail}] incompatible {build.IncompatibleLayers.Count} · cut candidates {build.CutCandidates.Count} · chosen [{string.Join(", ", build.CutCandidates.Where(c => c.IsChecked).Select(c => c.Name))}]");
        foreach (var stage in build.Stages)
            Note($"            {stage.Name,-12} {stage.ChipText,-6} {stage.TimeText,-6} {stage.Detail}");
        Save(host, Shot("41-build-done"));

        // layers the NPU cannot run: take the app's suggestion and build again, as the screen offers
        if (build.HasCutCandidates && build.CutCandidates.Any(c => c.IsChecked))
        {
            await build.ApplyCutCommand.ExecuteAsync(null);
            await Settle(shell);
            Note($"build 2   : {build.StatusText} · {build.TimeText} · executed {build.ExecutedText} · reused {build.ReusedText} · error=[{build.ErrorTitle}]");
            foreach (var stage in build.Stages)
                Note($"            {stage.Name,-12} {stage.ChipText,-6} {stage.TimeText,-6} {stage.Detail}");
            Save(host, Shot("42-build-after-cut"));
        }
        compile.PropertyChanged -= OnCompile;
        if (!build.Succeeded)
        {
            Note("stopped: the build did not succeed");
            return 2;
        }

        // a second build must reuse everything
        await shell.BuildCommand.ExecuteAsync(null);
        await Settle(shell);
        Note($"rebuild   : {build.StatusText} · executed {build.ExecutedText} · reused {build.ReusedText}");
        shell.IsLogOpen = false;

        // 5 결과
        shell.NavigateTo("result");
        await Settle(shell);
        var result = (ResultViewModel)shell.CurrentPage!;
        Note($"result    : {result.Caption} · fp32 {result.HasFp32} · callout [{result.CalloutText}]");
        foreach (var row in result.Rows)
            Note($"            {row.Name,-10} {row.ScoreText,-8} {row.DeltaText}");
        foreach (var fact in result.Facts)
            Note($"            {fact.Label}: {fact.Value}");
        Save(host, Shot("50-result"));
        if (result.HasFp32)
        {
            result.IsBoth = true;
            Save(host, Shot("51-result-both"));
            result.IsInt8 = true;
        }

        // 6 성능
        shell.NavigateTo("perf");
        var perf = (PerfViewModel)shell.CurrentPage!;
        Note($"perf      : {perf.TimeText} ms · {perf.FpsText} · memory {perf.TotalMemoryText} · ops {perf.OpsText}");
        Save(host, Shot("60-perf"));

        // 7 내보내기
        shell.NavigateTo("export");
        var export = (ExportViewModel)shell.CurrentPage!;
        Save(host, Shot("70-export"));   // with the default place (내 문서\TOPST Toolkit\내보내기, TOPST_SAVE_ROOT in captures)
        export.Destination = Path.Combine(outDir, "_export");
        await export.ExportCommand.ExecuteAsync(null);
        Note($"export    : {export.ResultPath} · {export.ResultSummary} · error=[{export.Error}] · {string.Join(" · ", export.ResultFacts.Select(f => f.Label + "=" + f.Value))}");
        Save(host, Shot("71-export-done"));

        shell.NavigateCommand.Execute(shell.SettingsItem);
        Save(host, Shot("80-settings"));
        shell.NavigateCommand.Execute(shell.HomeItem);
        Save(host, Shot("90-home"));
        Note("done");
        return 0;
    }

    /// <summary>Check first: step 1 right after a model came in, and the graph where the cut is seen and chosen.</summary>
    private static async Task<int> CheckModelAsync(ShellViewModel shell, ShellHost host, string outDir, string model, string prefix)
    {
        var log = new StringBuilder();
        void Note(string line)
        {
            log.AppendLine(line);
            File.WriteAllText(Path.Combine(outDir, prefix + ".txt"), log.ToString(), new UTF8Encoding(false));
        }
        string Shot(string name) => Path.Combine(outDir, $"{prefix}-{name}.png");

        shell.StartNewProject();
        var page = (ModelStepViewModel)shell.CurrentPage!;
        await page.ImportAsync(model);
        var project = shell.Project!;
        var check = project.Model?.Check;
        Note($"project   : {project.Id} · {project.Name} · preset {project.Preset} · staged {project.Model?.Staged} · original {project.Model?.Original}");
        Note($"check     : {check?.Outcome} ({check?.BlockedReason}) · {check?.ElapsedSec} s · layers {check?.Layers.Count}/{check?.TotalLayers} · ops {check?.Ops} · blocked {check?.BlockedOps} · error [{check?.ErrorTitle}]");
        Note($"proposal  : {check?.Proposal?.Kind} decoder {check?.Proposal?.Decoder} [{string.Join(", ", check?.Proposal?.Nodes ?? [])}]");
        Note($"cut       : by {project.Model?.Cut?.By} decoder {project.Model?.Cut?.Decoder} kept {project.Model?.Cut?.KeptOps} away {project.Model?.Cut?.AwayOps}");
        Note($"card      : chip [{page.CheckChipText}] state [{page.StateChipText}] strip {page.StripMode} [{page.StripRunText} | {page.StripCutText} | {page.StripTailText}] ticks {page.StripTicks.Count}");
        Note($"            tiles [{string.Join(" | ", page.Tiles.Select(t => $"{t.Label}: {t.Value} ({t.Note})"))}]");
        Note($"            message [{page.CheckMessageTitle}] next {page.CanGoNext} [{page.NextNote}] graph {page.CanOpenGraph} [{page.GraphButtonText}]");
        Note($"            guess [{page.GuessTitle}] [{page.GuessReason}] error [{page.Error}] notes [{string.Join(" | ", page.Notes)}]");
        Note($"            after [{string.Join(" | ", page.After.Select(f => f.Label + "=" + f.Value))}] bad [{string.Join(" | ", page.BadLayers.Select(b => b.Type + ": " + b.Reason))}]");
        Note($"            file [{page.FileName} · {page.FileSummary}] origin [{page.FileOrigin}] bad list shown {page.HasBadLayers}");
        Save(host, Shot("1-model"));
        if (!page.CanOpenGraph)
        {
            Note("no graph for this model");
            return 0;
        }

        // the graph as it opens
        page.OpenGraphCommand.Execute(null);
        var graph = (GraphCutViewModel)shell.CurrentPage!;
        await graph.Ready;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        void Describe(string label) =>
            Note($"{label,-10}: chip [{graph.ChipText}] {graph.KeptLabel} {graph.KeptText} · {graph.AwayLabel} {graph.AwayText} · bad [{graph.BadChipText}] · cuts {graph.Cuts.Count} " +
                 $"· verdict [{graph.VerdictTitle}] [{graph.VerdictNote}] · zone [{graph.ZoneTitle}] · apply {graph.CanApply} · suggest {graph.CanSuggest} [{graph.SuggestText}]");
        Note($"graph     : loaded {graph.HasGraph} [{graph.Message}] locked {graph.Locked} rows {graph.Layout?.Rows} boxes {graph.Layout?.Boxes.Count} size {graph.Layout?.Width:0}×{graph.Layout?.Height:0}");
        Describe("as opened");
        Save(host, Shot("2-graph"));
        if (!graph.HasGraph)
            return 0;

        // clicks on the drawing itself: the same hit test and commands as the mouse
        if (Find<Controls.GraphView>(host) is { } view && graph.Layout is { } placed)
        {
            Point Centre(Core.Projects.PlacedBox box) => new(box.CenterX, box.Y + box.Height / 2);
            var cutsBefore = graph.Cuts.Count;
            if (!graph.Locked && placed.Boxes.FirstOrDefault(b => b.Node is { State: "ok" }) is { } runnable)
            {
                view.ClickAt(Centre(runnable));
                var afterOne = graph.Cuts.Count;
                view.ClickAt(Centre(runnable));
                Note($"click     : on {runnable.Node!.Name}: cuts {cutsBefore} → {afterOne} → {graph.Cuts.Count}");
            }
            if (placed.Boxes.FirstOrDefault(b => b.Node is { State: "bad" }) is { } broken)
            {
                view.ClickAt(Centre(broken));
                Note($"click     : on {broken.Node!.Name}: note [{graph.PopTitle}] cuts {graph.Cuts.Count}");
                view.ClickAt(new Point(2, 2)); // empty space closes the note
                Note($"click     : on empty space: note [{graph.PopTitle}]");
            }
        }
        else
        {
            Note("click     : the drawing was not found");
        }

        // an operator that cannot run: why
        if (graph.Bads.FirstOrDefault() is { } firstBad)
        {
            graph.FocusCommand.Execute(firstBad);
            Note($"picked    : [{graph.PopTitle}] [{graph.PopName}] [{graph.PopBody}]");
            Save(host, Shot("3-graph-why"));
            graph.ClearPickCommand.Execute(null);
        }
        if (graph.Locked)
            return 0;

        // without any cut, then with the app's suggestion
        foreach (var row in graph.Cuts.ToList())
            graph.RemoveCutCommand.Execute(row);
        Describe("no cut");
        Save(host, Shot("4-graph-no-cut"));
        if (graph.SuggestCommand.CanExecute(null))
        {
            graph.SuggestCommand.Execute(null);
            Describe("suggested");
            Save(host, Shot("5-graph-suggested"));
            // one cut less: the rest still works, but no known box maths fits any more
            if (graph.Cuts.Count > 1)
            {
                graph.RemoveCutCommand.Execute(graph.Cuts[0]);
                Describe("one less");
                Save(host, Shot("6-graph-one-less"));
                graph.SuggestCommand.Execute(null);
            }
        }

        // "이대로 사용": the cut is made (or kept) and step 1 shows what is in the project now
        if (graph.CanApply)
        {
            await graph.ApplyCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var after = shell.CurrentPage as ModelStepViewModel;
            Note($"applied   : staged {project.Model?.Staged} · prepared {project.Model?.Prepared} · preset {project.Preset} · cut by {project.Model?.Cut?.By} decoder {project.Model?.Cut?.Decoder} " +
                 $"kept {project.Model?.Cut?.KeptOps} away {project.Model?.Cut?.AwayOps} · post {project.Options.Convert.PostProcess}");
            Note($"            card chip [{after?.CheckChipText}] tiles [{string.Join(" | ", after?.Tiles.Select(t => $"{t.Value} ({t.Note})") ?? [])}] next {after?.CanGoNext} error [{after?.Error}] notes [{string.Join(" | ", after?.Notes ?? [])}]");
            Save(host, Shot("7-model-after"));
        }
        Note("done");
        return 0;
    }

    /// <summary>The screens of a project that is already built, as they look when it is opened again.</summary>
    private static async Task<int> ReopenAsync(ShellViewModel shell, AppServices services, ShellHost host, string outDir, string projectId, string prefix)
    {
        var project = services.Projects.Load(projectId) ?? throw new FileNotFoundException("no such project in the capture workspace", projectId);
        string Shot(string name) => Path.Combine(outDir, $"{prefix}-{name}.png");
        shell.OpenProject(project);
        // a project that holds an exported YOLOv8 as it is: press "지금 준비하기" as the user would
        shell.NavigateTo("model");
        if (shell.CurrentPage is ModelStepViewModel { CanPrepare: true } unprepared)
        {
            Save(host, Shot("open-model-unprepared"));
            await unprepared.PrepareCommand.ExecuteAsync(null);
            File.WriteAllText(Path.Combine(outDir, prefix + "-prepare.txt"),
                $"prepared={project.Model?.Prepared} preset={project.Preset} staged={project.Model?.Staged} post={project.Options.Convert.PostProcess} " +
                $"source={project.Model?.Source} error=[{unprepared.Error}] notes=[{string.Join(" | ", unprepared.Notes)}]");
        }
        // a project from before the check existed: press "지금 점검하기" as the user would
        if (shell.CurrentPage is ModelStepViewModel { ShowRecheck: true } unchecked_ && project.Model?.Check is null)
        {
            Save(host, Shot("open-model-unchecked"));
            await unchecked_.RecheckCommand.ExecuteAsync(null);
            File.WriteAllText(Path.Combine(outDir, prefix + "-recheck.txt"),
                $"outcome={project.Model?.Check?.Outcome} layers={project.Model?.Check?.Layers.Count}/{project.Model?.Check?.TotalLayers} ops={project.Model?.Check?.Ops} " +
                $"chip=[{unchecked_.CheckChipText}] strip=[{unchecked_.StripRunText}] error=[{unchecked_.Error}] staged={project.Model?.Staged} preset={project.Preset}");
        }
        foreach (var step in shell.Steps)
        {
            shell.NavigateCommand.Execute(step);
            await Settle(shell);
            Save(host, Shot("open-" + step.Key));
            if (shell.CurrentPage is ResultViewModel { HasFp32: true } result)
            {
                result.IsBoth = true;
                Save(host, Shot("open-result-both"));
                result.IsFp32 = true;
                Save(host, Shot("open-result-fp32"));
                result.IsInt8 = true;
            }
        }
        shell.NavigateCommand.Execute(shell.HomeItem);
        Save(host, Shot("open-home"));
        return 0;
    }

    /// <summary>
    /// <c>--components</c> (design D12): the components page with the real installer, into the folder TOPST_COMPONENTS_ROOT
    /// names — the setup screen, the install while it runs, the end (done or stopped), and 설정 › 구성 요소. Writes
    /// <c>&lt;prefix&gt;.txt</c>. With TOPST_COMPONENTS_CATALOG pointing at a catalog with a wrong hash it shows the failure screen.
    /// </summary>
    private static async Task<int> ComponentsAsync(ShellViewModel shell, AppServices services, ShellHost host, string outDir, string prefix)
    {
        string Shot(string name) => Path.Combine(outDir, $"{prefix}-{name}.png");
        var c = shell.Components;
        var log = new StringBuilder();
        void Note(string line) => log.AppendLine($"{DateTime.Now:HH:mm:ss} {line}");
        Note($"root={c.Location} engine={(services.Engine is null ? "none" : services.EngineLayout!.RuntimeRoot)} problem=[{c.Problem}]");
        foreach (var row in c.Rows)
            Note($"row {row.Id}: source={row.Source} selected={row.IsSelected} download={row.DownloadText} contents=[{row.Contents}]");

        shell.NavigateTo("home");
        await Settle(shell);
        Note($"home page: {shell.CurrentPage?.GetType().Name} phase={c.Phase} title=[{c.SetupTitle}] total={c.DownloadTotal} disk={c.DiskAfter} during={c.DiskDuring} time={c.TimeText}");
        Save(host, Shot("1-setup"));
        Note($"install enabled before consent: {c.InstallCommand.CanExecute(null)}");
        c.Agreed = true;
        Note($"install enabled after consent: {c.InstallCommand.CanExecute(null)}");

        var install = c.InstallCommand.ExecuteAsync(null);
        var shotLines = new HashSet<string>();
        var shotDownload = false;
        while (!install.IsCompleted)
        {
            await Task.Delay(250);
            // a file that is really downloaded (not taken from components\downloads) shows a bar: "803 / 1,235 MB · 남은 시간 약 30초"
            if (!shotDownload && c.IsInstalling && c.Steps.FirstOrDefault(s => s.HasBar && s.Fraction is > 0.15 and < 0.9) is { } downloading)
            {
                shotDownload = true;
                Note($"downloading {c.CurrentTitle}: {downloading.Title} · {downloading.Detail} · {downloading.Right} · overall {c.OverallText}");
                Save(host, Shot("2-downloading"));
            }
            if (c.IsInstalling && c.CurrentLine >= 1 && shotLines.Add(c.CurrentTitle + c.CurrentLine))
            {
                Note($"installing {c.CurrentTitle} line {c.CurrentLine}: {c.Steps[c.CurrentLine].Title} · {c.Steps[c.CurrentLine].Detail} · overall {c.OverallText}");
                if (shotLines.Count == 1 || (c.CurrentTitle == c.Rows[0].Title && c.CurrentLine == 2))
                    Save(host, Shot("2-installing"));
            }
        }
        await install;
        await Settle(shell);
        Note($"end: phase={c.Phase} elapsed={c.ElapsedTotal} fail=[{c.FailTitle}] [{c.FailHeadline} {c.FailText}] detail=[{c.FailDetail}] engine={(services.Engine is null ? "none" : services.EngineLayout!.RuntimeRoot)}");
        foreach (var step in c.Steps)
            Note($"  step {step.State} {step.Title} · {step.Detail.Replace('\n', ' ')} · {step.Right}");
        Save(host, Shot(c.IsDone ? "3-done" : "3-failed"));

        shell.NavigateCommand.Execute(shell.SettingsItem);
        if (shell.CurrentPage is SettingsViewModel settings)
        {
            settings.ShowComponentsTab();
            await Settle(shell);
            foreach (var row in c.Rows)
                Note($"settings row {row.Id}: {row.StateText} {row.InstalledOn} [{row.Contents}] {row.SizeText} reinstall={row.CanReinstall} remove={row.CanRemove} install={row.CanInstall}");
            Note($"kept files {c.KeptText}");
            Save(host, Shot("4-settings"));
        }
        shell.NavigateTo("home");
        await Settle(shell);
        Note($"home afterwards: {shell.CurrentPage?.GetType().Name} caption=[{shell.EngineCaption}]");
        Save(host, Shot("5-home"));
        File.WriteAllText(Path.Combine(outDir, prefix + ".txt"), log.ToString());
        return c.IsDone ? 0 : 2;
    }

    /// <summary>
    /// The model zoo (design §5.9): the two lists, a search, a model that asks for consent, then one model prepared for
    /// real and built. Without downloads when TOPST_ZOO_SITE names the review's packages and TOPST_ZOO_RANDOM_WEIGHTS=1.
    /// </summary>
    private static async Task<int> ZooAsync(ShellViewModel shell, AppServices services, ShellHost host, string outDir, string modelId, string prefix)
    {
        string Shot(string name) => Path.Combine(outDir, $"{prefix}-{name}.png");
        var log = new StringBuilder();
        void Note(string line) => log.AppendLine($"{DateTime.Now:HH:mm:ss} {line}");
        shell.Home.Refresh();
        Save(host, Shot("0-home"));
        Note($"home: zoo card [{shell.Home.ZooCounts}]");

        shell.NavigateTo("zoo");
        await Settle(shell);
        if (shell.CurrentPage is not ZooViewModel zoo)
            throw new InvalidOperationException("the zoo page did not open");
        Note($"zoo: problem=[{zoo.Problem}] class={zoo.ClassCount} obj={zoo.ObjectCount} visible={zoo.Visible.Count} selected={zoo.Selected?.Id} " +
             $"consent={zoo.NeedsConsent} [{zoo.NeedTitle} · {zoo.NeedNote}] create={zoo.CreateAndBuildCommand.CanExecute(null)}");
        Save(host, Shot("1-obj"));
        zoo.IsClassTab = true;
        await Settle(shell);
        Note($"class tab: visible={zoo.Visible.Count} selected={zoo.Selected?.Id} filters={string.Join(", ", zoo.Filters.Select(f => f.Label + " " + f.Count))}");
        Save(host, Shot("2-class"));
        zoo.Search = "efficientnet";
        await Settle(shell);
        Note($"search efficientnet: {string.Join(", ", zoo.Visible.Take(8).Select(r => r.Display + " " + r.SizeText + " " + r.SubText))}");
        Save(host, Shot("3-search"));
        zoo.Search = "";

        var target = zoo.Visible.FirstOrDefault(r => r.Id == modelId);
        if (target is null)
        {
            zoo.IsObjectTab = true;
            await Settle(shell);
            target = zoo.Visible.FirstOrDefault(r => r.Id == modelId) ?? throw new InvalidOperationException(modelId + " is not in the catalog");
        }
        zoo.SelectCommand.Execute(target);
        await Settle(shell);
        Note($"chosen {target.Id}: [{zoo.Title}] [{zoo.About}] ms=[{zoo.MsText}] input=[{zoo.InputText}] weights=[{zoo.WeightsText}] fixes=[{zoo.FixText}] " +
             $"consent={zoo.NeedsConsent} [{zoo.NeedTitle}] before agree={zoo.CreateAndBuildCommand.CanExecute(null)}");
        Save(host, Shot("4-chosen"));
        zoo.Agreed = true;
        Note($"after agree={zoo.CreateAndBuildCommand.CanExecute(null)}");
        // a capture downloads nothing: the preparation runs with random weights, or with real ones downloaded before
        var random = Environment.GetEnvironmentVariable(ZooViewModel.RandomWeightsVariable) == "1";
        var downloaded = !random && await DownloadedZooWeightsAsync(services, target.Model) is { } file ? file : null;
        Note($"weights: {(random ? "random" : downloaded ?? "not downloaded")}");
        if (zoo.NeedsComponent || (!random && downloaded is null))
        {
            Note("no preparation: the zoo component and weights downloaded before (or TOPST_ZOO_SITE + TOPST_ZOO_RANDOM_WEIGHTS=1) are needed for it");
            File.WriteAllText(Path.Combine(outDir, prefix + ".txt"), log.ToString());
            return 0;
        }

        var create = zoo.CreateAndBuildCommand.ExecuteAsync(null);
        var shotPreparing = false;
        while (!create.IsCompleted)
        {
            await Task.Delay(200);
            if (!shotPreparing && zoo.IsPreparing && zoo.Steps.Any(s => s.State == StepState.Running && s.Plan.Keys.Contains("export")))
            {
                shotPreparing = true;
                Note($"preparing: {string.Join(" | ", zoo.Steps.Select(s => $"{s.State} {s.Title} · {s.Detail} · {s.Right}"))} · overall {zoo.OverallText}");
                Save(host, Shot("5-preparing"));
            }
        }
        await create;
        await Settle(shell);
        Note($"after: phase={zoo.Phase} fail=[{zoo.FailTitle} · {zoo.FailDetail} · {zoo.FailEvidence}] page={shell.CurrentPage?.GetType().Name} project={shell.Project?.Name} " +
             $"zoo origin={shell.Project?.Zoo?.Id} mean={string.Join("/", shell.Project?.Options.Convert.Mean ?? [])}");
        if (zoo.IsFailed)
        {
            Save(host, Shot("5-failed"));
            File.WriteAllText(Path.Combine(outDir, prefix + ".txt"), log.ToString());
            return 2;
        }
        // the build the button started
        while (shell.IsBuilding || shell.IsBusy)
            await Task.Delay(500);
        await Settle(shell);
        Note($"build: page={shell.CurrentPage?.GetType().Name} built={shell.Project?.Headline.IsBuilt} ms={shell.Project?.Headline.InferenceTimeMs}");
        Save(host, Shot("6-built"));
        shell.NavigateTo("result");
        await Settle(shell);
        Save(host, Shot("6-result"));
        // what a zoo project looks like on its own steps: the model as the zoo names it, the cut, no files to look after
        shell.NavigateTo("model");
        await Settle(shell);
        Save(host, Shot("6-model"));
        shell.NavigateTo("config");
        await Settle(shell);
        Save(host, Shot("6-config"));
        shell.NavigateCommand.Execute(shell.SettingsItem);
        if (shell.CurrentPage is SettingsViewModel settings)
        {
            settings.ShowComponentsTab();
            await Settle(shell);
            foreach (var row in shell.Components.Rows)
                Note($"settings row {row.Id}: {row.StateText} [{row.Contents}] {row.SizeText} install={row.CanInstall}");
            Save(host, Shot("7-settings"));
        }
        File.WriteAllText(Path.Combine(outDir, prefix + ".txt"), log.ToString());
        return shell.Project?.Headline.IsBuilt == true ? 0 : 3;
    }

    /// <summary>The model's weights when they are in the components folder already (checked), else null.</summary>
    private static async Task<string?> DownloadedZooWeightsAsync(AppServices services, TopstToolkit.Core.Zoo.ZooModel model)
    {
        if (services.Engine is not { } engine || TopstToolkit.Core.Zoo.ZooCatalog.Locate() is not { } catalog
            || TopstToolkit.Core.Components.ComponentCatalog.Find() is not { } components || services.ComponentsLocation is not { } root)
            return null;
        var preparer = new TopstToolkit.Core.Zoo.ZooPreparer(catalog, new TopstToolkit.Core.Components.ComponentInstaller(components, root), engine, services.Projects);
        return await preparer.DownloadedWeightsAsync(model);
    }

    /// <summary>
    /// <c>--project &lt;id&gt; --board &lt;address&gt; [--board-video &lt;file&gt;]</c> (design §5.10): the 보드 page with the real
    /// board — connect (the scan runs as on screen), status, the project's model on the board with its check pictures and
    /// the toolkit's sample pictures, a video, the board's models and a speed test, then the header pill on another page.
    /// Uses the board's NPU (allowed for the app's own runner). Writes <c>&lt;prefix&gt;.txt</c>.
    /// </summary>
    private static async Task<int> BoardAsync(ShellViewModel shell, AppServices services, ShellHost host, string outDir, string projectId,
        string boardHost, string? video, string prefix)
    {
        string Shot(string name) => Path.Combine(outDir, $"{prefix}-{name}.png");
        var log = new List<string>();
        var project = services.Projects.Load(projectId) ?? throw new FileNotFoundException("no such project in the capture workspace", projectId);
        shell.OpenProject(project);
        shell.NavigateTo("board");
        var page = (BoardViewModel)shell.CurrentPage!;
        await WaitUntil(() => !page.IsScanning, TimeSpan.FromSeconds(30));
        log.Add($"scan: [{string.Join(", ", page.Found.Select(f => $"{f.Title} {f.Caption}"))}] text=[{page.ScanText}] host=[{page.Host}]");
        if (string.IsNullOrWhiteSpace(page.Host))
            page.Host = boardHost;
        Save(host, Shot("1-connect"));

        await page.ConnectCommand.ExecuteAsync(null);
        log.Add($"connect: connected={page.IsConnected} error=[{page.ConnectError}] help=[{page.ConnectHelp}] runner={shell.Board.Agent?.RunnerVersion}");
        if (!page.IsConnected)
        {
            Save(host, Shot("1-connect-failed"));
            File.WriteAllLines(Path.Combine(outDir, prefix + ".txt"), log);
            return 1;
        }
        await Task.Delay(TimeSpan.FromSeconds(4.5)); // two status lines, so the CPU numbers are measured ones
        Save(host, Shot("2-status"));
        log.Add($"status: cpu={page.CpuValue}% npu={page.NpuValue}% mem={page.MemoryValue}% temp={page.TemperatureValue} cores=[{string.Join(" ", page.Cores.Select(c => c.Value))}] " +
                $"npus=[{string.Join(" ", page.Npus.Select(n => n.Value))}] memory=[{string.Join(" | ", page.Memory.Select(m => m.Name + " " + m.Value))}] " +
                $"temps=[{string.Join(" ", page.Temperatures.Select(t => t.Name + " " + t.Value))}] facts=[{page.NameText} | {page.OsText} | {page.DiskText} | {page.UptimeText} | {page.NetworkText}]");

        page.IsRunTab = true;
        await Settle(shell);
        Save(host, Shot("3-run-empty"));
        var pictures = services.Data.TestImages(project).ToList();
        if (services.EngineLayout is { } layout && Directory.Exists(layout.DefaultCalibrationDir))
            pictures.AddRange(Directory.GetFiles(layout.DefaultCalibrationDir).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Order().Take(4));
        await page.Run.RunPhotosAsync(pictures);
        await page.Run.Ready;
        await Settle(shell);
        Save(host, Shot("4-run-photos"));
        log.Add($"photos: error=[{page.Run.Error}] average={page.Run.AverageText} pc=[{page.Run.PcEstimateText}] target=[{page.Run.TargetTitle} · {page.Run.TargetCaption}] " +
                $"checks=[{string.Join(" | ", page.Run.Checks.Select(c => $"{c.Text} ({c.Detail})"))}] " +
                $"badges=[{string.Join(" ", page.Run.Photos.Select(p => p.Name + ":" + p.Badge))}] compare={page.Run.CompareChip} " +
                $"[{string.Join(" | ", page.Run.CompareRows.Select(r => $"{r.Name} {r.Board} {r.Pc}"))}]");
        if (page.Run.Photos.Count > 1)
        {
            page.Run.SelectPhotoCommand.Execute(page.Run.Photos[^1]);
            await page.Run.Ready;
            await Settle(shell);
            Save(host, Shot("4b-run-photo-other"));
        }
        // "결과 저장" into a folder next to the shots, as if the user picked it
        var saveDir = Directory.CreateDirectory(Path.Combine(outDir, prefix + "-saved")).FullName;
        if (services.Dialogs is SilentDialogs silent)
            silent.Folder = saveDir;
        await page.Run.SavePhotosCommand.ExecuteAsync(null);
        log.Add($"saved photos: [{page.Run.PhotoSaveText}]");

        if (video is not null)
        {
            page.Run.IsVideoMode = true;
            await page.Run.OpenVideoAsync(video);
            await Settle(shell);
            Save(host, Shot("5-video-ready"));
            var running = page.Run.RunVideoCommand.ExecuteAsync(null);
            await WaitUntil(() => running.IsCompleted || (page.Run.TotalFrames > 0 && page.Run.DoneFrames >= page.Run.TotalFrames / 2), TimeSpan.FromMinutes(3));
            Save(host, Shot("6-video-running"));
            log.Add($"video mid: {page.Run.ProgressText} speed=[{page.Run.SpeedText}] npu=[{page.Run.NpuAverageText}] remain=[{page.Run.RemainText}] temp=[{page.Run.TemperatureText}] hud=[{page.Run.HudFrame} {page.Run.HudVideoTime} {page.Run.HudCount}]");
            await running;
            await Settle(shell);
            Save(host, Shot("7-video-done"));
            log.Add($"video done: error=[{page.Run.Error}] {page.Run.ProgressText} frames={page.Run.FrameMax + 1} speed=[{page.Run.SpeedText}] npu=[{page.Run.NpuAverageText}] caption=[{page.Run.VideoCaption}]");
            page.Run.FrameIndex = page.Run.FrameMax / 3;   // the scrubber after the run
            await Settle(shell);
            Save(host, Shot("7b-video-scrub"));
            await page.Run.SaveVideoResultCommand.ExecuteAsync(null);
            log.Add($"saved video: [{page.Run.VideoSaveText}] error=[{page.Run.Error}]");
            page.Run.IsPhotoMode = true;
        }

        page.IsManageTab = true;
        await page.Manage.Ready;
        await Settle(shell);
        Save(host, Shot("8-manage"));
        await page.Manage.BenchCommand.ExecuteAsync(null);
        page.Manage.SoakOverride = TimeSpan.FromSeconds(12);
        var soak = page.Manage.StartSoakCommand.ExecuteAsync(null);
        await WaitUntil(() => soak.IsCompleted || page.Manage.SoakFraction > 0.3, TimeSpan.FromSeconds(40));
        Save(host, Shot("9-manage-soak"));
        await soak;
        await Settle(shell);
        Save(host, Shot("10-manage-done"));
        log.Add($"manage: models=[{string.Join(" | ", page.Manage.Models.Select(m => $"{m.Title} · {m.Caption}"))}] bench=[{string.Join(" | ", page.Manage.BenchRows.Select(r => r.Label + " " + r.Value))}] " +
                $"soak=[{page.Manage.SoakResult}] error=[{page.Manage.Error}]");

        // one of the board's own models, from the list
        if (page.Manage.Models.FirstOrDefault(m => !m.IsApp && m.Info.Type == "obj") is { } own)
        {
            page.Manage.RunModelCommand.Execute(own);
            await page.Run.RunPhotosAsync([services.EngineLayout!.SampleImage]);
            await page.Run.Ready;
            await Settle(shell);
            Save(host, Shot("10b-board-model"));
            log.Add($"board model: {page.Run.TargetTitle} · {page.Run.TargetCaption} error=[{page.Run.Error}] badges=[{string.Join(" ", page.Run.Photos.Select(p => p.Name + ":" + p.Badge))}] " +
                    $"boxes=[{string.Join(" | ", page.Run.Boxes.Select(b => b.Label))}]");
            page.Run.UseProjectModelCommand.Execute(null);
        }

        // a model the app put on the board from another workspace (its project is not here): the names come from its manifest
        await page.Manage.RefreshAsync();
        if (page.Manage.Models.FirstOrDefault(m => m.IsApp && m.Info.Manifest?.Project != project.Id) is { } other)
        {
            page.Manage.RunModelCommand.Execute(other);
            await page.Run.RunPhotosAsync([services.EngineLayout!.SampleImage]);
            await page.Run.Ready;
            await Settle(shell);
            Save(host, Shot("10c-other-model"));
            log.Add($"other app model: {page.Run.TargetTitle} · {page.Run.TargetCaption} labels={other.Info.Manifest?.Labels?.Count} error=[{page.Run.Error}] " +
                    $"boxes=[{string.Join(" | ", page.Run.Boxes.Select(b => b.Label))}]");
            if (video is not null)
            {
                page.Run.IsVideoMode = true;
                await page.Run.OpenVideoAsync(video);
                await page.Run.RunVideoCommand.ExecuteAsync(null);
                await Settle(shell);
                Save(host, Shot("10d-other-model-video"));
                log.Add($"other app model video: error=[{page.Run.Error}] {page.Run.ProgressText} caption=[{page.Run.VideoCaption}] speed=[{page.Run.SpeedText}] " +
                        $"hud=[{page.Run.HudFrame} {page.Run.HudVideoTime} {page.Run.HudCount}] boxes=[{string.Join(" | ", page.Run.VideoBoxes.Select(b => b.Label))}]");
                page.Run.IsPhotoMode = true;
            }
            page.Run.UseProjectModelCommand.Execute(null);
        }

        shell.NavigateTo("result");
        await Settle(shell);
        Save(host, Shot("11-pill"));
        await shell.Board.DisconnectAsync();
        shell.NavigateTo("board");
        await Settle(shell);
        Save(host, Shot("12-disconnected"));
        shell.FlushLog();
        log.AddRange(shell.Log.Select(l => "log: " + l.Text));
        File.WriteAllLines(Path.Combine(outDir, prefix + ".txt"), log);
        return 0;
    }

    private static async Task WaitUntil(Func<bool> done, TimeSpan limit)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.Elapsed < limit)
            await Task.Delay(100);
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
                return found;
            if (Find<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    /// <summary>Waits for what a page loads in the background (thumbnails, results) and for queued UI work.</summary>
    /// <summary>
    /// <c>--update demo</c>: every state of the update pill and card (docs/mockups/mockup-update.html) with a made-up
    /// release 0.2.0, no network.
    /// </summary>
    private static async Task<int> UpdateDemoAsync(ShellViewModel shell, AppServices services, ShellHost host, string outDir, string prefix)
    {
        string Shot(string name) => Path.Combine(outDir, $"{prefix}-{name}.png");
        var manifest = new TopstToolkit.Core.Updates.UpdateManifest
        {
            Product = TopstToolkit.Core.Updates.UpdateManifest.ProductName,
            Version = "0.2.0",
            Released = "2026-12-01",
            Installer = new TopstToolkit.Core.Updates.UpdateInstaller { Url = "https://example.invalid/TOPST-Toolkit-Setup-0.2.0.exe", Size = 59_037_652, Sha256 = new string('0', 64) },
            Notes = ["보드 화면에서 영상 실행을 멈췄다가 이어서 볼 수 있어요", "모델 모음에 YOLO11 모델 12개를 더했어요", "결과 화면이 더 빨리 열려요"],
        };
        services.Settings.UpdateCheckedAt = DateTimeOffset.Now.Date.AddHours(14).AddMinutes(2);
        UpdateViewModel Fresh(TopstToolkit.Core.Updates.SetupRecord? setup)
        {
            shell.Update = new UpdateViewModel(services, shell, null, setup, closeApp: () => { });
            return shell.Update;
        }
        var setup = new TopstToolkit.Core.Updates.SetupRecord(true, "0.1.0");

        Fresh(setup).ShowForCapture(UpdateStage.UpToDate, manifest);
        shell.OpenUpdateCommand.Execute(null);
        await Settle(shell);
        Save(host, Shot("1-up-to-date"));

        Fresh(setup).ShowForCapture(UpdateStage.Available, manifest);
        shell.NavigateTo("home");
        await Settle(shell);
        Save(host, Shot("2-found-home"));
        shell.OpenUpdateCommand.Execute(null);
        await Settle(shell);
        Save(host, Shot("3-offer"));

        Fresh(setup).ShowForCapture(UpdateStage.Downloading, manifest, progress: 0.42);
        await Settle(shell);
        Save(host, Shot("4-downloading"));
        Fresh(setup).ShowForCapture(UpdateStage.Ready, manifest);
        await Settle(shell);
        Save(host, Shot("5-ready"));
        Fresh(setup).ShowForCapture(UpdateStage.Updated, manifest, took: TimeSpan.FromSeconds(72));
        await Settle(shell);
        Save(host, Shot("6-updated"));
        Fresh(setup).ShowForCapture(UpdateStage.Failed, manifest, TopstToolkit.Core.Updates.UpdateProblem.Mismatch);
        await Settle(shell);
        Save(host, Shot("7-mismatch"));
        Fresh(setup).ShowForCapture(UpdateStage.Failed, manifest, installFailed: true);
        await Settle(shell);
        Save(host, Shot("8-install-failed"));
        Fresh(null).ShowForCapture(UpdateStage.Available, manifest with { Page = "https://example.invalid/topst-toolkit" });
        await Settle(shell);
        Save(host, Shot("9-not-installed"));
        Fresh(new TopstToolkit.Core.Updates.SetupRecord(false, "0.1.0")).ShowForCapture(UpdateStage.Ready, manifest);
        await Settle(shell);
        Save(host, Shot("10-ready-just-me"));

        // the patch (only the files that changed, design §7.5 "차등 패치"), and the installer after a patch was undone
        Fresh(setup).ShowForCapture(UpdateStage.Available, manifest, asPatch: true);
        await Settle(shell);
        Save(host, Shot("11-patch-offer"));
        Fresh(setup).ShowForCapture(UpdateStage.Downloading, manifest, progress: 0.42, asPatch: true);
        await Settle(shell);
        Save(host, Shot("12-patch-downloading"));
        Fresh(setup).ShowForCapture(UpdateStage.Ready, manifest, asPatch: true);
        await Settle(shell);
        Save(host, Shot("13-patch-ready"));
        Fresh(setup).ShowForCapture(UpdateStage.Starting, manifest, asPatch: true);
        await Settle(shell);
        Save(host, Shot("14-patch-applying"));
        Fresh(setup).ShowForCapture(UpdateStage.Available, manifest, patchUndone: true);
        await Settle(shell);
        Save(host, Shot("15-patch-undone"));
        Fresh(setup).ShowForCapture(UpdateStage.Failed, manifest, TopstToolkit.Core.Updates.UpdateProblem.SetupNotStarted, asPatch: true);
        await Settle(shell);
        Save(host, Shot("16-patch-declined"));
        return 0;
    }

    /// <summary>
    /// <c>--update e2e</c>: the real thing, from an app Setup installed (a test install): looks at <c>TOPST_UPDATE_URL</c>,
    /// downloads the installer, hands over to Setup and ends, as the window would close. Setup installs and starts the app
    /// again (on the same desktop: run this on a desktop nobody sees). Writes <c>&lt;prefix&gt;.txt</c> with what happened.
    /// With a patch on offer the patch is downloaded and swapped instead, and the app starts itself again; a patch that is
    /// undone goes on with the installer.
    /// </summary>
    private static async Task<int> UpdateEndToEndAsync(string outDir, string prefix)
    {
        var log = new StringBuilder();
        void Note(string line)
        {
            log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
            File.WriteAllText(Path.Combine(outDir, prefix + ".txt"), log.ToString(), new UTF8Encoding(false));
        }
        string Shot(string name) => Path.Combine(outDir, $"{prefix}-{name}.png");

        var services = AppServices.Create(); // TOPST_SETTINGS: the test's settings and workspace, the ones the app started again finds
        services.Dialogs = new SilentDialogs();
        var shell = new ShellViewModel(services);
        var host = new ShellHost { DataContext = shell, Width = Width, Height = Height };
        var closed = false;
        shell.Update = new UpdateViewModel(services, shell, TopstToolkit.Core.Updates.UpdateSource.Find(), TopstToolkit.Core.Updates.SetupRecord.Read(),
            closeApp: () => closed = true);
        var update = shell.Update;
        Note($"app {AppServices.AppVersion} at {AppContext.BaseDirectory} · setup record {TopstToolkit.Core.Updates.SetupRecord.Read()} · updates folder {update.Folder}");

        await update.CheckNowCommand.ExecuteAsync(null);
        Note($"check: stage {update.Stage} · {update.Headline} · {update.Lead}");
        shell.OpenUpdateCommand.Execute(null);
        await Settle(shell);
        Save(host, Shot("1-check"));
        if (update.Stage == UpdateStage.Available)
        {
            var mid = false;
            void OnProgress(object? sender, PropertyChangedEventArgs e)
            {
                if (!mid && e.PropertyName == nameof(UpdateViewModel.Progress) && update.Progress is > 0.3 and < 0.9)
                {
                    mid = true;
                    Save(host, Shot("2-downloading"));
                }
            }
            update.PropertyChanged += OnProgress;
            await update.DownloadAsync();
            update.PropertyChanged -= OnProgress;
            Note($"download: stage {update.Stage} · {update.Headline} · {update.Lead} · {update.ProgressText}");
            await Settle(shell);
            Save(host, Shot("3-downloaded"));
        }
        if (update.Stage != UpdateStage.Ready)
        {
            Note("stopped: nothing to install");
            return 1;
        }
        await update.InstallAsync();
        Note($"install: closed={closed} stage {update.Stage} · {update.Headline} · {update.Lead}");
        if (!closed && !update.IsPatch && update.Stage is UpdateStage.Available or UpdateStage.Ready)
        {
            // the patch was undone (design §7.5 "차등 패치"): on with the installer, as the user would
            Note($"fallback: stage {update.Stage} · {update.PrimaryHelp}");
            await Settle(shell);
            Save(host, Shot("4-patch-undone"));
            if (update.Stage == UpdateStage.Available)
            {
                await update.DownloadAsync();
                Note($"download: stage {update.Stage} · {update.Headline} · {update.ProgressText}");
            }
            if (update.Stage == UpdateStage.Ready)
            {
                await update.InstallAsync();
                Note($"install: closed={closed} stage {update.Stage} · {update.Headline} · {update.Lead}");
            }
        }
        return closed ? 0 : 1;
    }

    private static async Task Settle(ShellViewModel shell)
    {
        for (var i = 0; i < 3; i++)
        {
            switch (shell.CurrentPage)
            {
                case DataViewModel data: await data.Ready; break;
                case ResultViewModel result: await result.Ready; break;
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        shell.FlushLog();
    }

    private static void Save(FrameworkElement element, string path, double? scale = null)
    {
        element.Measure(new Size(Width, Height));
        element.Arrange(new Rect(0, 0, Width, Height));
        element.UpdateLayout();
        // let bindings, templates and item containers settle before drawing
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();

        var k = scale ?? StillScale;
        var bitmap = new RenderTargetBitmap((int)Math.Round(Width * k), (int)Math.Round(Height * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>The three projects shown in docs\mockups: a built YOLOv5, an SSD that needs a rebuild, a built classifier.</summary>
    private static void SeedDemoProjects(ProjectStore store)
    {
        var now = DateTimeOffset.Now;

        var flowers = store.Create("꽃 분류 실습", AppServices.AppVersion, "0.9.9", now.AddDays(-2));
        PresetCatalog.Find("classification-imagenet")!.ApplyTo(flowers);
        flowers.Model = new ProjectModel { Source = @"C:\Users\학생\Downloads\mobilenetv2-10.onnx", Staged = "inputs/model/mobilenetv2-10.onnx", Format = "onnx", SizeBytes = 13_963_115 };
        flowers.Headline = new ProjectHeadline { InferenceTimeMs = 1.197, CompatibleLayers = 66, TotalLayers = 66, WeightBytes = 3_555_840, BuiltAt = now.AddDays(-2), ExportedAt = now.AddDays(-2) };
        store.Save(flowers, now.AddDays(-2));

        var parking = store.Create("주차장 차량 검출", AppServices.AppVersion, "0.9.9", now.AddDays(-1));
        PresetCatalog.Find("ssd-anchor")!.ApplyTo(parking);
        parking.Model = new ProjectModel { Source = @"D:\모델\mb2-ssd-lite.onnx", Staged = "inputs/model/mb2-ssd-lite.onnx", Format = "onnx", SizeBytes = 13_563_314 };
        parking.Headline = new ProjectHeadline { InferenceTimeMs = 2.014, CompatibleLayers = 123, TotalLayers = 123, BuiltAt = now.AddDays(-1), NeedsRebuild = true };
        store.Save(parking, now.AddDays(-1));

        var yolo = store.Create("yolov5m 데모", AppServices.AppVersion, "0.9.9", now);
        PresetCatalog.Find("pytorch-yolo-v5")!.ApplyTo(yolo);
        yolo.Model = new ProjectModel { Source = @"E:\TOPST\yolov5m_extracted.onnx", Staged = "inputs/model/yolov5m_extracted.onnx", Format = "onnx", SizeBytes = 84_762_489 };
        yolo.Headline = new ProjectHeadline { InferenceTimeMs = 30.017, CompatibleLayers = 273, TotalLayers = 273, WeightBytes = 21_300_000, BuiltAt = now };
        store.Save(yolo, now);
    }
}
