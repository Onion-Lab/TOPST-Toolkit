using System.Text.Json;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Presets;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine.Helpers;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.Core.Projects;

/// <param name="Guess">Family guess, already applied to the project.</param>
/// <param name="Companion">The extra file found next to the model (grid .bin, .anchor, .weights), staged path.</param>
/// <param name="Problems">What could not be done (for example the model could not be inspected).</param>
/// <param name="Cut">Set when the model was cut: on the way in at the head of a known detector, later where the user chose.</param>
/// <param name="Graph">The graph analysis made with the check (ONNX), for the screens that show it.</param>
public sealed record ModelImportResult(PresetGuess Guess, string? Companion, IReadOnlyList<string> Problems, ModelCutReport? Cut = null,
    ModelGraphReport? Graph = null);

/// <summary>What a long import is doing right now, and the output of the tools it runs. Both are called on a background thread.</summary>
public sealed record ImportProgress(Action<string>? Stage = null, Action<ToolEvent>? ToolOutput = null);

/// <summary>
/// Step 1 "모델": copies the user's file into the project under an ASCII name, inspects it, and — check first
/// (design §5.7) — asks the converter which layers the NPU cannot run. For ONNX the answer is placed on the graph of
/// the model: what is stuck behind those layers, where the model can be cut. A model that ends in the head of a known
/// detector (an exported YOLOv8, an SSD with its box maths attached) is cut there right away; any other cut is the
/// user's choice on the graph (<see cref="CutAsync"/>). The original file stays in the project so the cut can change.
/// </summary>
public sealed class ModelImporter(ProjectStore store, EngineSession? engine)
{
    public static readonly IReadOnlyList<string> ModelExtensions = [".onnx", ".tflite", ".cfg", ".enlight"];

    /// <summary>Files that belong to a model and survive when the model is brought in again from the project's own copy.</summary>
    private static readonly string[] CompanionExtensions = [".weights", ".anchor", ".bin"];

    private static readonly JsonSerializerOptions GraphJson = new() { WriteIndented = false };

    public static string? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".onnx" => "onnx",
        ".tflite" => "tflite",
        ".cfg" => "darknet",
        ".enlight" => "enlight",
        _ => null,
    };

    public async Task<ModelImportResult> ImportAsync(ProjectFile project, string sourcePath, CancellationToken ct = default, ImportProgress? progress = null)
    {
        var format = FormatOf(sourcePath) ?? throw new NotSupportedException("지원하지 않는 모델 형식이에요. .onnx, .tflite, .cfg, .enlight 파일을 골라 주세요.");
        var folders = store.FoldersOf(project);
        var problems = new List<string>();

        // a new model replaces the old one (the source may be the copy inside the project when a model is brought in again)
        var fullSource = Path.GetFullPath(sourcePath);
        // brought in again from the copy inside the project: where the user's own file is does not change
        var fromProject = string.Equals(Path.GetDirectoryName(fullSource), Path.GetFullPath(folders.ModelInputDir), StringComparison.OrdinalIgnoreCase);
        var origin = fromProject && project.Model?.Source is { Length: > 0 } known ? known : fullSource;
        foreach (var old in Directory.EnumerateFiles(folders.ModelInputDir))
        {
            if (string.Equals(Path.GetFullPath(old), fullSource, StringComparison.OrdinalIgnoreCase))
                continue;
            if (fromProject && CompanionExtensions.Contains(Path.GetExtension(old), StringComparer.OrdinalIgnoreCase))
                continue; // its companion file is here too and nowhere else
            File.Delete(old);
        }
        var staged = FileStager.StageFile(sourcePath, folders.ModelInputDir);

        project.Model = new ProjectModel
        {
            Source = origin,
            Staged = store.ToRelative(project, staged.Staged),
            Sha256 = staged.Sha256,
            SizeBytes = staged.Size,
            Format = format,
        };

        // model facts (ONNX only for now; runs in its own process because onnx and the SDK cannot share one)
        if (format == "onnx" && engine is not null)
        {
            progress?.Stage?.Invoke("모델 구조를 읽는 중");
            project.Model.Facts = await InspectAsync(folders, staged.Staged, problems, ct).ConfigureAwait(false);
        }

        // check first: which layers can the NPU not run, and (ONNX) where are they on the graph
        ModelGraphReport? graph = null;
        if (format == "onnx" && engine is not null)
            graph = await CheckAsync(project, folders, staged.Staged, null, problems, progress, ct).ConfigureAwait(false);

        // the head of a known detector: cut there right away and let the toolkit's box maths take over
        ModelCutReport? cut = null;
        if (engine is not null && graph?.Proposal is { Decoder: not null, Nodes.Count: > 0 } proposal)
            cut = await CutStagedAsync(project, folders, staged.Staged, proposal.Nodes, "app", proposal.Decoder, problems, progress, ct).ConfigureAwait(false);

        var (guess, companion) = ApplySettings(project, folders, cut, Path.GetDirectoryName(fullSource)!, Path.GetFileNameWithoutExtension(sourcePath), problems);

        // a format without a graph is checked once its companion file is in place (Darknet needs its weights)
        if (format != "onnx" && engine is not null)
        {
            var weights = project.Model.Weights is { } w ? store.ToAbsolute(project, w) : null;
            await CheckAsync(project, folders, staged.Staged, weights, problems, progress, ct).ConfigureAwait(false);
        }

        if (project.Name is "새 프로젝트" or "")
            project.Name = Path.GetFileNameWithoutExtension(origin);
        project.Headline = new ProjectHeadline(); // a new model invalidates every earlier result
        project.Steps.Clear();
        store.Save(project);
        return new ModelImportResult(guess, companion, problems, cut, graph);
    }

    /// <summary>
    /// Cuts the project's model at the operators chosen on the graph: the model as it was brought in is cut again from
    /// scratch, the settings follow what the new outputs look like, and earlier build results no longer count.
    /// With no operators the project goes back to the model as it was brought in.
    /// </summary>
    public async Task<ModelImportResult> CutAsync(ProjectFile project, IReadOnlyList<string> nodes, CancellationToken ct = default, ImportProgress? progress = null)
    {
        var model = project.Model ?? throw new InvalidOperationException("프로젝트에 모델이 없어요.");
        if (engine is null)
            throw new InvalidOperationException("엔진이 아직 설치되지 않아 모델을 자를 수 없어요. 홈에서 설치할 수 있어요.");
        if (model.Format != "onnx")
            throw new NotSupportedException("그래프에서 자르는 것은 ONNX 모델에서만 됩니다.");
        var folders = store.FoldersOf(project);
        var problems = new List<string>();
        var original = store.ToAbsolute(project, model.Original ?? model.Staged);
        if (!File.Exists(original))
            throw new FileNotFoundException("프로젝트 안의 원본 모델을 찾지 못했어요. 모델을 다시 가져와 주세요.", original);

        ModelCutReport? cut = null;
        if (nodes.Count > 0)
        {
            var decoder = model.Check?.Proposal is { Decoder: not null } p && p.Nodes.ToHashSet().SetEquals(nodes) ? p.Decoder : null;
            cut = await CutStagedAsync(project, folders, original, nodes, "user", decoder, problems, progress, ct).ConfigureAwait(false);
            if (cut is null)
                return new ModelImportResult(PresetGuesser.Guess(model.Format, model.Facts), null, problems);
        }
        else if (model.Original is not null)
        {
            // back to the model as it came in
            var extracted = store.ToAbsolute(project, model.Staged);
            if (File.Exists(extracted) && !PathsEqual(extracted, original))
                File.Delete(extracted);
            model.Staged = model.Original;
            model.Original = null;
            model.Prepared = null;
            model.Cut = null;
            model.Sha256 = FileStager.Sha256Of(original);
            model.SizeBytes = new FileInfo(original).Length;
            model.Facts = await InspectAsync(folders, original, problems, ct).ConfigureAwait(false) ?? model.Facts;
        }

        var sourceDir = Path.GetDirectoryName(model.Source) is { Length: > 0 } dir && Directory.Exists(dir) ? dir : folders.ModelInputDir;
        var (guess, companion) = ApplySettings(project, folders, cut, sourceDir, Path.GetFileNameWithoutExtension(original), problems);
        project.Headline = new ProjectHeadline();
        project.Steps.Clear();
        store.Save(project);
        return new ModelImportResult(guess, companion, problems, cut);
    }

    /// <summary>
    /// Runs the check again for the model the project already holds (a project from before the check existed, or after
    /// an engine update). The staged model and the settings are left as they are.
    /// </summary>
    public async Task<ModelGraphReport?> CheckAgainAsync(ProjectFile project, CancellationToken ct = default, ImportProgress? progress = null)
    {
        var model = project.Model ?? throw new InvalidOperationException("프로젝트에 모델이 없어요.");
        if (engine is null)
            throw new InvalidOperationException("엔진이 아직 설치되지 않아 점검할 수 없어요. 홈에서 설치할 수 있어요.");
        var folders = store.FoldersOf(project);
        var problems = new List<string>();
        var path = store.ToAbsolute(project, model.Original ?? model.Staged);
        var weights = model.Weights is { } w ? store.ToAbsolute(project, w) : null;
        var graph = await CheckAsync(project, folders, path, weights, problems, progress, ct).ConfigureAwait(false);
        store.Save(project);
        return graph;
    }

    /// <summary>The graph analysis of the project's model: the file made with the check, or made again when it is gone. ONNX only.</summary>
    public async Task<ModelGraphReport?> LoadGraphAsync(ProjectFile project, CancellationToken ct = default)
    {
        if (project.Model is not { Format: "onnx" } model)
            return null;
        var folders = store.FoldersOf(project);
        if (model.Check?.Graph is { } relative && store.ToAbsolute(project, relative) is var file && File.Exists(file))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                if (await JsonSerializer.DeserializeAsync<ModelGraphReport>(stream, cancellationToken: ct).ConfigureAwait(false) is { Ok: true } saved)
                    return saved;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // made again below
            }
        }
        if (engine is null)
            return null;
        var path = store.ToAbsolute(project, model.Original ?? model.Staged);
        var layers = model.Check?.Layers ?? [];
        var graph = await AnalyzeAsync(folders, path, layers, [], ct).ConfigureAwait(false);
        if (graph is not null && model.Check is { } record)
        {
            record.Graph = store.ToRelative(project, Path.Combine(folders.CheckDir, "graph.json"));
            store.Save(project);
        }
        return graph;
    }

    /// <summary>The graph analysis saved with the check, read from disk only (no tool is started). Null when there is none.</summary>
    public ModelGraphReport? ReadSavedGraph(ProjectFile project)
    {
        if (project.Model?.Check?.Graph is not { } relative || ProjectStore.IsDraft(project))
            return null;
        try
        {
            var file = store.ToAbsolute(project, relative);
            return File.Exists(file) && JsonSerializer.Deserialize<ModelGraphReport>(File.ReadAllText(file)) is { Ok: true } saved ? saved : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the pieces

    private async Task<ModelFacts?> InspectAsync(ProjectWorkspace folders, string onnxPath, List<string> problems, CancellationToken ct)
    {
        try
        {
            var inspection = await engine!.For(folders).InspectModelAsync(onnxPath, ct).ConfigureAwait(false);
            if (inspection.Value is { Ok: true } m)
                return ToFacts(m);
            problems.Add("모델 구조를 읽지 못했어요: " + (inspection.Value?.Problems.FirstOrDefault() ?? $"종료 코드 {inspection.Run.ExitCode}"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            problems.Add("모델 구조를 읽지 못했어요: " + ex.Message);
        }
        return null;
    }

    /// <summary>The check of design §5.7, recorded in <c>project.Model.Check</c>. Returns the graph analysis (ONNX).</summary>
    private async Task<ModelGraphReport?> CheckAsync(ProjectFile project, ProjectWorkspace folders, string modelPath, string? weights,
        List<string> problems, ImportProgress? progress, CancellationToken ct)
    {
        var model = project.Model!;
        var record = new ModelCheckRecord { CheckedAt = DateTimeOffset.Now };
        model.Check = record;

        progress?.Stage?.Invoke("NPU에서 실행되는지 점검하는 중");
        try
        {
            var check = await new ModelChecker(engine!).CheckStagedAsync(folders, modelPath, weights, progress?.ToolOutput, ct).ConfigureAwait(false);
            record.ElapsedSec = Math.Round(check.Elapsed.TotalSeconds, 1);
            if (check.Error is { } error)
            {
                (record.Outcome, record.ErrorTitle, record.ErrorDetail) = ("failed", error.Title, error.Detail);
            }
            else if (check.Compatibility is not { } report)
            {
                (record.Outcome, record.ErrorTitle, record.ErrorDetail) = ("failed", "호환성 표를 읽지 못했어요", "로그를 열어 내용을 확인해 주세요.");
            }
            else
            {
                record.TotalLayers = report.Total;
                record.Layers = report.Layers.Select(l => new CheckedLayer { Index = l.Index, Name = l.Name, Type = l.Type, Description = l.Description }).ToList();
                record.Outcome = report.IsFullyCompatible ? "clean" : "listed";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            (record.Outcome, record.ErrorTitle, record.ErrorDetail) = ("failed", "점검하지 못했어요", ex.Message);
        }

        if (model.Format != "onnx")
            return null;

        // where those layers are on the graph, what is stuck behind them, where the model can be cut
        progress?.Stage?.Invoke("모델 그래프를 살펴보는 중");
        var graph = await AnalyzeAsync(folders, modelPath, record.Layers, problems, ct).ConfigureAwait(false);
        if (graph is null)
            return null;
        record.Graph = store.ToRelative(project, Path.Combine(folders.CheckDir, "graph.json"));
        record.Ops = graph.Ops;
        record.BlockedOps = graph.BlockedOps;
        foreach (var bad in graph.Bad)
            if (record.Layers.FirstOrDefault(l => l.Name == bad.Layer) is { } layer)
                layer.Node = bad.Node;
        if (record.Outcome != "failed")
        {
            // a layer that could not be placed on the graph leaves the answer a list
            record.Outcome = graph.Unmapped.Count > 0 ? "listed" : graph.Verdict;
            record.BlockedReason = graph.Reason;
        }
        if (graph.Proposal is { } proposal)
            record.Proposal = new ProposedCut
            {
                Kind = proposal.Kind, Decoder = proposal.Decoder, Nodes = [.. proposal.Nodes],
                KeptOps = proposal.Kept, AwayOps = proposal.Away?.Ops ?? 0,
            };
        return graph;
    }

    /// <summary>Runs <c>graph_model.py</c> and keeps its answer as <c>out\check\graph.json</c>.</summary>
    private async Task<ModelGraphReport?> AnalyzeAsync(ProjectWorkspace folders, string modelPath, IReadOnlyList<CheckedLayer> layers,
        List<string> problems, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(folders.CheckDir);
            string? layersJson = null;
            if (layers.Count > 0)
            {
                layersJson = Path.Combine(folders.CheckDir, "layers.json");
                await File.WriteAllTextAsync(layersJson,
                    JsonSerializer.Serialize(layers.Select(l => new { name = l.Name, type = l.Type, description = l.Description })), ct).ConfigureAwait(false);
            }
            var analysis = await engine!.For(folders).AnalyzeGraphAsync(modelPath, layersJson, 2, ct).ConfigureAwait(false);
            if (analysis.Value is { Ok: true } graph)
            {
                await File.WriteAllTextAsync(Path.Combine(folders.CheckDir, "graph.json"), JsonSerializer.Serialize(graph, GraphJson), ct).ConfigureAwait(false);
                return graph;
            }
            problems.Add("모델 그래프를 읽지 못했어요: " + (analysis.Value?.Problems.FirstOrDefault() ?? $"종료 코드 {analysis.Run.ExitCode}"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        {
            problems.Add("모델 그래프를 읽지 못했어요: " + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// Cuts <paramref name="originalPath"/> (inside the project) at the named operators and makes the result the staged
    /// model. Returns null, with the reason in <paramref name="problems"/>, when the cut could not be made.
    /// </summary>
    private async Task<ModelCutReport?> CutStagedAsync(ProjectFile project, ProjectWorkspace folders, string originalPath, IReadOnlyList<string> nodes,
        string by, string? decoder, List<string> problems, ImportProgress? progress, CancellationToken ct)
    {
        var model = project.Model!;
        progress?.Stage?.Invoke("모델을 자르는 중");
        try
        {
            var steps = engine!.For(folders);
            var stem = Path.GetFileNameWithoutExtension(originalPath);
            var result = await steps.CutModelAsync(originalPath, nodes, folders.ModelInputDir, stem, ct).ConfigureAwait(false);
            if (result.Value is not { Ok: true, Extracted: { } extracted } done || !File.Exists(extracted))
            {
                problems.Add("모델을 자르지 못했어요: " + (result.Value?.Problems.FirstOrDefault() ?? $"종료 코드 {result.Run.ExitCode}"));
                return null;
            }

            // a grid file of an earlier cut does not belong to this one
            var grid = Path.Combine(folders.ModelInputDir, stem + ".bin");
            if (done.Grid is null && model.Prepared == "yolo-v8" && File.Exists(grid))
                File.Delete(grid);

            model.Original = store.ToRelative(project, originalPath);
            model.Staged = store.ToRelative(project, extracted);
            model.Sha256 = FileStager.Sha256Of(extracted);
            model.SizeBytes = new FileInfo(extracted).Length;
            model.Prepared = done.Family == "v8" ? "yolo-v8" : "cut";
            model.Cut = new ModelCut
            {
                Nodes = [.. done.Nodes], By = by, Decoder = done.Family == "v8" ? "yolo-v8" : decoder,
                KeptOps = done.KeptNodes, AwayOps = done.RemovedNodes,
            };
            if (await InspectAsync(folders, extracted, problems, ct).ConfigureAwait(false) is { } facts)
                model.Facts = facts;
            else if (model.Facts is { } old)
                old.Outputs = done.Outputs.Select(ToFact).ToList();
            return done;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        {
            problems.Add("모델을 자르지 못했어요: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Fills the settings from what the staged model looks like and puts the companion file the family needs in place:
    /// the grid file made with a YOLOv8 cut, or the file next to the user's model (already in the project when the
    /// model is cut again later).
    /// </summary>
    private (PresetGuess Guess, string? Companion) ApplySettings(ProjectFile project, ProjectWorkspace folders, ModelCutReport? cut,
        string sourceDir, string stem, List<string> problems)
    {
        var model = project.Model!;
        var guess = PresetGuesser.Guess(model.Format, model.Facts);
        var preset = PresetCatalog.Find(guess.PresetId)!;
        preset.ApplyTo(project);
        if (cut?.RegMax is { } regMax)
            project.Options.Convert.DflRegMax = regMax;
        if (guess.NumClass is { } classes && classes != preset.NumClass)
        {
            project.Options.Convert.NumClass = classes;
            project.Options.Labels.Set = "custom"; // the preset's label list has a different length
        }
        if (guess.OutputOrder is not null)
            project.Options.Convert.OutputOrder = guess.OutputOrder;
        project.Options.Convert.ForceOutput = []; // layer names of an earlier model mean nothing for this one

        // the companion file sits next to the model in the toolkit's examples: yolov5m_extracted.onnx + yolov5m.bin
        string? companion = null;
        var baseStem = stem.EndsWith("_extracted", StringComparison.OrdinalIgnoreCase) ? stem[..^"_extracted".Length] : stem;
        string[] wanted = preset.Companion switch
        {
            "grid" => [baseStem + ".bin", stem + ".bin"],
            "anchor" => [baseStem + ".anchor", stem + ".anchor"],
            "weights" => [stem + ".weights"],
            _ => [],
        };
        var found = wanted.Select(n => Path.Combine(sourceDir, n)).FirstOrDefault(File.Exists);
        if (found is null && preset.Companion is { } kind)
        {
            // brought in earlier and still in the project (the model is being cut again, or came from the project's own copy)
            var extension = kind switch { "grid" => ".bin", "anchor" => ".anchor", _ => ".weights" };
            found = Directory.EnumerateFiles(folders.ModelInputDir, "*" + extension).FirstOrDefault();
        }
        project.Options.Convert.PostProcess = null;
        model.Weights = null;
        // only when the settings use it: a head cut like YOLOv8's whose outputs the guess does not take for YOLOv8
        // (YOLO26: 4 box channels) is built as kind "unknown", where a grid file without a class count stops the
        // converter's command line ("--add-detection-post-process needs --num-class")
        if (cut?.Grid is { } generatedGrid && preset.Companion == "grid")
        {
            // made a moment ago, already inside the project
            companion = generatedGrid;
            project.Options.Convert.PostProcess = store.ToRelative(project, generatedGrid);
        }
        else if (found is not null)
        {
            var copy = FileStager.StageFile(found, folders.ModelInputDir);
            companion = copy.Staged;
            if (preset.Companion == "weights")
                model.Weights = store.ToRelative(project, copy.Staged);
            else
                project.Options.Convert.PostProcess = store.ToRelative(project, copy.Staged);
        }
        else if (preset.Companion is not null)
        {
            problems.Add(preset.Companion switch
            {
                "grid" => $"박스 계산 파일({baseStem}.bin)을 모델 옆에서 찾지 못했어요. 설정에서 넣어 주세요.",
                "anchor" => $"anchor 파일({baseStem}.anchor)을 모델 옆에서 찾지 못했어요. 설정에서 넣어 주세요.",
                _ => $"가중치 파일({stem}.weights)을 모델 옆에서 찾지 못했어요. 같은 폴더에 두고 다시 가져와 주세요.",
            });
        }
        return (guess, companion);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static ModelFacts ToFacts(ModelInspection m) => new()
    {
        Inputs = m.Inputs.Select(ToFact).ToList(),
        Outputs = m.Outputs.Select(ToFact).ToList(),
        NodeCount = m.NodeCount,
        ParameterCount = m.ParameterCount,
        Opset = m.Opsets.FirstOrDefault(o => o.Domain is "ai.onnx" or "")?.Version,
        Operators = new Dictionary<string, int>(m.Operators),
    };

    private static TensorFact ToFact(OnnxTensor t) => new()
    {
        Name = t.Name,
        Shape = t.Shape.Select(d => d.ValueKind switch
        {
            JsonValueKind.Number => d.GetRawText(),
            JsonValueKind.String => d.GetString() ?? "?",
            _ => "?",
        }).ToList(),
    };
}
