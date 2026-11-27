using System.Globalization;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine.Commands;
using TopstToolkit.Engine.Errors;
using TopstToolkit.Engine.Helpers;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.Cli;

/// <summary>Expected outcome of a recipe, from the Windows verification runs (feature/09 §3.3–3.4).</summary>
public sealed record Expectation(string Description, Func<RecipeOutcome, bool> Check);

public sealed class RecipeOutcome
{
    public List<StepResult> Steps { get; } = [];
    public StepResult? Simulation { get; set; }
    public StepResult? Perf { get; set; }
    public StepResult? DirectConversion { get; set; }
    public string? EnlightType { get; set; }
    public IReadOnlyList<int>? InputShape { get; set; }
    public ModelInspection? Inspection { get; set; }
    public string? DumpRoot { get; set; }
    public string? NetSo { get; set; }

    public IEnumerable<string> InspectedOutputShapes => Inspection?.Outputs.Select(t => t.ShapeText) ?? [];

    public IReadOnlyList<Detection> Detections => Simulation?.SimResults.FirstOrDefault()?.Parsed?.Detections ?? [];
    public bool Detected(string label, double minScore) => Detections.Any(d => d.Label == label && d.ScorePercent >= minScore);
}

/// <summary>
/// The three verified sample flows (feature/02 recipes A, B, E), driven through the engine code instead of shell scripts.
/// Models come from the original toolkit folder; nothing is bundled (D6).
/// </summary>
public sealed class Recipes
{
    private readonly EngineSession _session;
    private readonly string _modelsDir;
    private readonly Action<string> _log;

    public Recipes(EngineSession session, string modelsDir, Action<string> log)
    {
        _session = session;
        _modelsDir = modelsDir;
        _log = log;
    }

    /// <summary>The M0 completion set (design §8).</summary>
    public static readonly string[] Names = ["mbv2", "ssd", "yolo"];

    /// <summary>The other families design §8 lists for checking during M0 (feature/02 recipes C, D, E, G). Never run before 2026-11-20.</summary>
    public static readonly string[] FamilyNames = ["yolo3", "yolo6", "yolo7", "yolo8", "yolox", "tflite", "darknet", "npy"];

    public Task<(RecipeOutcome Outcome, IReadOnlyList<Expectation> Expectations)> RunAsync(string name, CancellationToken ct) => name switch
    {
        "mbv2" => MobilenetV2Async(ct),
        "ssd" => SsdAsync(ct),
        "yolo" => Yolov5Async(ct),
        "tflite" => TfliteSsdAsync(ct),
        "darknet" => DarknetYolo4Async(ct),
        "npy" => NpyAsync(ct),
        _ when YoloSpecs.TryGetValue(name, out var spec) => PytorchYoloAsync(name, spec, ct),
        _ => throw new ArgumentException($"unknown recipe '{name}' (use {string.Join(", ", Names.Concat(FamilyNames))})"),
    };

    // Recipe A: classification, mobilenetv2-10.onnx (feature/02 §3 A, run with --type class as verified).
    private async Task<(RecipeOutcome, IReadOnlyList<Expectation>)> MobilenetV2Async(CancellationToken ct)
    {
        var p = NewProject("mbv2");
        var steps = _session.For(p);
        var o = new RecipeOutcome();

        var model = FileStager.StageFile(Model("mobilenetv2-10.onnx"), p.ModelInputDir);
        var labels = FileStager.StageFile(Path.Combine(_session.Layout.LabelsDir, "imagenet.label"), p.LabelsInputDir);
        StageCalibration(p);
        var test = StageTestImage(p);
        await InspectAsync(steps, o, model.Staged, ct);

        var fp32 = Path.Combine(p.NetworksDir, "mobilenetv2-10.enlight");
        var int8 = Path.Combine(p.NetworksDir, "mobilenetv2-10_quantized.enlight");
        if (!await Step(o, steps.ConvertAsync(new ConverterOptions
            {
                Model = model.Staged, Output = fp32, Type = NetworkType.Classification,
                CompatibilityLogRoot = p.CompatDir, DatasetRoot = p.CalibrationDir, EnableTrack = true, NumImages = 100,
                Mean = [0.485, 0.456, 0.406], Std = [0.229, 0.224, 0.225],
                NumClass = 1000, ClassLabels = labels.Staged,
            }, onEvent: Progress, ct: ct)))
            return (o, MbV2Expectations);

        await FinishAsync(o, steps, p, int8, quantizeFrom: fp32, thConf: null, thIou: null, letterbox: false, test, ct);
        return (o, MbV2Expectations);
    }

    private static readonly Expectation[] MbV2Expectations =
    [
        new("모든 단계 성공", o => o.Steps.All(s => s.Success) && o.Perf is not null),
        new("호환 레이어 66/66", o => o.Steps[0].Compatibility is { Compatible: 66, Incompatible: 0 }),
        new("dog.png → class 249 (malamute)", o => o.Simulation?.SimResults.FirstOrDefault()?.Parsed?.ClassNumber == 249),
        new("성능 추정 1.197 ms", o => o.Perf?.Perf?.InferenceTimeMs is { } t && Math.Abs(t - 1.197) < 0.001),
        new("read_enlight: 종류 class", o => o.EnlightType == "class"),
        new("network.h 입력 텐서 1×3×224×224", o => o.InputShape?.SequenceEqual([1, 3, 224, 224]) == true),
        new("inspect_model: 입력 [batch_size, 3, 224, 224]", o => o.Inspection?.Inputs.SingleOrDefault()?.ShapeText == "[batch_size, 3, 224, 224]"),
    ];

    // Recipe B: SSD with box decoding in the ONNX → direct conversion fails, two-step conversion works.
    private async Task<(RecipeOutcome, IReadOnlyList<Expectation>)> SsdAsync(CancellationToken ct)
    {
        var p = NewProject("ssd");
        var steps = _session.For(p);
        var o = new RecipeOutcome();

        var model = FileStager.StageFile(Model("mb2-ssd-lite.onnx"), p.ModelInputDir);
        var anchor = FileStager.StageFile(Model("mb2-ssd-lite.anchor"), p.ModelInputDir);
        var labels = FileStager.StageFile(Path.Combine(_session.Layout.LabelsDir, "voc.label"), p.LabelsInputDir);
        StageCalibration(p);
        var test = StageTestImage(p);
        await InspectAsync(steps, o, model.Staged, ct);

        ConverterOptions Detection(string input, string output) => new()
        {
            Model = input, Output = output, Type = NetworkType.Detection,
            CompatibilityLogRoot = p.CompatDir, DatasetRoot = p.CalibrationDir, EnableTrack = true, NumImages = 100,
            PostProcessFile = anchor.Staged, Logistic = "softmax", NumClass = 20, ClassLabels = labels.Staged,
            Mean = [0.498, 0.498, 0.498], Std = [0.502, 0.502, 0.502], Variance = [0.1, 0.2],
        };

        // 1) direct conversion: expected to stop on 4 incompatible layers (design §5.4 step 1)
        _log("  [직접 변환] 비호환 레이어 검출 확인");
        o.DirectConversion = await steps.ConvertAsync(Detection(model.Staged, Path.Combine(p.NetworksDir, "direct", "mb2-ssd-lite.enlight")),
            "convert-direct", Progress, ct);
        Report(o.DirectConversion);

        // 2) two-step conversion: cut before the box decoding, then attach the SDK post-process and calibrate
        var cut = Path.Combine(p.NetworksDir, "mb2-ssd-lite_wo_box_decode_score.enlight");
        var fp32 = Path.Combine(p.NetworksDir, "mb2-ssd-lite.enlight");
        var int8 = Path.Combine(p.NetworksDir, "mb2-ssd-lite_quantized.enlight");
        if (!await Step(o, steps.ConvertAsync(new ConverterOptions
            {
                Model = model.Staged, Output = cut, Type = NetworkType.Unknown,
                CompatibilityLogRoot = p.CompatDir, DatasetRoot = p.CalibrationDir,
                ForceOutput = ["Concat_0___Concat", "Concat_1___Concat_1"],
            }, "convert-cut", Progress, ct)))
            return (o, SsdExpectations);
        if (!await Step(o, steps.ConvertAsync(Detection(cut, fp32), "convert-post", Progress, ct)))
            return (o, SsdExpectations);

        await FinishAsync(o, steps, p, int8, quantizeFrom: fp32, thConf: 0.4, thIou: 0.45, letterbox: false, test, ct);
        return (o, SsdExpectations);
    }

    private static readonly Expectation[] SsdExpectations =
    [
        new("inspect_model: 출력 scores [1, 3000, 21] · boxes [1, 3000, 4], Softmax·Exp 포함",
            o => o.InspectedOutputShapes.SequenceEqual(["[1, 3000, 21]", "[1, 3000, 4]"])
                 && o.Inspection!.Operators.ContainsKey("Softmax") && o.Inspection.Operators.ContainsKey("Exp")),
        new("직접 변환은 비호환 레이어 4개로 멈춤", o => o.DirectConversion?.Error?.Code == ToolErrorCode.IncompatibleLayers
                                                && o.DirectConversion.Compatibility is { Incompatible: 4 }),
        new("비호환 레이어 이름 (Softmax, Exp, Concat×2)", o => o.DirectConversion?.Compatibility?.Layers.Select(l => l.Type).OrderBy(t => t).SequenceEqual(["Concat", "Concat", "Exp", "Softmax"]) == true),
        new("2단계 변환 이후 모든 단계 성공", o => o.Steps.All(s => s.Success) && o.Perf is not null),
        new("INT8 검출 bicycle·car·cat", o => o.Detected("bicycle", 60) && o.Detected("car", 80) && o.Detected("cat", 40)),
    ];

    // Recipe E: PyTorch YOLOv5m (extracted ONNX + grid .bin), letterbox.
    private async Task<(RecipeOutcome, IReadOnlyList<Expectation>)> Yolov5Async(CancellationToken ct)
    {
        var p = NewProject("yolo");
        var steps = _session.For(p);
        var o = new RecipeOutcome();

        var model = FileStager.StageFile(Model("yolov5m_extracted.onnx"), p.ModelInputDir);
        var grid = FileStager.StageFile(Model("yolov5m.bin"), p.ModelInputDir);
        var labels = FileStager.StageFile(Path.Combine(_session.Layout.LabelsDir, "coco_80.label"), p.LabelsInputDir);
        StageCalibration(p);
        var test = StageTestImage(p);
        await InspectAsync(steps, o, model.Staged, ct);

        var fp32 = Path.Combine(p.NetworksDir, "yolov5m.enlight");
        var int8 = Path.Combine(p.NetworksDir, "yolov5m_quantized.enlight");
        if (!await Step(o, steps.ConvertAsync(new ConverterOptions
            {
                Model = model.Staged, Output = fp32, Type = NetworkType.Detection,
                CompatibilityLogRoot = p.CompatDir, DatasetRoot = p.CalibrationDir, EnableTrack = true, NumImages = 100,
                PostProcessFile = grid.Staged, YoloVersion = "v5", NumClass = 80, ClassLabels = labels.Staged,
                Mean = [0, 0, 0], Std = [1, 1, 1], EnableLetterbox = true,
            }, onEvent: Progress, ct: ct)))
            return (o, YoloExpectations);

        await FinishAsync(o, steps, p, int8, quantizeFrom: fp32, thConf: 0.5, thIou: 0.5, letterbox: true, test, ct);
        return (o, YoloExpectations);
    }

    private static readonly Expectation[] YoloExpectations =
    [
        new("inspect_model: 검출 헤드 출력 3개 (80²·40²·20²)",
            o => o.InspectedOutputShapes.SequenceEqual(["[1, 255, 80, 80]", "[1, 255, 40, 40]", "[1, 255, 20, 20]"])),
        new("모든 단계 성공", o => o.Steps.All(s => s.Success) && o.Perf is not null),
        new("호환 레이어 273/273", o => o.Steps[0].Compatibility is { Compatible: 273, Incompatible: 0 }),
        new("INT8 검출 bicycle·truck·dog", o => o.Detected("bicycle", 50) && o.Detected("truck", 50) && o.Detected("dog", 70)),
        new("성능 추정 30.017 ms", o => o.Perf?.Perf?.InferenceTimeMs is { } t && Math.Abs(t - 30.017) < 0.001),
    ];

    // Recipe E for the other PyTorch YOLO versions: values of run_example_pytorch_yolo_onnx.sh (feature/02 §3 E table).
    private sealed record YoloSpec(string Net, string Version, int? DflRegMax, string? OutputOrder, double[] Std);

    private static readonly Dictionary<string, YoloSpec> YoloSpecs = new()
    {
        ["yolo3"] = new("yolov3", "v3", null, null, [1, 1, 1]),
        ["yolo6"] = new("yolov6m", "v6", 17, "cl", [1, 1, 1]),
        ["yolo7"] = new("yolov7", "v7", null, null, [1, 1, 1]),
        ["yolo8"] = new("yolov8m", "v8", 16, "cl", [1, 1, 1]),
        ["yolox"] = new("yolox_m", "x", null, "clo", [0.00392156, 0.00392156, 0.00392156]),
    };

    private async Task<(RecipeOutcome, IReadOnlyList<Expectation>)> PytorchYoloAsync(string name, YoloSpec spec, CancellationToken ct)
    {
        var p = NewProject(name);
        var steps = _session.For(p);
        var o = new RecipeOutcome();
        Expectation[] expectations =
        [
            new("모든 단계 성공", r => r.Steps.All(s => s.Success) && r.Perf is not null),
            new("호환 레이어 전부", r => r.Steps.FirstOrDefault()?.Compatibility is { Incompatible: 0, Total: > 0 }),
            new("INT8 검출에 dog 포함", r => r.Detected("dog", 50)),
        ];

        var model = FileStager.StageFile(Model(spec.Net + "_extracted.onnx"), p.ModelInputDir);
        var grid = FileStager.StageFile(Model(spec.Net + ".bin"), p.ModelInputDir);
        var labels = FileStager.StageFile(Path.Combine(_session.Layout.LabelsDir, "coco_80.label"), p.LabelsInputDir);
        StageCalibration(p);
        var test = StageTestImage(p);
        await InspectAsync(steps, o, model.Staged, ct);

        var fp32 = Path.Combine(p.NetworksDir, spec.Net + ".enlight");
        var int8 = Path.Combine(p.NetworksDir, spec.Net + "_quantized.enlight");
        if (!await Step(o, steps.ConvertAsync(new ConverterOptions
            {
                Model = model.Staged, Output = fp32, Type = NetworkType.Detection,
                CompatibilityLogRoot = p.CompatDir, DatasetRoot = p.CalibrationDir, EnableTrack = true, NumImages = 100,
                PostProcessFile = grid.Staged, YoloVersion = spec.Version, DflRegMax = spec.DflRegMax, OutputOrder = spec.OutputOrder,
                NumClass = 80, ClassLabels = labels.Staged, Mean = [0, 0, 0], Std = spec.Std, EnableLetterbox = true,
            }, onEvent: Progress, ct: ct)))
            return (o, expectations);

        await FinishAsync(o, steps, p, int8, quantizeFrom: fp32, thConf: 0.5, thIou: 0.5, letterbox: true, test, ct);
        return (o, expectations);
    }

    // Recipe C: TFLite SSD with its own post-process layer (no anchor file), thresholds from the .tflite NMS layer.
    private async Task<(RecipeOutcome, IReadOnlyList<Expectation>)> TfliteSsdAsync(CancellationToken ct)
    {
        const string net = "lite-model_ssd_mobilenet_v1_100_320_fp32_nms_1";
        var p = NewProject("tflite");
        var steps = _session.For(p);
        var o = new RecipeOutcome();
        Expectation[] expectations =
        [
            new("모든 단계 성공", r => r.Steps.All(s => s.Success) && r.Perf is not null),
            new("INT8 검출 1개 이상", r => r.Detections.Count > 0),
        ];

        var model = FileStager.StageFile(Model(net + ".tflite"), p.ModelInputDir);
        StageCalibration(p);
        var test = StageTestImage(p);

        var fp32 = Path.Combine(p.NetworksDir, net + ".enlight");
        var int8 = Path.Combine(p.NetworksDir, net + "_quantized.enlight");
        if (!await Step(o, steps.ConvertAsync(new ConverterOptions
            {
                Model = model.Staged, Output = fp32, Type = NetworkType.Detection,
                CompatibilityLogRoot = p.CompatDir, DatasetRoot = p.CalibrationDir, EnableTrack = true, NumImages = 100,
                NumClass = 90, Mean = [0.5, 0.5, 0.5], Std = [0.5, 0.5, 0.5],
            }, onEvent: Progress, ct: ct)))
            return (o, expectations);

        await FinishAsync(o, steps, p, int8, quantizeFrom: fp32, thConf: 0.3, thIou: 0.6, letterbox: false, test, ct);
        return (o, expectations);
    }

    // Recipe D: Darknet cfg + weights, thresholds from yolov4.cfg.
    private async Task<(RecipeOutcome, IReadOnlyList<Expectation>)> DarknetYolo4Async(CancellationToken ct)
    {
        var p = NewProject("darknet");
        var steps = _session.For(p);
        var o = new RecipeOutcome();
        Expectation[] expectations =
        [
            new("모든 단계 성공", r => r.Steps.All(s => s.Success) && r.Perf is not null),
            new("호환 레이어 전부", r => r.Steps.FirstOrDefault()?.Compatibility is { Incompatible: 0, Total: > 0 }),
            new("INT8 검출에 dog 포함", r => r.Detected("dog", 50)),
        ];

        var cfg = FileStager.StageFile(Model("yolov4.cfg"), p.ModelInputDir);
        var weights = FileStager.StageFile(Model("yolov4.weights"), p.ModelInputDir);
        var labels = FileStager.StageFile(Path.Combine(_session.Layout.LabelsDir, "coco_80.label"), p.LabelsInputDir);
        StageCalibration(p);
        var test = StageTestImage(p);

        var fp32 = Path.Combine(p.NetworksDir, "yolov4.enlight");
        var int8 = Path.Combine(p.NetworksDir, "yolov4_quantized.enlight");
        if (!await Step(o, steps.ConvertAsync(new ConverterOptions
            {
                Model = cfg.Staged, Weight = weights.Staged, Output = fp32, Type = NetworkType.Detection,
                CompatibilityLogRoot = p.CompatDir, DatasetRoot = p.CalibrationDir, EnableTrack = true, NumImages = 100,
                NumClass = 80, ClassLabels = labels.Staged, Mean = [0, 0, 0], Std = [1, 1, 1],
            }, onEvent: Progress, ct: ct)))
            return (o, expectations);

        await FinishAsync(o, steps, p, int8, quantizeFrom: fp32, thConf: 0.7, thIou: 0.213, letterbox: false, test, ct);
        return (o, expectations);
    }

    // Recipe G: 4-channel non-image input from .npy files (CustomNPY), with the NPU dump.
    private async Task<(RecipeOutcome, IReadOnlyList<Expectation>)> NpyAsync(CancellationToken ct)
    {
        var p = NewProject("npy");
        var steps = _session.For(p);
        var o = new RecipeOutcome();
        Expectation[] expectations =
        [
            new("모든 단계 성공", r => r.Steps.All(s => s.Success) && r.Perf is not null),
            new("PC 추론 결과가 텐서 (unknown 타입)", r => r.Simulation?.SimResults.FirstOrDefault()?.Parsed?.Kind == SimResultKind.Tensor),
            new("NPU 입력 덤프 input.ia.bin", r => r.DumpRoot is { } d && Directory.Exists(d)
                                                 && Directory.EnumerateFiles(d, "input.ia.bin", SearchOption.AllDirectories).Any()),
        ];

        var exampleDir = Path.Combine(Path.GetDirectoryName(_modelsDir)!, "test_example", "n_channel");
        var model = FileStager.StageFile(Path.Combine(exampleDir, "ch4_n_channel.onnx"), p.ModelInputDir);
        var data = FileStager.StageDataFiles([Path.Combine(exampleDir, "npy_4ch")], p.CalibrationDir, FileStager.NpyExtensions);
        _log($"  [calib] npy {data.Files.Count}개 반입: {string.Join(", ", data.Files.Select(f => Path.GetFileName(f.Staged)))}");
        var input = data.Files.FirstOrDefault(f => Path.GetFileName(f.Staged).StartsWith("input_dump", StringComparison.Ordinal)) ?? data.Files.First();
        await InspectAsync(steps, o, model.Staged, ct);

        var fp32 = Path.Combine(p.NetworksDir, "ch4_n_channel.enlight");
        var int8 = Path.Combine(p.NetworksDir, "ch4_n_channel_quantized.enlight");
        if (!await Step(o, steps.ConvertAsync(new ConverterOptions
            {
                Model = model.Staged, Output = fp32, Type = NetworkType.Unknown,
                CompatibilityLogRoot = p.CompatDir, DatasetRoot = p.CalibrationDir, Dataset = "CustomNPY", EnableTrack = true,
                Variance = [0.1, 0.2],
            }, onEvent: Progress, ct: ct)))
            return (o, expectations);

        o.DumpRoot = p.DumpDir;
        await FinishAsync(o, steps, p, int8, quantizeFrom: fp32, thConf: null, thIou: null, letterbox: false, input, ct, dump: true);
        return (o, expectations);
    }

    /// <summary>quantize → read_enlight → compile → simulate (INT8) → perf.</summary>
    private async Task FinishAsync(RecipeOutcome o, StepRunner steps, ProjectWorkspace p, string int8, string quantizeFrom,
        double? thConf, double? thIou, bool letterbox, StagedFile test, CancellationToken ct, bool dump = false)
    {
        if (!await Step(o, steps.QuantizeAsync(new QuantizerOptions { Model = quantizeFrom, Output = int8 }, onEvent: Progress, ct: ct)))
            return;

        var info = await steps.ReadEnlightAsync(int8, ct);
        o.EnlightType = info.Value?.Type;
        _log($"  [read_enlight] type={info.Value?.Type} quantized={info.Value?.IsQuantized} tracked={info.Value?.IsTracked} " +
             $"input=[{string.Join(",", info.Value?.InputSize ?? [])}] layers={info.Value?.Layers.Count} ({Seconds(info.Run.Elapsed)})");

        var compile = await StepValue(o, steps.CompileAsync(new CompilerOptions { Model = int8, DumpRoot = p.CodeDir, ThConf = thConf, ThIou = thIou }, onEvent: Progress, ct: ct));
        if (compile is null)
            return;
        var header = NetworkHeaderSummary.ParseFile(Path.Combine(compile.Output!, "network.h"));
        o.InputShape = header?.Input?.Shape;
        if (_session.Layout.FindCrossGcc() is not null)
        {
            var netSo = await steps.BuildBoardLibraryAsync(compile.Output!, ct: ct);
            o.NetSo = netSo.Success ? netSo.Output : null;
            _log(netSo.Success
                ? $"  [netso] 성공 · net.so {new FileInfo(netSo.Output!).Length:N0} B"
                : $"  [netso] 실패 — {netSo.Error!.Title} | {netSo.Error.Evidence}");
        }
        _log($"  [compile] work-buffer {(compile.WorkBufferMiB is { } wb ? wb.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")} MiB, buffers total {compile.Buffers?.Total:N0} B, " +
             $"input tensor {header?.Input?.Name} [{string.Join("x", header?.Input?.Shape ?? [])}], files {compile.CreatedFiles.Count}");

        o.Simulation = await StepValue(o, steps.SimulateAsync(new SimulatorOptions
        {
            Model = int8, Inputs = test.Staged, ResultRoot = p.ResultDir, DumpRoot = p.DumpDir,
            ThConf = thConf, ThIou = thIou, EnableLetterbox = letterbox, Dump = dump,
        }, onEvent: Progress, ct: ct));
        foreach (var r in o.Simulation?.SimResults ?? [])
            _log("  [result] " + Describe(r.Parsed));

        o.Perf = await StepValue(o, steps.PerfAsync(new PerfOptions
        {
            NpuCmdHeader = Path.Combine(compile.Output!, "npu_cmd.h"),
            NpuConfig = _session.Layout.ExampleNpuConfig,
            Output = Path.Combine(p.PerfDir, Path.GetFileNameWithoutExtension(int8) + "_performance.json"),
        }, onEvent: Progress, ct: ct));
        if (o.Perf?.Perf is { } perf)
            _log($"  [perf] {perf.InferenceTimeMs.ToString(CultureInfo.InvariantCulture)} ms (예제 클럭 — 보드 기준 아님)");
    }

    private async Task InspectAsync(StepRunner steps, RecipeOutcome o, string onnx, CancellationToken ct)
    {
        var r = await steps.InspectModelAsync(onnx, ct);
        o.Inspection = r.Value;
        if (r.Value is not { Ok: true } m)
        {
            _log($"  [inspect_model] 실패 (exit {r.Run.ExitCode}): {string.Join("; ", r.Value?.Problems ?? [])}");
            return;
        }
        _log($"  [inspect_model] opset {string.Join(",", m.Opsets.Select(s => s.Version))} · 노드 {m.NodeCount} · 파라미터 {m.ParameterCount:N0} " +
             $"· 입력 {string.Join(", ", m.Inputs.Select(t => t.Name + t.ShapeText))} · 출력 {string.Join(", ", m.Outputs.Select(t => t.ShapeText))} ({Seconds(r.Run.Elapsed)})");
    }

    private async Task<bool> Step(RecipeOutcome o, Task<StepResult> task) => await StepValue(o, task) is not null;

    private async Task<StepResult?> StepValue(RecipeOutcome o, Task<StepResult> task)
    {
        var result = await task;
        o.Steps.Add(result);
        Report(result);
        return result.Success ? result : null;
    }

    private void Report(StepResult r)
    {
        const double MiB = 1024.0 * 1024.0;
        var status = r.Success ? "성공" : $"실패 — {r.Error!.Title} ({r.Error.Code})";
        _log($"  [{r.Step}] {status} · {Seconds(r.Run.Elapsed)} · 최대 작업 집합 {r.Run.PeakWorkingSetBytes / MiB:N0} MB" +
             $" (커밋 {r.Run.PeakCommitBytes / MiB:N0} MB) · exit {r.Run.ExitCode}");
        if (!r.Success && r.Error!.Evidence is { } evidence)
            _log($"      근거: {evidence}");
        if (r.Compatibility is { } c)
            _log($"      호환성: {c.Compatible}/{c.Total} 호환, 비호환 {c.Incompatible}" +
                 (c.Layers.Count > 0 ? " — " + string.Join(", ", c.Layers.Select(l => $"{l.Type} {l.Name}")) : ""));
    }

    private ToolStage? _lastStage;
    private int _lastPercent = -1;

    private void Progress(ToolEvent e)
    {
        switch (e)
        {
            case StageEvent s when s.Stage != _lastStage:
                _lastStage = s.Stage;
                _lastPercent = -1;
                break;
            case ProgressEvent { Fraction: { } f } pe when pe.Stage is ToolStage.Calibration or ToolStage.Inference:
                var pct = (int)(f * 100);
                if (pct / 25 != _lastPercent / 25)
                {
                    _lastPercent = pct;
                    _log($"      {pe.Stage} {pe.Current}/{pe.Total}");
                }
                break;
            case StalledEvent st:
                _log($"      (응답 없음 {st.Silence.TotalSeconds:0}s)");
                break;
        }
    }

    private static string Describe(SimResult? r) => r switch
    {
        null => "(결과 없음)",
        { Kind: SimResultKind.Classification } => $"class {r.ClassNumber} {r.ClassLabel}",
        { Kind: SimResultKind.Detection } => r.Detections.Count == 0
            ? "검출 없음"
            : string.Join(", ", r.Detections.Select(d => $"{d.Label} {d.ScorePercent.ToString("0.0", CultureInfo.InvariantCulture)}%")),
        { Kind: SimResultKind.Tensor } => $"tensor {r.TensorShape}",
        _ => "(알 수 없는 형식)",
    };

    private static string Seconds(TimeSpan t) => $"{t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";

    private ProjectWorkspace NewProject(string name)
    {
        var p = _session.Workspace.CreateProject();
        _log($"[{name}] 프로젝트 {p.Root}");
        return p;
    }

    private string Model(string fileName)
    {
        var path = Path.Combine(_modelsDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Sample model not found (pass --models <toolkit>\\input_networks): {path}", path);
        return path;
    }

    private void StageCalibration(ProjectWorkspace p)
    {
        var r = FileStager.StageImages([_session.Layout.DefaultCalibrationDir], p.CalibrationDir);
        _log($"  [calib] {r.Files.Count}장 반입 (중복 {r.DuplicatesSkipped}, 미지원 {r.UnsupportedSkipped})");
    }

    private StagedFile StageTestImage(ProjectWorkspace p) =>
        FileStager.StageImages([_session.Layout.SampleImage], p.TestImagesDir).Files.Single();
}
