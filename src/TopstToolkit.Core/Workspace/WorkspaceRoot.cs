using System.Globalization;
using System.Security.Cryptography;

namespace TopstToolkit.Core.Workspace;

/// <summary>
/// The user's data folder (design §4.3). The whole path must be ASCII.
/// <code>
/// &lt;root&gt;\projects\&lt;id&gt;\...      one folder per project
/// &lt;root&gt;\cache\pycache\           -X pycache_prefix
/// &lt;root&gt;\config\empty-model-config\  --model-config-root (must stay empty)
/// &lt;root&gt;\downloads\zoo\           the model zoo's weights (the components folder may be read-only, design §7.2)
/// </code>
/// What the user keeps (exports, board results) goes to <see cref="SaveFolders"/> in Documents instead.
/// </summary>
public sealed class WorkspaceRoot
{
    public const string FolderName = "TOPST-Toolkit";

    public WorkspaceRoot(string root)
    {
        var problem = NameRules.WorkspaceRootProblem(root);
        if (problem is not null)
            throw new ArgumentException(problem, nameof(root));
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string ProjectsDir => Path.Combine(Root, "projects");
    public string CacheDir => Path.Combine(Root, "cache");
    public string PycacheDir => Path.Combine(CacheDir, "pycache");
    public string EmptyModelConfigDir => Path.Combine(Root, "config", "empty-model-config");
    /// <summary>Files the app downloads while it is used (the model zoo's weights), kept so they are not downloaded twice.</summary>
    public string DownloadsDir => Path.Combine(Root, "downloads");

    public WorkspaceRoot EnsureCreated()
    {
        Directory.CreateDirectory(ProjectsDir);
        Directory.CreateDirectory(PycacheDir);
        Directory.CreateDirectory(EmptyModelConfigDir);
        // Anything here would be applied to matching network names by --model-config auto (feature/05).
        foreach (var entry in Directory.EnumerateFileSystemEntries(EmptyModelConfigDir))
        {
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
        return this;
    }

    public ProjectWorkspace CreateProject(DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var id = $"p-{stamp}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
            var project = new ProjectWorkspace(this, id);
            if (!Directory.Exists(project.Root))
                return project.EnsureCreated();
        }
        throw new IOException("Could not allocate a project folder.");
    }

    public ProjectWorkspace OpenProject(string id) => new ProjectWorkspace(this, id).EnsureCreated();

    /// <summary>
    /// Default location: <c>%LOCALAPPDATA%\TOPST-Toolkit</c> when that path is ASCII (a Korean user name makes it not),
    /// else <c>%PUBLIC%\Documents\TOPST-Toolkit</c>. Null when neither works; the app then asks the user.
    /// </summary>
    public static string? DefaultLocation(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var candidates = new List<string>();
        if (getEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
            candidates.Add(Path.Combine(local, FolderName));
        if (getEnvironmentVariable("PUBLIC") is { Length: > 0 } pub)
            candidates.Add(Path.Combine(pub, "Documents", FolderName));
        return candidates.FirstOrDefault(c => NameRules.WorkspaceRootProblem(c) is null);
    }
}
