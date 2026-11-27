using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TopstToolkit.Core.Projects;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine.Board;
using TopstToolkit.Engine.Commands;
using TopstToolkit.Engine.Errors;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.Core.Pipeline;

/// <summary>The stages a build shows, in order (design §5.2 "4 빌드").</summary>
public enum BuildStage
{
    Compatibility,
    Convert,
    Quantize,
    Compile,
    BoardLibrary,
    Simulate,
    Perf,
}

public enum BuildStageState
{
    Pending,
    Running,
    Done,
    /// <summary>Nothing it depends on changed since the last build; the earlier output is used (design §4.5).</summary>
    Reused,
    Failed,
    /// <summary>Not run on purpose (for example no board tools installed).</summary>
    Skipped,
}

/// <param name="Detail">One short line for the stage card.</param>
/// <param name="Fraction">0..1 when known.</param>
/// <param name="PeakMemoryBytes">Largest working set a tool of this build reached so far, sent when a stage ends.</param>
public sealed record BuildUpdate(BuildStage Stage, BuildStageState State, string? Detail = null, double? Fraction = null,
    TimeSpan? Elapsed = null, ToolError? Error = null, long? PeakMemoryBytes = null);

/// <summary>Where the build outputs of a project are. The builder and the result, performance and export screens share these names.</summary>
/// <param name="Stem">Network name: the model file name without <c>_extracted</c> and the extension.</param>
/// <param name="CodeDir">Compiler output, the folder that goes to the board (<c>&lt;stem&gt;_quantized</c>).</param>
/// <param name="Int8DumpDir">NPU input/output dumps of the INT8 run, one folder per test input.</param>
public sealed record BuildPaths(string Stem, string Fp32Network, string Int8Network, string CodeDir, string NetSo, string PerfJson,
    string Int8ResultDir, string Fp32ResultDir, string Int8DumpDir)
{
    public static BuildPaths? For(ProjectStore store, ProjectFile project)
    {
        if (project.Model is null || ProjectStore.IsDraft(project))
            return null;
        var folders = store.FoldersOf(project);
        var stem = Path.GetFileNameWithoutExtension(project.Model.Staged);
        if (stem.EndsWith("_extracted", StringComparison.OrdinalIgnoreCase)) stem = stem[..^"_extracted".Length];
        var int8 = Path.Combine(folders.NetworksDir, stem + "_quantized.enlight");
        var codeDir = CompilerOutputs.OutputDirFor(folders.CodeDir, int8);
        return new BuildPaths(stem, Path.Combine(folders.NetworksDir, stem + ".enlight"), int8, codeDir, Path.Combine(codeDir, "net.so"),
            Path.Combine(folders.PerfDir, stem + "_quantized_performance.json"),
            Path.Combine(folders.ResultDir, stem + "_quantized"), Path.Combine(folders.ResultDir, stem), Path.Combine(folders.DumpDir, stem + "_quantized"));
    }
}

public sealed class BuildOutcome
{
    public bool Success => Error is null;
    public bool Cancelled => Error?.Code == ToolErrorCode.Cancelled;
    public ToolError? Error { get; init; }
    public BuildStage? FailedStage { get; init; }
    public CompatibilityReport? Compatibility { get; init; }
    /// <summary>Results of the INT8 network: what the board will compute.</summary>
    public IReadOnlyList<SimImageResult> Results { get; init; } = [];
    /// <summary>Results of the original FP32 network on the same inputs, for comparison. Empty when that run failed.</summary>
    public IReadOnlyList<SimImageResult> Fp32Results { get; init; } = [];
    public PerfResult? Perf { get; init; }
    public NetworkBuffers? Buffers { get; init; }
    public string? CodeDir { get; init; }
    public bool BoardLibraryBuilt { get; init; }
    public int Reused { get; init; }
    public int Executed { get; init; }
    /// <summary>Largest working set any tool of this build reached; 0 when every stage was reused.</summary>
    public long PeakMemoryBytes { get; init; }
}

/// <summary>
/// Builds a project end to end: prepares inputs, runs each stage or reuses its earlier output when nothing it depends on
/// changed, records the results in <c>project.json</c>. Layout-independent; the screens only listen to
/// <see cref="BuildUpdate"/> and tool events.
/// </summary>
public sealed class ProjectBuilder(EngineSession engine, ProjectStore store)
{
    public const string SdkVersion = "0.9.9";

    private static readonly Dictionary<string, string> LabelFiles = new()
    {
        ["coco80"] = "coco_80.label", ["coco91"] = "coco_91.label", ["voc20"] = "voc.label", ["imagenet"] = "imagenet.label",
    };

    /// <summary>The bundled label file of a label set (<c>coco80</c> gives <c>coco_80.label</c>); null for custom or unknown sets.</summary>
    public static string? LabelFileName(string? set) => set is not null && LabelFiles.TryGetValue(set, out var name) ? name : null;

    /// <summary>
    /// Fact of the <c>netso</c> step: which post-processing the library was built with
    /// (<see cref="FastPostProcess.KindOf"/>; absent in projects built before 2026-11-22 = the toolkit's own code).
    /// </summary>
    public const string BoardPostProcessFact = "postProcess";

    /// <summary>Which post-processing the project's <c>net.so</c> was built with; "toolkit" when not recorded.</summary>
    public static string BoardPostProcessOf(ProjectFile project) =>
        project.Steps.TryGetValue("netso", out var step) && step.Status == "done" && step.Facts.TryGetValue(BoardPostProcessFact, out var kind) ? kind : "toolkit";

    /// <summary>Facts of the <c>netso</c> step with generated code for the part behind the cut (design §5.8, stage 나).</summary>
    public const string TailOpsFact = "tailOps";
    public const string TailOutputsFact = "tailOutputs";
    /// <summary>Why the part behind the cut did not become board code, in a few words; the library then has the toolkit's code.</summary>
    public const string TailProblemFact = "tailProblem";

    /// <summary>The record of the PC computing the part behind the cut for the check pictures (<see cref="TailFiles"/>).</summary>
    public const string TailEvalStep = "tailEval";

    /// <summary>The part of a model behind its cut, to become board code: the original ONNX file and the operators cut at.</summary>
    public sealed record TailPlan(string Model, IReadOnlyList<string> Nodes);

    /// <summary>
    /// Whether the part behind the cut becomes board code (design §5.8, stage 나): for a model of the kind "기타" (its
    /// outputs as they are) that the app cut, while the original ONNX file is in the project and the app's own board code
    /// is switched on. Not for detection models: the toolkit's box maths (and the app's fast version of it) takes the cut
    /// outputs as they are. Null otherwise.
    /// </summary>
    public static TailPlan? TailPlanOf(ProjectStore store, ProjectFile project)
    {
        if (!project.Options.Board.FastPostProcess || project.Task != "unknown")
            return null;
        if (project.Model is not { Format: "onnx", Original: { } original, Cut: { Nodes.Count: > 0 } cut })
            return null;
        var path = store.ToAbsolute(project, original);
        return File.Exists(path) ? new TailPlan(path, [.. cut.Nodes]) : null;
    }

    /// <summary>What the stage card says after "net.so" about the code in the library; empty for the toolkit's own code.</summary>
    public static string BoardStageSuffix(IReadOnlyDictionary<string, string> facts)
    {
        var kind = facts.GetValueOrDefault(BoardPostProcessFact) ?? "toolkit";
        if (kind == TailCode.Kind)
            return " · " + FastPostProcess.Describe(kind) + (facts.TryGetValue(TailOpsFact, out var ops) ? $" (연산 {ops}개)" : "");
        if (kind != "toolkit")
            return " · " + FastPostProcess.Describe(kind);
        return facts.TryGetValue(TailProblemFact, out var problem) ? $" · 툴킷 기본 코드 (잘라낸 구간: {problem})" : "";
    }

    public async Task<BuildOutcome> BuildAsync(ProjectFile project, Action<BuildUpdate>? onUpdate = null,
        Action<ToolEvent>? onToolEvent = null, CancellationToken ct = default)
    {
        var folders = store.FoldersOf(project);
        var steps = engine.For(folders);
        long peakMemory = 0;
        void Tell(BuildStage stage, BuildStageState state, string? detail = null, double? fraction = null, TimeSpan? elapsed = null, ToolError? error = null) =>
            onUpdate?.Invoke(new BuildUpdate(stage, state, detail, fraction, elapsed, error,
                state is BuildStageState.Done or BuildStageState.Failed && peakMemory > 0 ? peakMemory : null));

        if (project.Model is null || string.IsNullOrEmpty(project.Task))
            return new BuildOutcome { Error = new ToolError(ToolErrorCode.InvalidOptions, "모델과 작업 종류를 먼저 정해 주세요", "1 모델, 2 설정 단계를 확인해 주세요.", SuggestedAction.GoToConvert), FailedStage = BuildStage.Convert };

        // ---------- inputs
        var modelPath = store.ToAbsolute(project, project.Model.Staged);
        var isNpy = project.Options.Calibration.Source == "npy";
        if (!Directory.EnumerateFiles(folders.CalibrationDir).Any())
        {
            var source = project.Options.Calibration.Source == "samples" || project.Options.Calibration.SourcePath is null
                ? engine.Layout.DefaultCalibrationDir
                : project.Options.Calibration.SourcePath;
            var staged = FileStager.StageDataFiles([source], folders.CalibrationDir, isNpy ? FileStager.NpyExtensions : FileStager.ImageExtensions,
                limit: Math.Max(1, project.Options.Calibration.Count));
            project.Options.Calibration.ListSha256 = staged.ListSha256;
        }
        var calibrationCount = Directory.EnumerateFiles(folders.CalibrationDir).Count();
        if (!Directory.EnumerateFiles(folders.TestImagesDir).Any())
        {
            if (isNpy)
                File.Copy(Directory.EnumerateFiles(folders.CalibrationDir).First(), Path.Combine(folders.TestImagesDir, "input.npy"));
            else
                FileStager.StageImages([engine.Layout.SampleImage], folders.TestImagesDir);
        }
        string? labelsPath = null;
        if (project.Options.Labels.Toolkit is { } customLabels)
            labelsPath = store.ToAbsolute(project, customLabels);
        else if (project.Options.Labels.Set is { } set && LabelFiles.TryGetValue(set, out var labelFile))
            labelsPath = FileStager.StageFile(Path.Combine(engine.Layout.LabelsDir, labelFile), folders.LabelsInputDir).Staged;
        // a grid or anchor file the settings lost while the file is still in the project (switching the kind away and back)
        if (Companions.Restore(store, project))
            store.Save(project);
        var postProcess = project.Options.Convert.PostProcess is { } pp ? store.ToAbsolute(project, pp) : null;
        var testInputs = Directory.EnumerateFiles(folders.TestImagesDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        // ---------- names
        var paths = BuildPaths.For(store, project)!;
        var stem = paths.Stem;
        var fp32 = paths.Fp32Network;
        var int8 = paths.Int8Network;
        var codeDir = paths.CodeDir;
        var perfJson = paths.PerfJson;

        // ---------- keys: what each stage depends on (design §4.5)
        var c = project.Options.Convert;
        var convertKey = Key("convert", SdkVersion, project.Model.Sha256, project.Task, Nums(c.Mean), Nums(c.Std), c.NumClass, c.YoloVersion, c.DflRegMax,
            c.OutputOrder, Nums(c.Variance), c.Logistic, c.NoBackground, c.Letterbox, string.Join(",", c.ForceOutput), HashOf(postProcess), HashOf(labelsPath),
            project.Model.Weights, project.Options.Calibration.Source, project.Options.Calibration.ListSha256, calibrationCount);
        var quantizeKey = Key("quantize", convertKey, project.Options.Quantize.MStd8, project.Options.Quantize.MStdRatio);
        var compileKey = Key("compile", quantizeKey, project.Options.Compile.ThConf, project.Options.Compile.ThIou, project.Options.Compile.MaxDetection);
        // Which post-processing goes into net.so is known only once the compiler has written post_process.c, so this
        // key is made right before that stage. With the toolkit's own code it is the key older projects have. Generated
        // code for the part behind the cut depends on the original model, the cut and the generator itself.
        var tail = TailPlanOf(store, project);
        string BoardKey() =>
            tail is not null
                ? Key("netso", compileKey, "tail", project.Model.Sha256, string.Join("|", tail.Nodes), TailCode.GeneratorHash(engine.Layout))
                : (project.Options.Board.FastPostProcess ? FastPostProcess.Find(engine.Layout, codeDir) : null) is { } fast
                    ? Key("netso", compileKey, "fast", HashOf(fast))
                    : Key("netso", compileKey);
        var simulateKey = Key("simulate", "int8+fp32+dump", quantizeKey, string.Join("|", testInputs.Select(FileStager.Sha256Of)), project.Options.Compile.ThConf, project.Options.Compile.ThIou, c.Letterbox);
        var perfKey = Key("perf", compileKey, project.Options.Perf.ClockPreset);

        int reused = 0, executed = 0;
        CompatibilityReport? compatibility = null;
        ToolError? failure = null;
        BuildStage? failedStage = null;

        IReadOnlyList<SimImageResult> fp32Results = [];
        BuildOutcome Finish(IReadOnlyList<SimImageResult>? results = null, PerfResult? perf = null, bool boardLibrary = false)
        {
            store.Save(project);
            return new BuildOutcome
            {
                Error = failure, FailedStage = failedStage, Compatibility = compatibility, Results = results ?? [], Fp32Results = fp32Results, Perf = perf,
                Buffers = NetworkBuffers.ParseFile(Path.Combine(codeDir, "network_buf.txt")), CodeDir = codeDir, BoardLibraryBuilt = boardLibrary,
                Reused = reused, Executed = executed, PeakMemoryBytes = peakMemory,
            };
        }

        bool Fresh(string step, string key, params string[] outputs) =>
            project.Steps.TryGetValue(step, out var r) && r.Status == "done" && r.Key == key && outputs.All(o => File.Exists(o) || Directory.Exists(o));

        void Record(string step, string key, StepResult result, params (string Name, string Value)[] facts)
        {
            project.Steps[step] = new StepRecord
            {
                Key = key,
                Status = result.Success ? "done" : result.Error!.Code == ToolErrorCode.Cancelled ? "cancelled" : "failed",
                ElapsedSec = Math.Round(result.Run.Elapsed.TotalSeconds, 1),
                FinishedAt = DateTimeOffset.Now,
                Outputs = result.Output is null ? [] : [store.ToRelative(project, result.Output)],
                Facts = facts.ToDictionary(f => f.Name, f => f.Value),
                ErrorCode = result.Error?.Code.ToString(),
                ErrorTitle = result.Error?.Title,
            };
            peakMemory = Math.Max(peakMemory, result.Run.PeakWorkingSetBytes);
        }

        // ---------- 1+2. compatibility check and conversion with calibration
        if (Fresh("convert", convertKey, fp32))
        {
            reused++;
            var facts = project.Steps["convert"].Facts;
            Tell(BuildStage.Compatibility, BuildStageState.Reused, CompatText(facts.GetValueOrDefault("compatible"), facts.GetValueOrDefault("total")));
            Tell(BuildStage.Convert, BuildStageState.Reused, $"사진 {calibrationCount}장으로 값의 범위를 쟀습니다");
        }
        else
        {
            executed++;
            project.Headline.NeedsRebuild = true;
            Tell(BuildStage.Compatibility, BuildStageState.Running, "NPU에서 실행할 수 있는지 확인하는 중");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var compatTold = false;
            void OnConvertEvent(ToolEvent e)
            {
                onToolEvent?.Invoke(e);
                switch (e)
                {
                    case CompatibilitySummaryEvent s when !compatTold && s.Incompatible == 0:
                        compatTold = true;
                        Tell(BuildStage.Compatibility, BuildStageState.Done, CompatText(s.Compatible, s.Total), elapsed: watch.Elapsed);
                        Tell(BuildStage.Convert, BuildStageState.Running, "캘리브레이션 준비 중");
                        break;
                    // only after the layers passed: with incompatible layers the converter still writes its file,
                    // and that must not look like a conversion in progress
                    case ProgressEvent { Stage: ToolStage.Calibration } p when compatTold:
                        Tell(BuildStage.Convert, BuildStageState.Running, $"캘리브레이션 {p.Current} / {p.Total}", p.Fraction);
                        break;
                    case ProgressEvent { Stage: ToolStage.Serialize } when compatTold:
                        Tell(BuildStage.Convert, BuildStageState.Running, "저장하는 중", 1);
                        break;
                }
            }

            ConverterOptions Options(string input, string output, bool withPostProcess, bool track, IReadOnlyList<string>? forceOutput, NetworkType? type = null) => new()
            {
                Model = input, Output = output, Type = type ?? TypeOf(project.Task),
                CompatibilityLogRoot = folders.CompatDir, DatasetRoot = folders.CalibrationDir,
                Dataset = isNpy ? "CustomNPY" : "Custom", EnableTrack = track, NumImages = track ? calibrationCount : null,
                Mean = isNpy ? null : c.Mean, Std = isNpy ? null : c.Std,
                NumClass = c.NumClass, ClassLabels = project.Task == "unknown" ? null : labelsPath,
                PostProcessFile = withPostProcess ? postProcess : null,
                YoloVersion = withPostProcess || postProcess is null ? c.YoloVersion : null,
                DflRegMax = c.DflRegMax, OutputOrder = c.OutputOrder, Variance = c.Variance, Logistic = c.Logistic, NoBackground = c.NoBackground,
                EnableLetterbox = c.Letterbox, ForceOutput = forceOutput,
                Weight = project.Model.Weights is { } w ? store.ToAbsolute(project, w) : null,
            };

            StepResult convert;
            if (c.ForceOutput.Count > 0 && postProcess is not null && project.Task == "obj")
            {
                // two-step conversion (design §5.4): cut before the unsupported layers, then attach the SDK's post-process
                var cut = Path.Combine(folders.NetworksDir, stem + "_cut.enlight");
                convert = await steps.ConvertAsync(Options(modelPath, cut, withPostProcess: false, track: false, c.ForceOutput, NetworkType.Unknown), "convert-cut", OnConvertEvent, ct).ConfigureAwait(false);
                if (convert.Success)
                {
                    compatTold = false;
                    convert = await steps.ConvertAsync(Options(cut, fp32, withPostProcess: true, track: true, null), "convert", OnConvertEvent, ct).ConfigureAwait(false);
                }
            }
            else
            {
                convert = await steps.ConvertAsync(Options(modelPath, fp32, withPostProcess: true, track: true, c.ForceOutput.Count > 0 ? c.ForceOutput : null), "convert", OnConvertEvent, ct).ConfigureAwait(false);
            }

            compatibility = convert.Compatibility;
            Record("convert", convertKey, convert, ("compatible", (compatibility?.Compatible ?? 0).ToString(CultureInfo.InvariantCulture)),
                ("total", (compatibility?.Total ?? 0).ToString(CultureInfo.InvariantCulture)), ("images", calibrationCount.ToString(CultureInfo.InvariantCulture)));
            if (!convert.Success)
            {
                failure = convert.Error;
                failedStage = convert.Error!.Code is ToolErrorCode.IncompatibleLayers or ToolErrorCode.CompatibilityUnknown || !compatTold ? BuildStage.Compatibility : BuildStage.Convert;
                Tell(failedStage.Value, BuildStageState.Failed, convert.Error.Title, elapsed: watch.Elapsed, error: convert.Error);
                return Finish();
            }
            project.Headline.CompatibleLayers = compatibility?.Compatible;
            project.Headline.TotalLayers = compatibility?.Total;
            Tell(BuildStage.Convert, BuildStageState.Done, $"사진 {calibrationCount}장으로 값의 범위를 쟀습니다", 1, convert.Run.Elapsed);
        }

        // ---------- generic stage runner
        async Task<StepResult?> Stage(BuildStage stage, string step, string key, string running, Func<Action<ToolEvent>, Task<StepResult>> run,
            Func<StepResult, string> done, Func<string> reusedText, params string[] outputs)
        {
            if (failure is not null) return null;
            if (Fresh(step, key, outputs))
            {
                reused++;
                Tell(stage, BuildStageState.Reused, reusedText());
                return null;
            }
            executed++;
            Tell(stage, BuildStageState.Running, running);
            var result = await run(e =>
            {
                onToolEvent?.Invoke(e);
                switch (e)
                {
                    case StageEvent { Stage: ToolStage.Optimize } s: Tell(stage, BuildStageState.Running, $"최적화 ({s.Name})"); break;
                    case StageEvent { Stage: ToolStage.CodeEmit } s: Tell(stage, BuildStageState.Running, $"코드 생성 ({s.Name})"); break;
                    case ProgressEvent { Stage: ToolStage.WriteWeights } p: Tell(stage, BuildStageState.Running, "가중치 기록", p.Fraction); break;
                    case ProgressEvent { Stage: ToolStage.Inference } p: Tell(stage, BuildStageState.Running, $"사진 {p.Current} / {p.Total}", p.Fraction); break;
                }
            }).ConfigureAwait(false);
            Record(step, key, result);
            if (!result.Success)
            {
                failure = result.Error;
                failedStage = stage;
                Tell(stage, BuildStageState.Failed, result.Error!.Title, elapsed: result.Run.Elapsed, error: result.Error);
                return result;
            }
            Tell(stage, BuildStageState.Done, done(result), 1, result.Run.Elapsed);
            return result;
        }

        await Stage(BuildStage.Quantize, "quantize", quantizeKey, "FP32 → INT8",
            on => steps.QuantizeAsync(new QuantizerOptions { Model = fp32, Output = int8, MStd8 = project.Options.Quantize.MStd8, MStdRatio = project.Options.Quantize.MStdRatio }, onEvent: on, ct: ct),
            _ => "FP32 → INT8", () => "FP32 → INT8", int8).ConfigureAwait(false);

        var isDetection = project.Task == "obj";
        await Stage(BuildStage.Compile, "compile", compileKey, "NPU 명령으로 바꾸는 중",
            on => steps.CompileAsync(new CompilerOptions
            {
                Model = int8, DumpRoot = folders.CodeDir,
                ThConf = isDetection ? project.Options.Compile.ThConf : null, ThIou = isDetection ? project.Options.Compile.ThIou : null,
                MaxDetection = project.Options.Compile.MaxDetection,
            }, onEvent: on, ct: ct),
            r => r.Buffers is { } b ? $"보드 메모리 {b.Total / 1048576.0:0.0} MB" : "완료",
            () => NetworkBuffers.ParseFile(Path.Combine(codeDir, "network_buf.txt")) is { } b ? $"보드 메모리 {b.Total / 1048576.0:0.0} MB" : "완료",
            Path.Combine(codeDir, "npu_cmd.bin"), Path.Combine(codeDir, "network.h")).ConfigureAwait(false);

        // ---------- board library (optional component)
        var netSo = paths.NetSo;
        var boardBuilt = false;
        if (failure is null)
        {
            if (engine.Layout.FindCrossGcc() is null)
            {
                Tell(BuildStage.BoardLibrary, BuildStageState.Skipped, "보드 도구가 없어 건너뜁니다 · 설정 › 구성 요소에서 설치할 수 있어요");
            }
            else
            {
                // what this build adds to the facts besides the kind of code: the generated tail code, or why there is none
                var boardFacts = new Dictionary<string, string>();
                async Task<StepResult> BuildBoard()
                {
                    if (tail is null)
                        return await steps.BuildBoardLibraryAsync(codeDir, ct: ct, fastPostProcess: project.Options.Board.FastPostProcess).ConfigureAwait(false);
                    // the part behind the cut becomes C first (design §5.8, stage 나); when it cannot, the library gets the
                    // toolkit's own code and the card says why
                    var generated = await steps.GenerateTailAsync(tail.Model, tail.Nodes, int8, codeDir, folders.TailDir, stem, ct).ConfigureAwait(false);
                    if (generated.Cancelled)
                        return new StepResult { Step = "netso", Run = generated.Run, Error = ErrorClassifier.Classify(generated.Run), Output = netSo };
                    if (generated.Source is { } source)
                    {
                        boardFacts[TailOpsFact] = generated.Value!.Ops.ToString(CultureInfo.InvariantCulture);
                        boardFacts[TailOutputsFact] = string.Join(", ", generated.Value.Outputs.Select(o => o.Name + " " + o.ShapeText));
                        Tell(BuildStage.BoardLibrary, BuildStageState.Running, "net.so 만드는 중");
                        var built = await steps.BuildBoardLibraryAsync(codeDir, ct: ct, tailSource: source).ConfigureAwait(false);
                        if (built.Success && built.BoardPostProcess != TailCode.Kind)
                            boardFacts[TailProblemFact] = "만든 코드가 컴파일되지 않음";
                        return built;
                    }
                    boardFacts[TailProblemFact] = generated.Problem ?? "구간을 코드로 옮기지 못함";
                    return await steps.BuildBoardLibraryAsync(codeDir, ct: ct, fastPostProcess: false).ConfigureAwait(false);
                }
                Dictionary<string, string> FactsOf(string? kind) => new(boardFacts) { [BoardPostProcessFact] = kind ?? "toolkit" };
                string BoardText(IReadOnlyDictionary<string, string> facts) => $"net.so {new FileInfo(netSo).Length / 1024.0:N0} KB" + BoardStageSuffix(facts);
                var r = await Stage(BuildStage.BoardLibrary, "netso", BoardKey(), tail is null ? "net.so 만드는 중" : "잘라낸 구간을 C 코드로 옮기는 중",
                    _ => BuildBoard(),
                    built => BoardText(FactsOf(built.BoardPostProcess)),
                    () => BoardText(project.Steps["netso"].Facts), netSo).ConfigureAwait(false);
                if (r is { Success: true })
                    foreach (var (name, value) in FactsOf(r.BoardPostProcess))
                        project.Steps["netso"].Facts[name] = value;
                boardBuilt = failure is null;
            }
        }

        // ---------- PC inference with the INT8 network: what the board will compute
        IReadOnlyList<SimImageResult> results = [];
        var resultDir = paths.Int8ResultDir;
        SimulatorOptions SimOptions(string model, bool dump) => new()
        {
            Model = model, Inputs = isNpy ? testInputs[0] : folders.TestImagesDir, ResultRoot = folders.ResultDir, DumpRoot = folders.DumpDir,
            ThConf = isDetection ? project.Options.Compile.ThConf : null, ThIou = isDetection ? project.Options.Compile.ThIou : null,
            EnableLetterbox = c.Letterbox, Dump = dump,
        };
        var simulated = await Stage(BuildStage.Simulate, "simulate", simulateKey, "결과 확인용 사진으로 실행",
            async on =>
            {
                // INT8 first: this is the result that counts, and its dump gives the NPU input file for the board package
                var int8Run = await steps.SimulateAsync(SimOptions(int8, dump: true), onEvent: on, ct: ct).ConfigureAwait(false);
                if (!int8Run.Success)
                    return int8Run;
                // the original network on the same inputs, so the result screen can show what quantization changed
                Tell(BuildStage.Simulate, BuildStageState.Running, "원본(FP32)으로도 실행하는 중");
                var fp32Run = await steps.SimulateAsync(SimOptions(fp32, dump: false), "simulate-fp32", on, ct).ConfigureAwait(false);
                peakMemory = Math.Max(peakMemory, fp32Run.Run.PeakWorkingSetBytes);
                if (fp32Run.Error?.Code == ToolErrorCode.Cancelled)
                    return fp32Run;
                fp32Results = fp32Run.Success ? fp32Run.SimResults : []; // a failed comparison run does not fail the build
                return int8Run;
            },
            r => Describe(r.SimResults), () => "이전 결과", resultDir, paths.Fp32ResultDir).ConfigureAwait(false);
        if (failure is null)
        {
            results = simulated?.SimResults ?? LoadResults(resultDir);
            if (simulated is null)
                fp32Results = LoadResults(paths.Fp32ResultDir);
        }

        // ---------- the part behind the cut on the PC (design §5.8, stage 나): what the generated board code computes for
        // the check pictures, from the same NPU outputs. The result screen shows it and the package's board check compares
        // with it. It never fails the build: the library and the simulation are complete without it.
        if (failure is null && tail is not null && BoardPostProcessOf(project) == TailCode.Kind && Directory.Exists(paths.Int8DumpDir))
        {
            var evalKey = Key("tail-eval", project.Steps["netso"].Key, simulateKey);
            var evalAnswer = Path.Combine(folders.TailDir, TailFiles.Evaluation);
            var dumps = Directory.EnumerateDirectories(paths.Int8DumpDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            if (dumps.Count > 0 && !Fresh(TailEvalStep, evalKey, evalAnswer))
            {
                var evaluated = await steps.EvaluateTailAsync(tail.Model, tail.Nodes, codeDir, dumps, folders.TailDir, ct).ConfigureAwait(false);
                if (!evaluated.Run.Cancelled)
                {
                    var ok = evaluated.Value is { Ok: true } && File.Exists(evalAnswer);
                    project.Steps[TailEvalStep] = new StepRecord
                    {
                        Key = evalKey, Status = ok ? "done" : "failed", ElapsedSec = Math.Round(evaluated.Run.Elapsed.TotalSeconds, 1),
                        FinishedAt = DateTimeOffset.Now, Outputs = ok ? [store.ToRelative(project, evalAnswer)] : [],
                        ErrorTitle = ok ? null : evaluated.Value?.Problems.FirstOrDefault() ?? "eval_tail.py did not answer",
                    };
                }
            }
        }

        // ---------- estimated inference time on the board
        PerfResult? perf = null;
        var perfStep = await Stage(BuildStage.Perf, "perf", perfKey, "보드에서의 추론 시간 계산",
            on => steps.PerfAsync(new PerfOptions { NpuCmdHeader = Path.Combine(codeDir, "npu_cmd.h"), NpuConfig = engine.Layout.ExampleNpuConfig, Output = perfJson }, onEvent: on, ct: ct),
            r => r.Perf is { } p ? $"{p.InferenceTimeMs.ToString("0.0", CultureInfo.InvariantCulture)} ms" : "완료",
            () => PerfResult.ParseFile(perfJson) is { } p ? $"{p.InferenceTimeMs.ToString("0.0", CultureInfo.InvariantCulture)} ms" : "완료", perfJson).ConfigureAwait(false);
        if (failure is null)
        {
            perf = perfStep?.Perf ?? PerfResult.ParseFile(perfJson);
            project.Headline.InferenceTimeMs = perf?.InferenceTimeMs;
            project.Headline.WeightBytes = File.Exists(Path.Combine(codeDir, "quantized_network.bin")) ? new FileInfo(Path.Combine(codeDir, "quantized_network.bin")).Length : null;
            project.Headline.BuiltAt = DateTimeOffset.Now;
            project.Headline.NeedsRebuild = false;
        }
        return Finish(results, perf, boardBuilt);
    }

    /// <summary>Reads the results of an earlier simulation from <c>out\result\&lt;network&gt;\&lt;image&gt;\</c>.</summary>
    public static IReadOnlyList<SimImageResult> LoadResults(string networkResultDir)
    {
        var list = new List<SimImageResult>();
        if (!Directory.Exists(networkResultDir))
            return list;
        foreach (var dir in Directory.EnumerateDirectories(networkResultDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var text = Directory.EnumerateFiles(dir, "*_result.txt").FirstOrDefault();
            var image = Directory.EnumerateFiles(dir, "*_result.png").FirstOrDefault();
            list.Add(new SimImageResult(dir, text, image, text is null ? null : SimResult.ParseFile(text)));
        }
        return list;
    }

    private static string Describe(IReadOnlyList<SimImageResult> results)
    {
        var first = results.FirstOrDefault()?.Parsed;
        return first switch
        {
            { Kind: SimResultKind.Detection } => results.Count == 1 ? $"{first.Detections.Count}개 검출" : $"사진 {results.Count}장",
            { Kind: SimResultKind.Classification } => $"{first.ClassLabel}",
            _ => $"사진 {results.Count}장",
        };
    }

    private static string CompatText(object? compatible, object? total) =>
        Equals(compatible?.ToString(), total?.ToString()) ? $"레이어 {total}개 모두 NPU에서 실행됩니다" : $"레이어 {compatible} / {total}개 호환";

    private static NetworkType TypeOf(string task) => task switch
    {
        "obj" => NetworkType.Detection,
        "class" => NetworkType.Classification,
        _ => NetworkType.Unknown,
    };

    private static string Nums(double[]? values) => values is null ? "" : string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    private static string HashOf(string? file) => file is not null && File.Exists(file) ? FileStager.Sha256Of(file) : "";

    private static string Key(string name, params object?[] parts)
    {
        var text = name + "|" + string.Join("|", parts.Select(p => p switch
        {
            null => "",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => p.ToString(),
        }));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }
}
