using System.Diagnostics;
using System.Text;
using TopstToolkit.Cli;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine;

// Console harness for the engine (M0): runs the verified sample flows end to end through Engine + Core.
//
//   TopstToolkit.Cli check                 engine self-check (engine_check.py)
//   TopstToolkit.Cli run [all|families|everything|<name>[,<name>...]]
//                        [--workspace <ascii dir>] [--runtime <dir>] [--helpers <dir>] [--models <dir>] [--log <file>]
//
// --log appends every line to a file as it is produced, so the results of finished recipes survive
// when the console goes away (2026-11-20: the PC went to sleep mid-run and the launching shell was ended).
// Exit code 0 only when every step succeeded and every expectation held.

Console.OutputEncoding = Encoding.UTF8;
var options = ParseOptions(args);
var command = options.Positional.ElementAtOrDefault(0) ?? "run";

StreamWriter? logFile = null;
if (options.Get("--log") is { } logPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
    logFile = new StreamWriter(logPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
    logFile.WriteLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} TopstToolkit.Cli {string.Join(' ', args)}");
}

void Out(string line)
{
    logFile?.WriteLine(line);
    try { Console.WriteLine(line); }
    catch (IOException) { /* console gone: keep running, the log file has it */ }
}

if (command == "update")
{
    // app updates, the publisher's side (design §7.5): update key | update manifest | update check
    var code = await UpdateCommand.RunAsync(options.Positional.Skip(1).ToList(), options.Get, Out);
    logFile?.Dispose();
    return code;
}

if (command == "install")
{
    // TopstToolkit.Cli install [engine|board-tools|all] [--root <components folder>] [--lookup <folder>] [--pip-cache <folder>]
    // Installs what the app installs on a user's PC (design D12), from the official sources in engine\components\components.json.
    // --lookup: a folder searched for already downloaded files (same name and SHA-256) before downloading (dev: artifacts\cache).
    var helpersDir = options.Get("--helpers") ?? EngineLayout.FindHelpersDir() ?? Path.Combine(FindRepoRoot() ?? ".", "engine", "helpers");
    var catalog = TopstToolkit.Core.Components.ComponentCatalog.Load(TopstToolkit.Core.Components.ComponentCatalog.DirectoryFor(helpersDir));
    if ((options.Get("--root") ?? TopstToolkit.Core.Components.ComponentsRoot.DefaultLocation(configured: TopstToolkit.Core.Settings.AppSettings.Load().ComponentsRoot)) is not { } componentsRoot)
    {
        Out("No usable components location. Pass --root <ASCII folder>.");
        return 2;
    }
    var installer = new TopstToolkit.Core.Components.ComponentInstaller(catalog, componentsRoot,
        options.Get("--lookup") is { } lookup ? [lookup] : [], options.Get("--pip-cache"));
    // all their downloads at once, then the engine and the board tools side by side (as Setup does); --one-by-one 1 as the app's screen
    var requested = options.Positional.ElementAtOrDefault(1) ?? TopstToolkit.Core.Components.ComponentCatalog.Engine;
    var ids = requested == "all" ? catalog.Components.Select(c => c.Id).ToList() : [requested];
    using var installCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; installCts.Cancel(); };
    foreach (var id in ids)
        Out($"설치: {catalog.Get(id).Title} ({id}) → {installer.FolderOf(id)} · 받을 양 약 {catalog.Get(id).DownloadBytes / 1048576.0:N0} MB");
    var told = new Dictionary<(string, string), (string Step, int Tenth)>();
    var progress = new SyncProgress<TopstToolkit.Core.Components.ComponentProgress>(p =>
    {
        var tenth = p.Fraction is { } f ? (int)(f * 10) : -1;
        lock (told)
        {
            if (told.TryGetValue((p.Component, p.Key), out var last) && (last.Step == p.Step && tenth <= last.Tenth))
                return;
            told[(p.Component, p.Key)] = (p.Step, tenth);
        }
        Out($"  [{DateTime.Now:HH:mm:ss}] {p.Component}{(p.Ahead ? " (미리 받기)" : "")}: {p.Step}" +
            (p.Fraction is { } g ? $" {g:P0} ({p.Done / 1048576.0:N0} / {p.Total / 1048576.0:N0} MB)" : ""));
    });
    var results = await TopstToolkit.Core.Components.ComponentBatch.RunAsync(installer, ids, together: options.Get("--one-by-one") is null,
        id => Out($"  [{DateTime.Now:HH:mm:ss}] {catalog.Get(id).Title} 설치 시작"), progress, installCts.Token);
    foreach (var installed in results)
        Out(installed.Success
            ? $"{catalog.Get(installed.Component).Title}: 완료 · {installed.Elapsed.TotalSeconds:0} s · {installed.Smoke}"
            : $"{catalog.Get(installed.Component).Title}: {(installed.Cancelled ? "취소됨" : "실패")}: {installed.Error} · 기록 {installed.LogDirectory}");
    logFile?.Dispose();
    return results.Count == ids.Count && results.All(r => r.Success) ? 0 : 1;
}

var layout = options.Get("--runtime") is { } runtimeDir
    ? new EngineLayout(runtimeDir, options.Get("--helpers") ?? Path.Combine(FindRepoRoot() ?? ".", "engine", "helpers"))
    : TopstToolkit.Core.Components.ComponentsRoot.FindEngine(configured: TopstToolkit.Core.Settings.AppSettings.Load().ComponentsRoot); // as the app does; TOPST_COMPONENTS_ROOT picks an installed engine
if (layout is null)
{
    Out("Engine runtime not found. Build it with build\build-runtime.ps1 or pass --runtime <dir>.");
    return 2;
}

var workspaceDir = options.Get("--workspace")
                   ?? (FindRepoRoot() is { } repo ? Path.Combine(repo, "artifacts", "workspace") : WorkspaceRoot.DefaultLocation());
if (workspaceDir is null)
{
    Out("No usable workspace location. Pass --workspace <ASCII folder>.");
    return 2;
}

if (command == "board")
    return await BoardCommand.RunAsync(options.Positional.Skip(1).ToList(), options.Get, layout, workspaceDir, Out);

var session = new EngineSession(layout, new WorkspaceRoot(workspaceDir));
Out($"runtime   : {layout.RuntimeRoot}");
Out($"helpers   : {layout.HelpersDir}");
Out($"workspace : {session.Workspace.Root}");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var checkProject = session.Workspace.OpenProject("engine-check");
var check = await session.For(checkProject).EngineCheckAsync(cts.Token);
if (check.Value is not { } report)
{
    Out($"engine_check wrote no report (exit {check.Run.ExitCode}). See {check.Run.StderrLogPath}");
    return 1;
}
Out($"engine    : SDK {report.SdkVersion} · Python {report.Python} · torch {report.Packages?.GetValueOrDefault("torch")} · " +
    $"import {report.ImportSeconds:0.0}s · {(report.Ok ? "OK" : "문제 있음")}");
foreach (var problem in report.Problems)
    Out($"   ! {problem}");
if (command == "check")
    return report.Ok ? 0 : 1;
if (!report.Ok)
    return 1;

if (command == "netso")
{
    // TopstToolkit.Cli netso <compiler output dir>...   builds net.so for compiled networks (needs the Arm toolchain)
    var failed = 0;
    foreach (var codeDir in options.Positional.Skip(1).Select(Path.GetFullPath))
    {
        // logs and job folders go to the owning project when the folder is <workspace>\projects\<id>\out\code\<net>
        var parts = codeDir.Split(Path.DirectorySeparatorChar);
        var at = Array.LastIndexOf(parts, "projects");
        var owner = at >= 0 && at + 1 < parts.Length && codeDir.StartsWith(session.Workspace.ProjectsDir, StringComparison.OrdinalIgnoreCase)
            ? session.Workspace.OpenProject(parts[at + 1])
            : session.Workspace.OpenProject("board-builds");
        var built = await session.For(owner).BuildBoardLibraryAsync(codeDir, ct: cts.Token);
        if (!built.Success) failed++;
        Out(built.Success
            ? $"netso OK   {Path.GetFileName(codeDir),-55} {new FileInfo(built.Output!).Length,9:N0} B"
            : $"netso FAIL {Path.GetFileName(codeDir),-55} {built.Error!.Title} | {built.Error.Evidence}");
    }
    logFile?.Dispose();
    return failed == 0 ? 0 : 1;
}

if (command == "export")
{
    // TopstToolkit.Cli export <project id> <destination folder>   the board package of a built project, as 7 내보내기 makes it
    var store = new TopstToolkit.Core.Projects.ProjectStore(session.Workspace);
    if (options.Positional.Count < 3 || store.Load(options.Positional[1]) is not { } built)
    {
        Out("usage: export <project id in the workspace> <destination folder>");
        return 2;
    }
    var packager = new TopstToolkit.Core.Export.BoardPackager(store, layout);
    if (packager.Problem(built) is { } problem)
    {
        Out("내보낼 수 없음: " + problem);
        return 1;
    }
    var exported = packager.Export(built, new TopstToolkit.Core.Export.ExportOptions(Path.GetFullPath(options.Positional[2])));
    Out($"내보냄 {exported.Root} · 파일 {exported.FileCount}개 · {exported.TotalBytes / 1048576.0:0.0} MB · net.so {(exported.HasNetSo ? "있음" : "없음")}");
    logFile?.Dispose();
    return 0;
}

if (command == "probe")
{
    // TopstToolkit.Cli probe --dir <folder> [--out <results.jsonl>] [--build 1] [--calib 4] [--cut-frontier 1] [--keep 1] [--only a,b]
    // The model zoo review (design §5.9). <folder> holds <id>.onnx files and export.jsonl from build\zoo\zoo_probe.py.
    // Each model is brought in the way the app does it (inspect, check, cut at a known head); with --cut-frontier a tail
    // the app has no decoder for is cut at the last supported points; with --build it is built once (calibration with
    // --calib pictures) for the time estimate. One JSON line per model; models already in --out are skipped.
    var probeDir = options.Get("--dir");
    if (probeDir is null || !File.Exists(Path.Combine(probeDir, "export.jsonl")))
    {
        Out("usage: probe --dir <folder with export.jsonl> [--out <file>] [--build 1] [--calib 4] [--cut-frontier 1]");
        return 2;
    }
    var probeOut = options.Get("--out") ?? Path.Combine(probeDir, "probe.jsonl");
    var probeBuild = options.Get("--build") is "1" or "true";
    var cutFrontier = options.Get("--cut-frontier") is "1" or "true";
    var keepProjects = options.Get("--keep") is "1" or "true";
    var calibCount = int.TryParse(options.Get("--calib"), out var calib) ? calib : 4;
    // models larger than this are checked but not built: too big for the board to be worth a place in the zoo
    var buildMaxParams = long.TryParse(options.Get("--build-max-params"), out var maxParams) ? maxParams : long.MaxValue;
    var onlyIds = options.Get("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    // --shard k/n: this worker takes every n-th model starting at k, so several can run side by side
    var shard = options.Get("--shard")?.Split('/') is [var k, var n] && int.TryParse(k, out var shardK) && int.TryParse(n, out var shardN) && shardN > 0 ? (shardK, shardN) : (0, 1);
    var jsonOptions = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    // every probe*.jsonl next to the output counts as done, whichever worker wrote it
    var probed = Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(probeOut))!, "probe*.jsonl")
        .SelectMany(File.ReadLines)
        .Select(l => { try { return System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("id").GetString(); } catch (System.Text.Json.JsonException) { return null; } })
        .Where(id => id is not null).ToHashSet();
    var store = new TopstToolkit.Core.Projects.ProjectStore(session.Workspace);
    var importer = new TopstToolkit.Core.Projects.ModelImporter(store, session);
    var builder = new ProjectBuilder(session, store);
    var exported = File.ReadLines(Path.Combine(probeDir, "export.jsonl")).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement).ToList();
    var count = 0;
    var position = -1;
    foreach (var item in exported)
    {
        position++;
        var id = item.GetProperty("id").GetString()!;
        if (position % shard.Item2 != shard.Item1)
            continue;
        if (!item.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean() || probed.Contains(id) || (onlyIds is not null && !onlyIds.Contains(id)))
            continue;
        var onnx = Path.Combine(probeDir, id + ".onnx");
        if (!File.Exists(onnx))
            continue;
        count++;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var project = store.Create(id, "probe", ProjectBuilder.SdkVersion);
        var record = new Dictionary<string, object?>
        {
            ["id"] = id, ["lib"] = item.GetProperty("lib").GetString(), ["name"] = item.GetProperty("name").GetString(),
            ["task"] = item.GetProperty("task").GetString(), ["size"] = item.GetProperty("size").GetInt32(),
            ["params"] = item.TryGetProperty("params", out var p) ? p.GetInt64() : 0, ["onnxBytes"] = new FileInfo(onnx).Length,
        };
        try
        {
            using var importCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            importCts.CancelAfter(TimeSpan.FromMinutes(15));
            var imported = await importer.ImportAsync(project, onnx, importCts.Token);
            record["guess"] = imported.Guess.PresetId;
            record["problems"] = imported.Problems;
            var model = project.Model!;
            if (model.Check is { } c)
            {
                record["outcome"] = c.Outcome;
                record["blockedReason"] = c.BlockedReason;
                record["checkError"] = c.ErrorTitle;
                record["checkDetail"] = c.ErrorDetail;
                record["layers"] = c.TotalLayers;
                record["ops"] = c.Ops;
                record["blockedOps"] = c.BlockedOps;
                record["bad"] = c.Layers.Select(l => new { l.Type, Why = l.Description.Length > 160 ? l.Description[..160] : l.Description }).Take(12).ToList();
                record["badTypes"] = c.Layers.Select(l => l.Type).Distinct().ToList();
                record["proposal"] = c.Proposal is { } pr ? new { pr.Kind, pr.Decoder, Nodes = pr.Nodes.Count, pr.KeptOps, pr.AwayOps } : null;
            }
            if (cutFrontier && model.NeedsCut && model.Check?.Proposal is { Decoder: null, Nodes.Count: > 0 } frontier)
            {
                using var cutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                cutCts.CancelAfter(TimeSpan.FromMinutes(10));
                await importer.CutAsync(project, frontier.Nodes, cutCts.Token);
                record["frontierCut"] = true;
            }
            record["cut"] = model.Cut is { } cut ? new { cut.By, cut.Decoder, cut.KeptOps, cut.AwayOps, Nodes = cut.Nodes.Count } : null;
            record["ready"] = project.Model!.IsReady;
            record["preset"] = project.Preset;
            record["kind"] = project.Task;
            record["postProcess"] = project.Options.Convert.PostProcess;
            record["outputs"] = project.Model.Facts?.Outputs.Select(o => string.Join("x", o.Shape)).Take(8).ToList();
            record["opset"] = project.Model.Facts?.Opset;
            record["importSec"] = Math.Round(watch.Elapsed.TotalSeconds, 1);

            if (probeBuild && project.Model.IsReady && (long)record["params"]! > buildMaxParams)
                record["skippedBuild"] = "params";
            else if (probeBuild && project.Model.IsReady)
            {
                project.Options.Calibration.Count = calibCount;
                store.Save(project);
                var buildWatch = System.Diagnostics.Stopwatch.StartNew();
                using var buildCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                buildCts.CancelAfter(TimeSpan.FromMinutes(40));
                var outcome = await builder.BuildAsync(project, _ => { }, null, buildCts.Token);
                record["built"] = outcome.Success;
                record["failedStage"] = outcome.FailedStage?.ToString();
                record["buildError"] = outcome.Error is { } e ? e.Title + " · " + (e.Detail.Length > 300 ? e.Detail[..300] : e.Detail) : null;
                record["ms"] = project.Headline.InferenceTimeMs;
                record["weightBytes"] = project.Headline.WeightBytes;
                record["peakMemoryBytes"] = outcome.PeakMemoryBytes;
                record["netso"] = outcome.BoardLibraryBuilt;
                record["boardPostProcess"] = ProjectBuilder.BoardPostProcessOf(project);
                record["result"] = outcome.Results.FirstOrDefault()?.Parsed is { } parsed
                    ? parsed.Kind == TopstToolkit.Engine.Parsing.SimResultKind.Detection ? $"{parsed.Detections.Count} boxes" : $"{parsed.Kind} {parsed.ClassNumber}"
                    : null;
                record["buildSec"] = Math.Round(buildWatch.Elapsed.TotalSeconds, 1);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
        {
            record["exception"] = ex.GetType().Name + ": " + (ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
        }
        record["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1);
        File.AppendAllText(probeOut, System.Text.Json.JsonSerializer.Serialize(record, jsonOptions) + Environment.NewLine);
        Out($"[{count}] {id,-42} {record.GetValueOrDefault("outcome"),-7} ready={record.GetValueOrDefault("ready")} " +
            (record.ContainsKey("built") ? $"built={record["built"]} {record.GetValueOrDefault("ms")} ms " : "") + $"{record["seconds"]} s" +
            (record.GetValueOrDefault("exception") is string err ? "  ! " + err : ""));
        if (!keepProjects)
        {
            try { Directory.Delete(store.FoldersOf(project).Root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    logFile?.Dispose();
    return 0;
}

if (command == "zoo")
{
    // TopstToolkit.Cli zoo list [--task class|obj]
    // TopstToolkit.Cli zoo prepare <id>[,<id>...] [--build 1] [--random-weights 1] [--root <components folder>]
    // The model zoo (design §5.9) without the window: what 모델 모음 does on "만들고 바로 빌드" — the weights from their
    // official source (checked, kept in <components>\downloads\zoo), the export the review did, the import every model
    // goes through, the catalog's preprocessing, and with --build the app's build. The export packages are the installed
    // "zoo" component, or TOPST_ZOO_SITE (development: the review's artifacts\zoo-site).
    var zooCatalog = TopstToolkit.Core.Zoo.ZooCatalog.Load(Path.Combine(TopstToolkit.Core.Zoo.ZooCatalog.DirectoryFor(layout.HelpersDir), TopstToolkit.Core.Zoo.ZooCatalog.FileName));
    if (options.Positional.ElementAtOrDefault(1) is not ("list" or "prepare"))
    {
        Out("usage: zoo list [--task class|obj] | zoo prepare <id>[,<id>...] [--build 1] [--random-weights 1] [--root <components folder>]");
        return 2;
    }
    if (options.Positional[1] == "list")
    {
        foreach (var m in zooCatalog.Models.Where(m => options.Get("--task") is not { } task || m.Task == task))
            Out($"{m.Id,-40} {m.TaskText} · {m.LibraryName,-11} {m.Display,-26} {m.Export.Size,4}px · {m.Review.Params / 1e6,6:0.0} M · {m.Review.Ms,7:0.00} ms · {m.Weights.Caption}");
        Out($"{zooCatalog.Models.Count} models (reviewed {zooCatalog.Reviewed})");
        return 0;
    }
    var componentCatalog = TopstToolkit.Core.Components.ComponentCatalog.Load(TopstToolkit.Core.Components.ComponentCatalog.DirectoryFor(layout.HelpersDir));
    var zooRoot = options.Get("--root") ?? TopstToolkit.Core.Components.ComponentsRoot.DefaultLocation(configured: TopstToolkit.Core.Settings.AppSettings.Load().ComponentsRoot);
    if (zooRoot is null)
    {
        Out("No usable components location. Pass --root <ASCII folder>.");
        return 2;
    }
    var zooStore = new TopstToolkit.Core.Projects.ProjectStore(session.Workspace);
    var preparer = new TopstToolkit.Core.Zoo.ZooPreparer(zooCatalog, new TopstToolkit.Core.Components.ComponentInstaller(componentCatalog, zooRoot), session, zooStore);
    Out($"packages  : {preparer.SiteDirectory ?? "(모델 모음 구성 요소 없음)"}");
    var zooFailures = 0;
    foreach (var id in (options.Positional.ElementAtOrDefault(2) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (zooCatalog.Find(id) is not { } model)
        {
            Out($"! {id}: not in the catalog");
            zooFailures++;
            continue;
        }
        Out($"=== {model.Id} · {model.LibraryName} {model.Display} · {model.Export.Size}px · {(model.Export.Plain ? "as is" : "fixes " + string.Join("/", model.Export.Fixes))}");
        var project = zooStore.NewDraft("새 프로젝트", "cli", ProjectBuilder.SdkVersion);
        string? lastStep = null;
        var lastTenth = -1;
        var zooProgress = new SyncProgress<TopstToolkit.Core.Zoo.ZooProgress>(p =>
        {
            var tenth = p.Fraction is { } f ? (int)(f * 10) : -1;
            if (p.Step == lastStep && tenth <= lastTenth)
                return;
            Out($"  [{DateTime.Now:HH:mm:ss}] {p.Step}" + (p.Fraction is { } g ? $" {g:P0}" : ""));
            (lastStep, lastTenth) = (p.Step, tenth);
        });
        try
        {
            var prepared = await preparer.PrepareAsync(model, project, zooProgress, null, cts.Token, options.Get("--random-weights") is "1" or "true");
            Out($"  프로젝트 {project.Id} · {project.Name} · {project.Preset} · 점검 {project.Model?.Check?.Outcome} · 자른 곳 {project.Model?.Cut?.Decoder ?? "없음"} · " +
                $"mean {string.Join("/", project.Options.Convert.Mean)} std {string.Join("/", project.Options.Convert.Std)} · 내보내기 {prepared.Export.Seconds:0.0} s, 고침 {string.Join(",", prepared.Export.Fixes.Keys)}");
            if (options.Get("--build") is "1" or "true")
            {
                var outcome = await new ProjectBuilder(session, zooStore).BuildAsync(project, _ => { }, null, cts.Token);
                Out(outcome.Success
                    ? $"  빌드: 성공 · 추정 {outcome.Perf?.InferenceTimeMs} ms (검토 {model.Review.Ms} ms) · net.so {(outcome.BoardLibraryBuilt ? "있음" : "없음")}"
                    : $"  빌드: 실패 ({outcome.FailedStage}) — {outcome.Error!.Title} · {outcome.Error.Detail}");
                foreach (var r in outcome.Results.Take(2))
                    Out("    " + (r.Parsed?.Kind == TopstToolkit.Engine.Parsing.SimResultKind.Detection
                        ? string.Join(", ", r.Parsed.Detections.Select(d => $"{d.Label} {d.ScorePercent:0.0}%"))
                        : $"{r.Parsed?.Kind} {r.Parsed?.ClassNumber} {r.Parsed?.ClassLabel}"));
                if (!outcome.Success)
                    zooFailures++;
            }
        }
        catch (TopstToolkit.Core.Zoo.ZooPrepareException e)
        {
            Out($"  실패 ({e.Stage}): {e.Title} · {e.Detail}" + (e.Evidence is { } ev ? $" · {ev}" : ""));
            zooFailures++;
        }
    }
    logFile?.Dispose();
    return zooFailures == 0 ? 0 : 1;
}

if (command == "project")
{
    // TopstToolkit.Cli project <model file> [--force-output A,B] [--preset <id>]
    // The same path the app takes: create a project, import the model (inspect + guess the family), build, build again
    // (the second build must reuse every stage). --preset takes the place of the guess, as picking another kind on the
    // model screen does (generic-unknown: a cut model whose part behind the cut becomes board code, design §5.8).
    var store = new TopstToolkit.Core.Projects.ProjectStore(session.Workspace);
    var importer = new TopstToolkit.Core.Projects.ModelImporter(store, session);
    var builder = new ProjectBuilder(session, store);
    var project = store.Create("새 프로젝트", "cli", ProjectBuilder.SdkVersion);
    var import = await importer.ImportAsync(project, options.Positional[1], cts.Token);
    Out($"프로젝트 {project.Id} · {project.Name}");
    Out($"  추정: {import.Guess.PresetId} — {import.Guess.Reason}");
    foreach (var note in import.Guess.Notes.Concat(import.Problems)) Out($"  ! {note}");
    if (options.Get("--preset") is { } presetId)
    {
        if (TopstToolkit.Core.Presets.PresetCatalog.Find(presetId) is not { } preset)
        {
            Out($"  ! 프리셋 {presetId}이 없습니다");
            return 2;
        }
        // as the settings screen does (ConfigViewModel.ApplyPreset): a companion file of another kind does not carry over
        var oldCompanion = TopstToolkit.Core.Presets.PresetCatalog.Find(project.Preset)?.Companion;
        preset.ApplyTo(project);
        if (preset.Companion != oldCompanion)
            project.Options.Convert.PostProcess = null;
        store.Save(project);
        Out($"  프리셋: {preset.Id} (추정 대신)");
    }
    if (project.Model?.Cut is { } cut)
        Out($"  자른 곳: {string.Join(", ", cut.Nodes)} (decoder {cut.Decoder ?? "없음"})");
    Out($"  설정: task={project.Task} numClass={project.Options.Convert.NumClass} yolo={project.Options.Convert.YoloVersion} order={project.Options.Convert.OutputOrder} " +
        $"post={project.Options.Convert.PostProcess} labels={project.Options.Labels.Set} letterbox={project.Options.Convert.Letterbox}");
    if (options.Get("--force-output") is { } forced)
        project.Options.Convert.ForceOutput = [.. forced.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    var ok = true;
    for (var round = 1; round <= 2 && ok; round++)
    {
        Out($"--- 빌드 {round}");
        var seen = new HashSet<string>();
        var outcome = await builder.BuildAsync(project, u =>
        {
            var line = $"{u.Stage}:{u.State}";
            if (u.State != BuildStageState.Running || seen.Add(line))
                lock (seen) Out($"  [{u.Stage}] {u.State} · {u.Detail}" + (u.Elapsed is { } t ? $" · {t.TotalSeconds:0.0} s" : ""));
        }, null, cts.Token);
        ok = outcome.Success;
        Out(outcome.Success
            ? $"  결과: 성공 · 실행 {outcome.Executed} · 재사용 {outcome.Reused} · 추정 {outcome.Perf?.InferenceTimeMs} ms · net.so {(outcome.BoardLibraryBuilt ? "있음" : "없음")} · 사진 {outcome.Results.Count}장"
            : $"  결과: 실패 ({outcome.FailedStage}) — {outcome.Error!.Title} · {outcome.Error.Detail}");
        foreach (var layer in outcome.Compatibility?.Layers ?? []) Out($"    비호환: {layer.Index} {layer.Name} ({layer.Type}) — {layer.Description}");
        if (project.Steps.TryGetValue("netso", out var netso))
            Out($"    net.so: {string.Join(" · ", netso.Facts.Select(f => f.Key + "=" + f.Value))}");
        if (project.Steps.TryGetValue(ProjectBuilder.TailEvalStep, out var tailEval))
            Out($"    잘라낸 구간 PC 계산: {tailEval.Status} {tailEval.ErrorTitle}");
        foreach (var r in outcome.Results.Take(2))
            Out("    " + (r.Parsed?.Kind == TopstToolkit.Engine.Parsing.SimResultKind.Detection
                ? string.Join(", ", r.Parsed.Detections.Select(d => $"{d.Label} {d.ScorePercent:0.0}%"))
                : $"{r.Parsed?.Kind} {r.Parsed?.ClassNumber} {r.Parsed?.ClassLabel}{r.Parsed?.TensorShape}"));
    }
    logFile?.Dispose();
    return ok ? 0 : 1;
}

var models = options.Get("--models") ?? @"E:\TOPST\tc-nn-toolkit\tc-nn-toolkit\input_networks";
var which = options.Positional.ElementAtOrDefault(1) ?? "all";
string[] names = which switch
{
    "all" => Recipes.Names,
    "families" => Recipes.FamilyNames,
    "everything" => [.. Recipes.Names, .. Recipes.FamilyNames],
    _ => which.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
};
var recipes = new Recipes(session, models, Out);

var failures = 0;
var total = Stopwatch.StartNew();
foreach (var name in names)
{
    var recipeTime = Stopwatch.StartNew();
    try
    {
        var (outcome, expectations) = await recipes.RunAsync(name, cts.Token);
        Out($"  [{name}] 기대 결과 ({recipeTime.Elapsed.TotalSeconds:0} s)");
        foreach (var expectation in expectations)
        {
            bool ok;
            try { ok = expectation.Check(outcome); }
            catch (Exception) { ok = false; }
            if (!ok) failures++;
            Out($"    {(ok ? "OK  " : "FAIL")} {expectation.Description}");
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // one broken recipe must not hide the results of the others
        failures++;
        Out($"  [{name}] 예외로 중단: {ex.GetType().Name}: {ex.Message}");
    }
    Out("");
}
Out($"총 {total.Elapsed.TotalSeconds:0} s, 실패한 기대 결과 {failures}개");
logFile?.Dispose();
return failures == 0 ? 0 : 1;

static string? FindRepoRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "TopstToolkit.slnx")))
            return d.FullName;
    return null;
}

static CliOptions ParseOptions(string[] args)
{
    var result = new CliOptions();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            result.Named[args[i]] = args[++i];
        else
            result.Positional.Add(args[i]);
    }
    return result;
}

internal sealed class CliOptions
{
    public List<string> Positional { get; } = [];
    public Dictionary<string, string> Named { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string name) => Named.GetValueOrDefault(name);
}

/// <summary>Reports on the caller's thread, in order (Progress&lt;T&gt; would post to the thread pool in a console).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
