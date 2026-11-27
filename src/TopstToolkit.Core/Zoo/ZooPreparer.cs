using TopstToolkit.Core.Components;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Projects;
using TopstToolkit.Engine.Errors;
using TopstToolkit.Engine.Helpers;
using TopstToolkit.Engine.Runner;

namespace TopstToolkit.Core.Zoo;

/// <summary>Where a preparation is. <see cref="Key"/>: <c>weights</c>, <c>export</c>, <c>import</c> or <c>settings</c>.</summary>
public sealed record ZooProgress(string Key, string Step, long? Done = null, long? Total = null)
{
    public double? Fraction => Total is > 0 && Done is { } done ? Math.Clamp((double)done / Total.Value, 0, 1) : null;
}

/// <summary>A preparation that stopped: at which stage, why (in the app's words) and the log line behind it.</summary>
public sealed class ZooPrepareException(string stage, string title, string detail, string? evidence = null, Exception? inner = null)
    : Exception(title + " · " + detail, inner)
{
    /// <summary><c>weights</c>, <c>export</c> or <c>import</c>.</summary>
    public string Stage { get; } = stage;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string? Evidence { get; } = evidence;
}

/// <summary>What a preparation made: the project (model brought in, settings from the catalog) and how it got there.</summary>
public sealed class ZooPreparation
{
    public required ProjectFile Project { get; init; }
    public required ModelImportResult Import { get; init; }
    public required ZooExportReport Export { get; init; }
    /// <summary>The weights file used, or null for random weights (development and tests).</summary>
    public required string? Weights { get; init; }
}

/// <summary>
/// The model zoo's "만들기" (design §5.9): the model's weights from their official source (checked against the
/// catalog's hash, kept in <c>downloads\zoo</c>), the ONNX export exactly as the review did it
/// (<c>engine\helpers\zoo_export.py</c> with the zoo component's packages), then the import every model goes through
/// (check, cut at a known head, settings) and the preprocessing the weights were trained with. Building is the
/// app's usual build afterwards.
/// </summary>
public sealed class ZooPreparer
{
    /// <summary>Development and tests only: a folder with the zoo's packages in place of the installed component (the review's artifacts\zoo-site).</summary>
    public const string SiteVariable = "TOPST_ZOO_SITE";

    private readonly ComponentInstaller _installer;
    private readonly EngineSession _session;
    private readonly ProjectStore _store;
    private readonly ZooCatalog _catalog;

    public ZooPreparer(ZooCatalog catalog, ComponentInstaller installer, EngineSession session, ProjectStore store, Func<string, string?>? getEnvironmentVariable = null)
    {
        _catalog = catalog;
        _installer = installer;
        _session = session;
        _store = store;
        var custom = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(SiteVariable);
        SiteOverride = custom is { Length: > 0 } && Directory.Exists(custom) ? Path.GetFullPath(custom) : null;
    }

    public ZooCatalog Catalog => _catalog;

    /// <summary>The packages folder given by <see cref="SiteVariable"/>, if any.</summary>
    public string? SiteOverride { get; }

    /// <summary>Where the zoo's packages are: <c>&lt;components&gt;\zoo\site</c>, or null when the component is not installed.</summary>
    public string? SiteDirectory => SiteOverride
        ?? (_installer.StateOf(_catalog.Component) == ComponentState.Installed
            ? Path.Combine(_installer.FolderOf(_catalog.Component), _installer.Catalog.Get(_catalog.Component).Extras.FirstOrDefault()?.Target ?? "site")
            : null);

    /// <summary>The export packages are there; otherwise the component has to be installed first.</summary>
    public bool HasPackages => SiteDirectory is not null;

    /// <summary>
    /// Where downloaded weights are kept: the user's workspace (<c>downloads\zoo</c>), since the components folder of an app
    /// installed for all users is read-only (design §7.2). Weights downloaded before 2026-11-25 into
    /// <c>&lt;components&gt;\downloads\zoo</c> are still used from there.
    /// </summary>
    public string WeightsDirectory => Path.Combine(_session.Workspace.DownloadsDir, "zoo");

    /// <summary>The weights file when it was downloaded before (and still has the right hash); null otherwise.</summary>
    public async Task<string?> DownloadedWeightsAsync(ZooModel model, CancellationToken ct = default)
    {
        var download = model.WeightsDownload();
        foreach (var path in new[] { Path.Combine(WeightsDirectory, download.FileName), Path.Combine(_installer.DownloadsDirectory, "zoo", download.FileName) })
            if (await ComponentInstaller.MatchesAsync(path, download, ct).ConfigureAwait(false))
                return path;
        return null;
    }

    /// <summary>The model's weights from their official source, checked; a file downloaded before is used again.</summary>
    public async Task<string> FetchWeightsAsync(ZooModel model, IProgress<ZooProgress>? progress, CancellationToken ct)
    {
        var relay = progress is null ? null : new Progress<ComponentProgress>(p => progress.Report(new ZooProgress("weights", p.Step, p.Done, p.Total)));
        try
        {
            return await _installer.FetchAsync(_catalog.Component, model.WeightsDownload(), relay, ct, WeightsDirectory).ConfigureAwait(false);
        }
        catch (ComponentDownloadMismatchException e)
        {
            throw new ZooPrepareException("weights", "받은 가중치가 목록의 것과 달라요",
                "받은 파일을 지웠어요. 잠시 뒤 다시 해 보고, 같으면 출처가 바뀐 것이니 알려 주세요.", $"SHA-256 {e.ActualSha256}", e);
        }
        catch (HttpRequestException e)
        {
            throw new ZooPrepareException("weights", "가중치를 받지 못했어요",
                $"{model.Weights.Host}에 연결할 수 없어요. 인터넷 연결을 확인하고 다시 해 주세요.", e.Message, e);
        }
        catch (InvalidDataException e)
        {
            throw new ZooPrepareException("weights", "가중치를 확인할 수 없어요", "모델 목록에 이 가중치의 해시가 없어요.", e.Message, e);
        }
    }

    /// <summary>
    /// Everything up to a project that is ready to build: weights, export, import, settings. <paramref name="project"/>
    /// is a draft or an empty project; it gets its folder here and is saved.
    /// </summary>
    /// <param name="randomWeights">Development and tests: no download, the review's random weights (structure only).</param>
    public async Task<ZooPreparation> PrepareAsync(ZooModel model, ProjectFile project, IProgress<ZooProgress>? progress = null,
        ImportProgress? importProgress = null, CancellationToken ct = default, bool randomWeights = false)
    {
        var site = SiteDirectory ?? throw new ZooPrepareException("export", "모델 모음 구성 요소가 없어요", "설정 › 구성 요소에서 \"모델 모음\"을 설치해 주세요.");
        var weights = randomWeights ? null : await FetchWeightsAsync(model, progress, ct).ConfigureAwait(false);

        // the export runs as a job of the project: its log and answer stay with it
        project.Name = model.Display;
        _store.Materialize(project);
        var folders = _store.FoldersOf(project);
        progress?.Report(new ZooProgress("export", $"ONNX로 내보내는 중 ({model.Export.Size} × {model.Export.Size})"));
        var request = new ToolRunRequest
        {
            WorkingDirectory = folders.NewJobDirectory("zoo-export"),
            PycacheDirectory = _session.Workspace.PycacheDir,
            LogDirectory = folders.LogsDir,
            LogName = folders.NewLogName("zoo-export"),
        };
        var onnx = Path.Combine(request.WorkingDirectory, OnnxName(model));
        var export = await _session.Helpers.ExportZooModelAsync(model.Recipe(), site, weights, onnx, request, ct).ConfigureAwait(false);
        if (export.Run.Cancelled)
            throw new OperationCanceledException(ct);
        if (export.Value is not { Ok: true } exported || !File.Exists(onnx))
        {
            var error = ErrorClassifier.Classify(export.Run);
            var why = export.Value?.Problems.FirstOrDefault() ?? error?.Evidence ?? error?.Title ?? "보조 스크립트가 답하지 않았어요";
            throw new ZooPrepareException("export", "ONNX로 내보내지 못했어요", "로그를 열어 내용을 확인하거나 진단 패키지를 만들어 알려 주세요.", why);
        }

        progress?.Report(new ZooProgress("import", "NPU에서 실행되는지 점검하는 중"));
        var importer = new ModelImporter(_store, _session);
        var imported = await importer.ImportAsync(project, onnx, ct, importProgress).ConfigureAwait(false);

        // the preprocessing the weights were trained with, and where the model came from
        progress?.Report(new ZooProgress("settings", "설정 채우는 중"));
        project.Options.Convert.Mean = [.. model.Preprocess.Mean];
        project.Options.Convert.Std = [.. model.Preprocess.Std];
        project.Zoo = new ZooOrigin
        {
            Id = model.Id, Library = model.Library, Display = model.Display, WeightsUrl = model.Weights.Url,
            WeightsSha256 = weights is null ? "" : model.Weights.Sha256.Length > 0 ? model.Weights.Sha256 : model.Weights.Sha256Prefix ?? "",
            Reviewed = _catalog.Reviewed, Size = model.Export.Size, NominalSize = model.Export.NominalSize,
        };
        _store.Save(project);
        // the project keeps its own copy of the model: the export's file would only take room twice
        TryDelete(onnx);
        return new ZooPreparation { Project = project, Import = imported, Export = exported, Weights = weights };
    }

    /// <summary>The exported file's name: the library's model name (<c>yolov8n.onnx</c>), so the cut and the grid file are named after it.</summary>
    public static string OnnxName(ZooModel model) =>
        string.Concat(model.Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')) + ".onnx";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
