using System.Text.Json;
using TopstToolkit.Core.Projects;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine.Commands;
using TopstToolkit.Engine.Errors;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.Core.Pipeline;

/// <param name="Compatibility">The layer table; null when the model could not be read at all.</param>
/// <param name="Error">Why the check did not finish. Incompatible layers are not an error here: they are the answer.</param>
public sealed record ModelCheckResult(string ModelFile, CompatibilityReport? Compatibility, ToolError? Error, TimeSpan Elapsed)
{
    public bool IsCompatible => Error is null && Compatibility is { IsFullyCompatible: true };
}

/// <summary>
/// Tool "모델 점검": answers one question for one model file — can the NPU run every layer — without making a
/// project. Runs the converter without calibration in a scratch folder of the workspace.
/// </summary>
public sealed class ModelChecker(EngineSession engine)
{
    public const string ScratchProject = "quick-check";

    public async Task<ModelCheckResult> CheckAsync(string modelPath, Action<ToolEvent>? onEvent = null, CancellationToken ct = default)
    {
        if (ModelImporter.FormatOf(modelPath) is not { } format)
            throw new NotSupportedException("지원하지 않는 모델 형식이에요. .onnx, .tflite, .cfg, .enlight 파일을 골라 주세요.");

        var folders = engine.Workspace.OpenProject(ScratchProject);
        foreach (var dir in new[] { folders.ModelInputDir, folders.NetworksDir, folders.CompatDir })
            if (Directory.Exists(dir))
                foreach (var old in Directory.EnumerateFiles(dir))
                    File.Delete(old);

        var staged = FileStager.StageFile(modelPath, folders.ModelInputDir);
        string? weights = null;
        if (format == "darknet")
        {
            var beside = Path.ChangeExtension(Path.GetFullPath(modelPath), ".weights");
            if (!File.Exists(beside))
                return new ModelCheckResult(Path.GetFileName(modelPath), null,
                    new ToolError(ToolErrorCode.InputMissing, "가중치 파일이 필요해요", $"{Path.GetFileName(beside)} 파일을 .cfg와 같은 폴더에 두고 다시 해 주세요.", SuggestedAction.None), TimeSpan.Zero);
            weights = FileStager.StageFile(beside, folders.ModelInputDir).Staged;
        }

        // As an image model the converter normalizes with three mean/std values, so a model whose input is not
        // 3-channel stops before any layer is checked ("size of tensor a (4) must match the size of tensor b (3)").
        // Such a model takes .npy input (feature/02 recipe G): the same dataset kind is named here, without data.
        var npyInput = format == "onnx" && await InputChannelsAsync(folders, staged.Staged, ct).ConfigureAwait(false) is { } channels and not 3;

        return await RunAsync(folders, staged.Staged, weights, folders.NetworksDir, folders.CompatDir, npyInput, Path.GetFileName(modelPath), onEvent, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same check for a model that is already inside a project (brought in a moment ago): the log stays with the
    /// project, the compatibility table goes to <c>out\check</c>, and the network file of the check is removed again
    /// (it is as large as the model and of no use).
    /// </summary>
    public async Task<ModelCheckResult> CheckStagedAsync(ProjectWorkspace folders, string stagedModel, string? stagedWeights = null,
        Action<ToolEvent>? onEvent = null, CancellationToken ct = default)
    {
        var format = ModelImporter.FormatOf(stagedModel) ?? throw new NotSupportedException("지원하지 않는 모델 형식이에요.");
        Directory.CreateDirectory(folders.CheckDir);
        foreach (var old in Directory.EnumerateFiles(folders.CheckDir))
            File.Delete(old);
        if (format == "darknet" && stagedWeights is null)
            return new ModelCheckResult(Path.GetFileName(stagedModel), null,
                new ToolError(ToolErrorCode.InputMissing, "가중치 파일이 필요해요", "같은 이름의 .weights 파일을 .cfg와 같은 폴더에 두고 다시 가져와 주세요.", SuggestedAction.None), TimeSpan.Zero);

        var npyInput = format == "onnx" && await InputChannelsAsync(folders, stagedModel, ct).ConfigureAwait(false) is { } channels and not 3;
        var result = await RunAsync(folders, stagedModel, stagedWeights, folders.CheckDir, folders.CheckDir, npyInput, Path.GetFileName(stagedModel), onEvent, ct).ConfigureAwait(false);
        foreach (var network in Directory.EnumerateFiles(folders.CheckDir, "*.enlight"))
            File.Delete(network);
        return result;
    }

    private async Task<ModelCheckResult> RunAsync(ProjectWorkspace folders, string model, string? weights, string networkDir, string compatDir,
        bool npyInput, string displayName, Action<ToolEvent>? onEvent, CancellationToken ct)
    {
        var output = Path.Combine(networkDir, Path.GetFileNameWithoutExtension(model) + ".enlight");
        // "unknown" converts the graph as it is, which is exactly the question: which layers can the NPU run
        var result = await engine.For(folders).ConvertAsync(new ConverterOptions
        {
            Model = model, Output = output, Type = NetworkType.Unknown,
            CompatibilityLogRoot = compatDir, DatasetRoot = folders.CalibrationDir,
            EnableTrack = false, Weight = weights, Dataset = npyInput ? "CustomNPY" : "Custom",
        }, "check", onEvent, ct).ConfigureAwait(false);

        // incompatible layers are the result of the check, not a failure of it
        var error = result.Error is { Code: ToolErrorCode.IncompatibleLayers } ? null : result.Error;
        return new ModelCheckResult(displayName, result.Compatibility, error, result.Run.Elapsed);
    }

    /// <summary>Channels of the first input when the model states them as a number (ONNX, N×C×H×W); null when unknown.</summary>
    private async Task<int?> InputChannelsAsync(ProjectWorkspace folders, string onnxPath, CancellationToken ct)
    {
        try
        {
            var inspection = await engine.For(folders).InspectModelAsync(onnxPath, ct).ConfigureAwait(false);
            var shape = inspection.Value is { Ok: true } model ? model.Inputs.FirstOrDefault()?.Shape : null;
            return shape is { Count: 4 } && shape[1].ValueKind == JsonValueKind.Number && shape[1].TryGetInt32(out var c) && c > 0 ? c : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            return null; // the check itself reports what is wrong with the file
        }
    }
}
