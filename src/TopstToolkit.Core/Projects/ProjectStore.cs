using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TopstToolkit.Core.Workspace;

namespace TopstToolkit.Core.Projects;

/// <summary>A project folder whose <c>project.json</c> could not be read; listed so the user can be told.</summary>
public sealed record BrokenProject(string Id, string Reason);

/// <summary>Creates, lists, loads and saves projects under <c>&lt;workspace&gt;\projects\&lt;id&gt;\project.json</c>.</summary>
public sealed class ProjectStore
{
    public const string FileName = "project.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Korean names readable in the file
    };

    private readonly WorkspaceRoot _workspace;

    public ProjectStore(WorkspaceRoot workspace) => _workspace = workspace.EnsureCreated();

    public WorkspaceRoot Workspace => _workspace;

    public ProjectWorkspace FoldersOf(ProjectFile project) => new(_workspace, project.Id);

    /// <summary>Makes the folders and the first <c>project.json</c>.</summary>
    public ProjectFile Create(string name, string appVersion = "", string sdkVersion = "", DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.Now;
        var folders = _workspace.CreateProject(time.LocalDateTime);
        var project = new ProjectFile
        {
            Id = folders.Id,
            Name = string.IsNullOrWhiteSpace(name) ? folders.Id : name.Trim(),
            CreatedAt = time,
            UpdatedAt = time,
            Versions = new ProjectVersions { App = appVersion, Sdk = sdkVersion },
        };
        Write(project);
        return project;
    }

    /// <summary>
    /// A project that exists only in memory: nothing is written until <see cref="Materialize"/>. "새 프로젝트" starts
    /// with a draft so that a project the user never puts a model into leaves no folder behind.
    /// </summary>
    public ProjectFile NewDraft(string name, string appVersion = "", string sdkVersion = "", DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.Now;
        return new ProjectFile
        {
            Id = "",
            Name = name,
            CreatedAt = time,
            UpdatedAt = time,
            Versions = new ProjectVersions { App = appVersion, Sdk = sdkVersion },
        };
    }

    public static bool IsDraft(ProjectFile project) => project.Id.Length == 0;

    /// <summary>Gives a draft its folder and writes the first <c>project.json</c>. Does nothing for a stored project.</summary>
    public void Materialize(ProjectFile project, DateTimeOffset? now = null)
    {
        if (!IsDraft(project))
            return;
        var time = now ?? DateTimeOffset.Now;
        project.Id = _workspace.CreateProject(time.LocalDateTime).Id;
        project.CreatedAt = time;
        project.UpdatedAt = time;
        if (string.IsNullOrWhiteSpace(project.Name))
            project.Name = project.Id;
        Write(project);
    }

    /// <summary>Removes a project folder with everything in it. The folder must be inside this workspace.</summary>
    public void Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..")
            throw new ArgumentException("not a project id", nameof(id));
        var dir = Path.Combine(_workspace.ProjectsDir, id);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>All readable projects, most recently updated first. Unreadable ones go to <paramref name="broken"/>.</summary>
    public IReadOnlyList<ProjectFile> List(List<BrokenProject>? broken = null)
    {
        var result = new List<ProjectFile>();
        if (!Directory.Exists(_workspace.ProjectsDir))
            return result;
        foreach (var dir in Directory.EnumerateDirectories(_workspace.ProjectsDir))
        {
            var file = Path.Combine(dir, FileName);
            if (!File.Exists(file))
                continue; // a folder without project.json is not a project (for example the CLI's test runs)
            var id = Path.GetFileName(dir);
            try
            {
                var project = Read(file);
                if (project.Id != id)
                    project.Id = id; // the folder is the truth when someone copied a project folder
                result.Add(project);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                broken?.Add(new BrokenProject(id, ex.Message));
            }
        }
        return result.OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public ProjectFile? Load(string id)
    {
        var file = Path.Combine(_workspace.ProjectsDir, id, FileName);
        return File.Exists(file) ? Read(file) : null;
    }

    public void Save(ProjectFile project, DateTimeOffset? now = null)
    {
        project.UpdatedAt = now ?? DateTimeOffset.Now;
        Write(project);
    }

    private static ProjectFile Read(string file)
    {
        using var stream = File.OpenRead(file);
        var project = JsonSerializer.Deserialize<ProjectFile>(stream, Json) ?? throw new JsonException("empty project file");
        if (project.Schema > ProjectFile.CurrentSchema)
            throw new NotSupportedException($"project schema {project.Schema} is newer than this app understands ({ProjectFile.CurrentSchema})");
        return project;
    }

    /// <summary>Writes to a temporary file first so a crash never leaves a half-written project.</summary>
    private void Write(ProjectFile project)
    {
        var folders = new ProjectWorkspace(_workspace, project.Id).EnsureCreated();
        var file = Path.Combine(folders.Root, FileName);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(project, Json), new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Project-relative path with forward slashes, as stored in <c>project.json</c>.</summary>
    public string ToRelative(ProjectFile project, string absolutePath) =>
        Path.GetRelativePath(FoldersOf(project).Root, absolutePath).Replace('\\', '/');

    public string ToAbsolute(ProjectFile project, string relativePath) =>
        Path.GetFullPath(Path.Combine(FoldersOf(project).Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
