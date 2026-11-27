using System.Text.Json;
using System.Text.Json.Serialization;
using TopstToolkit.Core.Components;
using TopstToolkit.Engine.Helpers;

namespace TopstToolkit.Core.Zoo;

/// <summary>
/// <c>engine\zoo\zoo.json</c> (design §5.9): the models of the model zoo, all checked and built by the review, with the
/// recipe to export each one the same way, the preprocessing its weights were trained with and where the weights
/// officially come from. Made by <c>build\zoo\zoo_catalog.py</c>; ships with the app next to <c>engine\helpers</c>.
/// </summary>
public sealed class ZooCatalog
{
    public const string FileName = "zoo.json";
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")] public int Schema { get; init; }
    /// <summary>The day of the review the numbers come from.</summary>
    [JsonPropertyName("reviewed")] public string Reviewed { get; init; } = "";
    [JsonPropertyName("sdk")] public string Sdk { get; init; } = "";
    /// <summary>The component with the export packages (<see cref="ComponentCatalog.Zoo"/>).</summary>
    [JsonPropertyName("component")] public string Component { get; init; } = ComponentCatalog.Zoo;
    [JsonPropertyName("models")] public List<ZooModel> Models { get; init; } = [];

    public ZooModel? Find(string id) => Models.FirstOrDefault(m => m.Id == id);

    public static ZooCatalog Load(string path)
    {
        var catalog = JsonSerializer.Deserialize<ZooCatalog>(File.ReadAllText(path)) ?? throw new InvalidDataException($"{path} is empty.");
        if (catalog.Schema != CurrentSchema)
            throw new InvalidDataException($"{path}: schema {catalog.Schema}, this app reads {CurrentSchema}.");
        return catalog;
    }

    /// <summary><c>engine\zoo</c> next to <c>engine\helpers</c>.</summary>
    public static string DirectoryFor(string helpersDir) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(helpersDir))) ?? helpersDir, "zoo");

    /// <summary>The catalog that comes with the app (or the repository), or null when there is none.</summary>
    public static ZooCatalog? Locate(string? startDirectory = null)
    {
        if (TopstToolkit.Engine.EngineLayout.FindHelpersDir(startDirectory) is not { } helpers)
            return null;
        var path = Path.Combine(DirectoryFor(helpers), FileName);
        try
        {
            return File.Exists(path) ? Load(path) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class ZooModel
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    /// <summary><c>torchvision</c>, <c>timm</c> or <c>ultralytics</c>.</summary>
    [JsonPropertyName("lib")] public string Library { get; init; } = "";
    /// <summary>The library's own name of the model (<c>mobilenet_v2</c>, <c>yolov8n</c>).</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("display")] public string Display { get; init; } = "";
    /// <summary><c>class</c> or <c>obj</c>, as in the project file.</summary>
    [JsonPropertyName("task")] public string Task { get; init; } = "";
    [JsonPropertyName("family")] public string Family { get; init; } = "";
    /// <summary>One line about the model for its card.</summary>
    [JsonPropertyName("about")] public string About { get; init; } = "";
    /// <summary><c>imagenet</c> or <c>coco80</c>.</summary>
    [JsonPropertyName("dataset")] public string Dataset { get; init; } = "";
    [JsonPropertyName("export")] public ZooExport Export { get; init; } = new();
    [JsonPropertyName("preprocess")] public ZooPreprocess Preprocess { get; init; } = new();
    [JsonPropertyName("weights")] public ZooWeights Weights { get; init; } = new();
    [JsonPropertyName("license")] public ZooLicense License { get; init; } = new();
    [JsonPropertyName("review")] public ZooReview Review { get; init; } = new();

    [JsonIgnore] public string LibraryName => Library switch { "ultralytics" => "Ultralytics", "torchvision" => "torchvision", "timm" => "timm", var x => x };
    [JsonIgnore] public string DatasetText => Dataset == "imagenet" ? "ImageNet 1000개" : Dataset == "coco80" ? "COCO 80개" : Dataset;
    [JsonIgnore] public string TaskText => Task == "obj" ? "객체 검출" : "이미지 분류";

    /// <summary>The weights as a download the components installer fetches and checks (kept in <c>downloads\zoo</c>).</summary>
    public DownloadSpec WeightsDownload() => new()
    {
        Id = "zoo-" + Id, Name = $"{Display} 가중치 ({Weights.File})", Url = Weights.Url, Sha256 = Weights.Sha256,
        Sha256Prefix = Weights.Sha256Prefix, Bytes = Weights.Bytes, License = License.Name, LicenseUrl = License.Url,
        Folder = "zoo", LocalName = Weights.LocalName(Library),
    };

    /// <summary>How the export helper is asked for this model.</summary>
    public ZooExportRecipe Recipe() => new(Library, Name, Task, Export.Size, Export.Plain, Export.Relu6, Weights.Tag);
}

public sealed class ZooExport
{
    /// <summary>The input size the model was built with in the review (smaller than <see cref="NominalSize"/> when the compiler needed it).</summary>
    [JsonPropertyName("size")] public int Size { get; init; }
    /// <summary>The size the weights were trained for.</summary>
    [JsonPropertyName("nominalSize")] public int NominalSize { get; init; }
    /// <summary>Exported as is (the review's first round), none of the fixes.</summary>
    [JsonPropertyName("plain")] public bool Plain { get; init; }
    /// <summary>Hard-sigmoid as clip(x + 3, 0, 6) / 6.</summary>
    [JsonPropertyName("relu6")] public bool Relu6 { get; init; }
    /// <summary>The input had to be made smaller than the trained size for the compiler.</summary>
    [JsonPropertyName("smaller")] public bool Smaller { get; init; }
    /// <summary>What the export changed (<c>meanToPool</c>, <c>hardSigmoidToRelu6</c>, <c>reparameterized</c>, <c>odd</c>, <c>smaller</c> ...).</summary>
    [JsonPropertyName("fixes")] public List<string> Fixes { get; init; } = [];
}

/// <summary>What the weights were trained with, in 0..1 units like the project's settings.</summary>
public sealed class ZooPreprocess
{
    [JsonPropertyName("mean")] public double[] Mean { get; init; } = [0, 0, 0];
    [JsonPropertyName("std")] public double[] Std { get; init; } = [1, 1, 1];
}

public sealed class ZooWeights
{
    /// <summary>The file's name at the source (<c>model.safetensors</c> for every timm model).</summary>
    [JsonPropertyName("file")] public string File { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    /// <summary>torchvision: the start of the SHA-256 in the file name, what torch.hub checks (no full hash is published).</summary>
    [JsonPropertyName("sha256Prefix")] public string? Sha256Prefix { get; init; }
    /// <summary>torchvision: the weights entry (<c>IMAGENET1K_V1</c>); timm: <c>model.tag</c>; Ultralytics: the release.</summary>
    [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    /// <summary>Where a person can read about the weights (the Hugging Face page for timm).</summary>
    [JsonPropertyName("page")] public string Page { get; init; } = "";

    /// <summary>A name that tells the files apart in <c>downloads\zoo</c>: timm's are all called model.safetensors.</summary>
    public string LocalName(string library) => library == "timm" ? Tag + Path.GetExtension(File) : File;

    /// <summary>"yolov8n.pt · 6 MB · GitHub"</summary>
    public string Caption => $"{File} · {ComponentSteps.Size(Bytes)} · {Host}";

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        ? uri.Host switch { "github.com" => "GitHub", "huggingface.co" => "Hugging Face", var h => h }
        : "";
}

public sealed class ZooLicense
{
    /// <summary>Not shown on screen (the user, 2026-11-22): the link is.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

/// <summary>What the review measured for the model.</summary>
public sealed class ZooReview
{
    /// <summary>The toolkit's performance estimate of one frame on the board, in milliseconds.</summary>
    [JsonPropertyName("ms")] public double? Ms { get; init; }
    [JsonPropertyName("params")] public long Params { get; init; }
    /// <summary><c>clean</c> (all on the NPU) or <c>tail</c> (the end is cut and runs on the CPU).</summary>
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    /// <summary>The box maths the cut tail uses on the board: <c>yolo-v8</c>, <c>ssd</c> or none.</summary>
    [JsonPropertyName("decoder")] public string? Decoder { get; init; }
    [JsonPropertyName("preset")] public string? Preset { get; init; }
    [JsonPropertyName("boardPostProcess")] public string? BoardPostProcess { get; init; }
    /// <summary>The most memory the build's tools took on the PC in the review (null in the first catalog of 2026-11-23).</summary>
    [JsonPropertyName("peakMemoryBytes")] public long? PeakMemoryBytes { get; init; }
}
