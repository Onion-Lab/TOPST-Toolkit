using System.Text.Json;
using TopstToolkit.Engine.Commands;
using TopstToolkit.Engine.Runner;

namespace TopstToolkit.Engine.Helpers;

/// <summary>A helper run: its process result plus the JSON it wrote (null when it wrote none).</summary>
public sealed record HelperResult<T>(ToolRunResult Run, T? Value) where T : class;

/// <summary>
/// Runs our Python helpers (design §4.7). Each helper takes <c>--toolkit &lt;abs&gt; --out &lt;json&gt;</c>,
/// writes its answer to the JSON file (stdout is left to the toolkit's own progress output) and prints
/// <c>TOPST helper. done.</c> at the end.
/// </summary>
public sealed class HelperClient
{
    private readonly EngineLayout _layout;
    private readonly ToolRunner _runner;

    public HelperClient(EngineLayout layout, ToolRunner runner)
    {
        _layout = layout;
        _runner = runner;
    }

    public Task<HelperResult<EngineCheckReport>> EngineCheckAsync(ToolRunRequest request, CancellationToken ct = default) =>
        RunAsync<EngineCheckReport>("engine_check", "엔진 점검", request, [], ct);

    public Task<HelperResult<EnlightInfo>> ReadEnlightAsync(string enlightPath, ToolRunRequest request, CancellationToken ct = default) =>
        RunAsync<EnlightInfo>("read_enlight", "네트워크 정보 읽기", request, ["--model", Path.GetFullPath(enlightPath)], ct);

    /// <summary>ONNX only. Runs in its own process because onnx and the SDK cannot share one.</summary>
    public Task<HelperResult<ModelInspection>> InspectModelAsync(string onnxPath, ToolRunRequest request, CancellationToken ct = default) =>
        RunAsync<ModelInspection>("inspect_model", "모델 분석", request, ["--model", Path.GetFullPath(onnxPath)], ct);

    /// <summary>
    /// Cuts an exported YOLOv8 model at its head and writes the grid file: <c>&lt;stem&gt;_extracted.onnx</c> and
    /// <c>&lt;stem&gt;.bin</c> in <paramref name="outputDir"/>. Runs without the SDK, like model inspection.
    /// </summary>
    public Task<HelperResult<YoloPreparation>> PrepareYoloAsync(string onnxPath, string outputDir, string stem, ToolRunRequest request, CancellationToken ct = default) =>
        RunAsync<YoloPreparation>("prepare_yolo", "YOLO 모델 준비", request,
            ["--model", Path.GetFullPath(onnxPath), "--output-dir", Path.GetFullPath(outputDir), "--stem", stem], ct);

    /// <summary>
    /// ONNX only: which operators the NPU cannot run (<paramref name="layersJson"/> = the incompatible layers of the
    /// compatibility table, or null), what is behind them, where the model can be cut, and the part worth drawing.
    /// </summary>
    public Task<HelperResult<ModelGraphReport>> AnalyzeGraphAsync(string onnxPath, string? layersJson, int depth, ToolRunRequest request, CancellationToken ct = default)
    {
        var args = new List<string> { "--model", Path.GetFullPath(onnxPath), "--depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (layersJson is not null)
            args.AddRange(["--layers", Path.GetFullPath(layersJson)]);
        return RunAsync<ModelGraphReport>("graph_model", "모델 그래프 분석", request, args, ct);
    }

    /// <summary>
    /// Cuts an ONNX model at the named operators: <c>&lt;stem&gt;_extracted.onnx</c> in <paramref name="outputDir"/>
    /// (plus <c>&lt;stem&gt;.bin</c> when the cut is the head of an exported YOLOv8).
    /// </summary>
    public Task<HelperResult<ModelCutReport>> CutModelAsync(string onnxPath, IReadOnlyList<string> nodes, string outputDir, string stem, ToolRunRequest request, CancellationToken ct = default)
    {
        var args = new List<string> { "--model", Path.GetFullPath(onnxPath), "--output-dir", Path.GetFullPath(outputDir), "--stem", stem, "--nodes" };
        args.AddRange(nodes);
        return RunAsync<ModelCutReport>("cut_model", "모델 자르기", request, args, ct);
    }

    /// <summary>
    /// Writes the C code for the part of an ONNX model behind the cut operators (design §5.8, stage 나) to
    /// <paramref name="cOut"/>. The compiled network is matched by its <c>network.h</c> and by the layer names of the
    /// quantized network (<paramref name="layersJson"/>: the answer of <see cref="ReadEnlightAsync"/>).
    /// </summary>
    public Task<HelperResult<TailGeneration>> GenerateTailAsync(string onnxPath, IReadOnlyList<string> nodes, string networkHeader,
        string layersJson, string cOut, string title, ToolRunRequest request, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "--model", Path.GetFullPath(onnxPath), "--network-h", Path.GetFullPath(networkHeader), "--layers-json", Path.GetFullPath(layersJson),
            "--c-out", Path.GetFullPath(cOut), "--title", title, "--nodes",
        };
        args.AddRange(nodes);
        return RunAsync<TailGeneration>("gen_tail", "잘라낸 구간 코드 만들기", request, args, ct);
    }

    /// <summary>
    /// The part behind the cut computed on the PC from the NPU outputs of INT8 dumps: one output folder per dump folder,
    /// <c>&lt;index&gt;.bin</c> (float32) per output. What the generated board code has to compute for the same pictures.
    /// </summary>
    public Task<HelperResult<TailEvaluation>> EvaluateTailAsync(string onnxPath, IReadOnlyList<string> nodes, string networkHeader,
        IReadOnlyList<string> dumpDirs, IReadOnlyList<string> outDirs, ToolRunRequest request, CancellationToken ct = default)
    {
        var args = new List<string> { "--model", Path.GetFullPath(onnxPath), "--network-h", Path.GetFullPath(networkHeader), "--dump" };
        args.AddRange(dumpDirs.Select(Path.GetFullPath));
        args.Add("--out-dir");
        args.AddRange(outDirs.Select(Path.GetFullPath));
        args.Add("--nodes");
        args.AddRange(nodes);
        return RunAsync<TailEvaluation>("eval_tail", "잘라낸 구간 계산 (PC)", request, args, ct);
    }

    /// <summary>
    /// One model of the model zoo to ONNX (design §5.9), exactly as the review exported it: <paramref name="recipe"/> is
    /// the catalog's export entry. Runs with the zoo component's packages first (<paramref name="site"/>), never with the
    /// SDK. Without <paramref name="weights"/> the model gets random weights (the review's structure-only export).
    /// </summary>
    public Task<HelperResult<ZooExportReport>> ExportZooModelAsync(ZooExportRecipe recipe, string site, string? weights, string onnxOut,
        ToolRunRequest request, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "--site", Path.GetFullPath(site), "--config", Path.Combine(Path.GetFullPath(request.WorkingDirectory), "ultralytics"),
            "--lib", recipe.Library, "--name", recipe.Name, "--task", recipe.Task,
            "--size", recipe.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), "--onnx", Path.GetFullPath(onnxOut),
        };
        if (recipe.Plain)
            args.Add("--plain");
        if (recipe.Relu6)
            args.Add("--relu6");
        if (weights is not null)
        {
            args.AddRange(["--weights", Path.GetFullPath(weights)]);
            if (recipe.WeightsTag is { Length: > 0 } tag && recipe.Library == "torchvision")
                args.AddRange(["--weights-tag", tag]);
        }
        return RunAsync<ZooExportReport>("zoo_export", "모델 모음 내보내기", request, args, ct);
    }

    /// <summary>
    /// The estimated NPU time on the TOPST AI-G board (design §11, 2026-11-28): the simulator's value (<paramref name="perfJson"/>)
    /// with what the board does differently, from the simulator's timeline and the model's first convolution. Without one
    /// of them that part is left out and the answer says so. Runs without the SDK.
    /// </summary>
    public Task<HelperResult<BoardTimeEstimate>> BoardTimeAsync(string perfJson, string coefficients, string? timeline, string? model,
        ToolRunRequest request, CancellationToken ct = default)
    {
        var args = new List<string> { "--perf", Path.GetFullPath(perfJson), "--coefficients", Path.GetFullPath(coefficients) };
        if (timeline is not null && File.Exists(timeline))
            args.AddRange(["--timeline", Path.GetFullPath(timeline)]);
        if (model is not null && File.Exists(model))
            args.AddRange(["--model", Path.GetFullPath(model)]);
        return RunAsync<BoardTimeEstimate>("board_time", "보드 시간 보정", request, args, ct);
    }

    public async Task<HelperResult<T>> RunAsync<T>(string helper, string title, ToolRunRequest request, IReadOnlyList<string> arguments, CancellationToken ct)
        where T : class
    {
        Directory.CreateDirectory(request.WorkingDirectory);
        var outJson = Path.Combine(request.WorkingDirectory, helper + ".json");
        if (File.Exists(outJson))
            File.Delete(outJson);

        var args = new List<string> { "--toolkit", _layout.ToolkitRoot, "--out", outJson };
        args.AddRange(arguments);
        var invocation = new ToolInvocation(ToolKind.Helper, _layout.HelperScript(helper), args, title) { HelperName = helper };
        var run = await _runner.RunAsync(invocation, request, null, ct).ConfigureAwait(false);

        T? value = null;
        if (File.Exists(outJson))
        {
            await using var stream = File.OpenRead(outJson);
            value = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        return new HelperResult<T>(run, value);
    }
}
