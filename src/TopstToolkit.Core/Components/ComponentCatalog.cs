using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopstToolkit.Core.Components;

/// <summary>
/// <c>engine\components\components.json</c> (design D12): what the app installs on the user's PC when the user asks for it,
/// where each file officially comes from and the SHA-256 it must have. Ships with the app, next to <c>engine\helpers</c>.
/// </summary>
public sealed class ComponentCatalog
{
    public const string FileName = "components.json";
    public const int CurrentSchema = 1;

    /// <summary>Id of the component without which nothing can be built.</summary>
    public const string Engine = "engine";
    /// <summary>Id of the optional Arm cross compiler for <c>net.so</c>.</summary>
    public const string BoardTools = "board-tools";
    /// <summary>Id of the model zoo's export packages (design §5.9), installed with the engine's Python into a folder of their own.</summary>
    public const string Zoo = "zoo";

    [JsonPropertyName("schema")] public int Schema { get; init; }
    [JsonPropertyName("components")] public List<ComponentSpec> Components { get; init; } = [];

    /// <summary>The folder the catalog was read from; lock files are relative to it.</summary>
    [JsonIgnore] public string Directory { get; private set; } = "";

    public ComponentSpec Get(string id) =>
        Components.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException($"The component catalog has no '{id}'.");

    public static ComponentCatalog Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var catalog = JsonSerializer.Deserialize<ComponentCatalog>(File.ReadAllText(path))
                      ?? throw new InvalidDataException($"{path} is empty.");
        if (catalog.Schema != CurrentSchema)
            throw new InvalidDataException($"{path}: schema {catalog.Schema}, this app reads {CurrentSchema}.");
        catalog.Directory = Path.GetFullPath(directory);
        return catalog;
    }

    /// <summary>The catalog folder that belongs to a helpers folder: <c>engine\components</c> next to <c>engine\helpers</c>.</summary>
    public static string DirectoryFor(string helpersDir) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(helpersDir))) ?? helpersDir, "components");

    /// <summary>Development and tests only: a folder with another catalog (for example one with a wrong hash, to see the failure screen).</summary>
    public const string EnvironmentVariable = "TOPST_COMPONENTS_CATALOG";

    /// <summary>The catalog that comes with the app (or the repository), or null when there is none.</summary>
    public static ComponentCatalog? Find(string? startDirectory = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var directory = getEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } custom ? custom
            : TopstToolkit.Engine.EngineLayout.FindHelpersDir(startDirectory) is { } helpers ? DirectoryFor(helpers) : null;
        try
        {
            return directory is not null && File.Exists(Path.Combine(directory, FileName)) ? Load(directory) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed class ComponentSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    /// <summary>Folder under the components root: <c>runtime</c> or <c>toolchain</c>, the names the app folder and the repository use too.</summary>
    [JsonPropertyName("folder")] public string Folder { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("required")] public bool Required { get; init; }
    /// <summary>Offered (picked) on the first-run screen. The model zoo's packages are not: they come when a model is first chosen.</summary>
    [JsonPropertyName("setup")] public bool Setup { get; init; } = true;
    /// <summary>Another component this one runs with (the model zoo's packages run with the engine's Python).</summary>
    [JsonPropertyName("needs")] public string? Needs { get; init; }
    [JsonPropertyName("installedBytes")] public long InstalledBytes { get; init; }
    [JsonPropertyName("downloads")] public List<DownloadSpec> Downloads { get; init; } = [];
    [JsonPropertyName("packages")] public PackagesSpec? Packages { get; init; }
    [JsonPropertyName("extras")] public List<ExtraSpec> Extras { get; init; } = [];
    [JsonPropertyName("toolkit")] public ToolkitSpec? Toolkit { get; init; }
    [JsonPropertyName("prune")] public PruneSpec? Prune { get; init; }
    [JsonPropertyName("archive")] public ArchiveSpec? Archive { get; init; }
    /// <summary>Paths (relative to the component folder) that must exist after the install.</summary>
    [JsonPropertyName("requiredFiles")] public List<string> RequiredFiles { get; init; } = [];

    /// <summary>What the install fetches: the listed files plus the Python packages.</summary>
    [JsonIgnore] public long DownloadBytes => Downloads.Sum(d => d.Bytes) + (Packages?.Bytes ?? 0) + Extras.Sum(e => e.Bytes);

    public DownloadSpec Download(string id) =>
        Downloads.FirstOrDefault(d => d.Id == id) ?? throw new KeyNotFoundException($"Component '{Id}' has no download '{id}'.");
}

public sealed class DownloadSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    /// <summary>
    /// When the source publishes no full hash: the start of the SHA-256 it puts into the file name (torchvision's
    /// <c>resnet18-f37072fd.pth</c>, what torch.hub itself checks). Used only when <see cref="Sha256"/> is empty.
    /// </summary>
    [JsonPropertyName("sha256Prefix")] public string? Sha256Prefix { get; init; }
    [JsonPropertyName("md5")] public string? Md5 { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("license")] public string License { get; init; } = "";
    [JsonPropertyName("licenseUrl")] public string? LicenseUrl { get; init; }

    /// <summary>The name the file is kept under when the URL's own does not tell files apart (timm's model.safetensors).</summary>
    [JsonPropertyName("localName")] public string? LocalName { get; init; }
    /// <summary>A folder under <c>downloads</c> for files of one kind (<c>zoo</c> for model weights).</summary>
    [JsonPropertyName("folder")] public string? Folder { get; init; }

    [JsonIgnore] public string FileName => LocalName ?? Path.GetFileName(new Uri(Url).AbsolutePath);

    /// <summary>Whether a file's SHA-256 is the one this download must have.</summary>
    public bool Accepts(string sha256) =>
        Sha256.Length > 0 ? string.Equals(sha256, Sha256, StringComparison.OrdinalIgnoreCase)
        : Sha256Prefix is { Length: >= 8 } prefix && sha256.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}

public sealed class PackagesSpec
{
    /// <summary>pip requirements with <c>--hash</c> on every line, relative to the catalog folder.</summary>
    [JsonPropertyName("lock")] public string Lock { get; init; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("license")] public string License { get; init; } = "";
}

/// <summary>Packages installed into a folder of their own (<c>pip --target</c>), off the engine's sys.path.</summary>
public sealed class ExtraSpec
{
    [JsonPropertyName("target")] public string Target { get; init; } = "";
    [JsonPropertyName("lock")] public string Lock { get; init; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("license")] public string License { get; init; } = "";
}

/// <summary>What is taken from the toolkit zip: folders (with the names robocopy /XD and /XF left out) and wheels.</summary>
public sealed class ToolkitSpec
{
    [JsonPropertyName("download")] public string Download { get; init; } = "";
    /// <summary>The folder every entry of the zip starts with (<c>tc-nn-toolkit/</c>).</summary>
    [JsonPropertyName("root")] public string Root { get; init; } = "";
    [JsonPropertyName("copy")] public List<CopyRule> Copy { get; init; } = [];
    [JsonPropertyName("wheels")] public List<string> Wheels { get; init; } = [];
}

public sealed class CopyRule
{
    [JsonPropertyName("from")] public string From { get; init; } = "";
    [JsonPropertyName("to")] public string To { get; init; } = "";
    /// <summary>Folder names left out at any depth.</summary>
    [JsonPropertyName("excludeDirs")] public List<string> ExcludeDirs { get; init; } = [];
    /// <summary>File names left out at any depth.</summary>
    [JsonPropertyName("excludeFiles")] public List<string> ExcludeFiles { get; init; } = [];
}

/// <summary>Build-only files removed after pip (feature/09 §6-1): headers, import libraries, tests, launchers.</summary>
public sealed class PruneSpec
{
    [JsonPropertyName("roots")] public List<string> Roots { get; init; } = [];
    [JsonPropertyName("files")] public List<string> Files { get; init; } = [];
    [JsonPropertyName("dirs")] public List<string> Dirs { get; init; } = [];
    [JsonPropertyName("remove")] public List<string> Remove { get; init; } = [];
}

/// <summary>Members extracted from a .tar.xz with the Windows <c>tar.exe</c>, and renames after it.</summary>
public sealed class ArchiveSpec
{
    [JsonPropertyName("download")] public string Download { get; init; } = "";
    [JsonPropertyName("root")] public string Root { get; init; } = "";
    [JsonPropertyName("members")] public List<string> Members { get; init; } = [];
    [JsonPropertyName("renames")] public List<RenameRule> Renames { get; init; } = [];
}

public sealed class RenameRule
{
    [JsonPropertyName("from")] public string From { get; init; } = "";
    [JsonPropertyName("to")] public string To { get; init; } = "";
}
