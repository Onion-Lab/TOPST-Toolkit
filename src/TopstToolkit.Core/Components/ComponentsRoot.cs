using System.Text.Json;
using System.Text.Json.Serialization;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine;

namespace TopstToolkit.Core.Components;

/// <summary>
/// Where the app installs its components (design D12). Installed by Setup (design §7.2): in the app's own folder,
/// <c>C:\Program Files\TOPST Toolkit\components</c>, installed and removed with the app. The double-click folder:
/// <c>&lt;LOCALAPPDATA&gt;\TOPST-Toolkit\components</c>, or the public fallback the workspace uses, so the path is ASCII as
/// the toolkit requires. Laid out like the app folder: <c>runtime\</c> (the engine) and <c>toolchain\</c> (board tools),
/// so <see cref="EngineLayout"/> works unchanged.
/// </summary>
public static class ComponentsRoot
{
    /// <summary>Overrides the location; when set, only that folder is searched for an engine (a clean-PC test in a dev tree).</summary>
    public const string EnvironmentVariable = "TOPST_COMPONENTS_ROOT";
    public const string FolderName = "components";

    /// <summary>
    /// Written next to <c>TopstToolkit.exe</c> by Setup (<c>build\installer\setup.json</c>): the app was installed, and its
    /// components belong in <c>&lt;app&gt;\components</c>, where Setup put them and the uninstaller removes them.
    /// </summary>
    public const string SetupMarker = "setup.json";

    /// <summary>
    /// <c>&lt;app&gt;\components</c> when Setup installed the app and the toolkit can work there (ASCII, short enough);
    /// null for the double-click folder, a development build, or an install folder with Korean letters in its path.
    /// </summary>
    public static string? InAppFolder(string? appDirectory = null)
    {
        var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory));
        if (!File.Exists(Path.Combine(app, SetupMarker)))
            return null;
        var folder = Path.Combine(app, FolderName);
        return NameRules.IsAscii(folder) && folder.Length <= MaxLocationLength ? folder : null;
    }

    /// <summary>
    /// The longest folder path the components can go into. The deepest file the install writes is 163 characters below
    /// it (a test file inside torch, measured 2026-11-22), 8 more while it is assembled in <c>runtime.partial</c>, and
    /// Windows paths stop at 260.
    /// </summary>
    public const int MaxLocationLength = 80;

    /// <summary>
    /// Where the components are: the folder <see cref="EnvironmentVariable"/> names (tests), else the app's own folder when
    /// Setup installed it (<see cref="InAppFolder"/>), else the folder the user chose (<paramref name="configured"/>, from
    /// the settings), else <c>&lt;default workspace&gt;\components</c>.
    /// </summary>
    /// <param name="appDirectory">The app's folder (tests); the running app's when null.</param>
    public static string? DefaultLocation(Func<string, string?>? getEnvironmentVariable = null, string? configured = null, string? appDirectory = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        if (getEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } custom)
            return Path.GetFullPath(custom);
        if (InAppFolder(appDirectory) is { } setup)
            return setup;
        if (configured is { Length: > 0 } chosen)
            return Path.GetFullPath(chosen);
        return WorkspaceRoot.DefaultLocation(getEnvironmentVariable) is { } workspace ? Path.Combine(workspace, FolderName) : null;
    }

    /// <summary>The location is fixed by <see cref="EnvironmentVariable"/>, so the user cannot change it.</summary>
    public static bool IsForced(Func<string, string?>? getEnvironmentVariable = null) =>
        (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(EnvironmentVariable) is { Length: > 0 };

    /// <summary>The user cannot move the components: a test fixed them, or they belong to the installed app (Setup chose the place).</summary>
    public static bool IsFixed(Func<string, string?>? getEnvironmentVariable = null, string? appDirectory = null) =>
        IsForced(getEnvironmentVariable) || InAppFolder(appDirectory) is not null;

    /// <summary>
    /// The engine the app uses: next to the app or in the repository (<see cref="EngineLayout.Discover"/>), else the one it
    /// installed. With <see cref="EnvironmentVariable"/> set, only the components folder counts.
    /// </summary>
    public static EngineLayout? FindEngine(string? startDirectory = null, Func<string, string?>? getEnvironmentVariable = null, string? configured = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var root = DefaultLocation(getEnvironmentVariable, configured, startDirectory);
        if (IsForced(getEnvironmentVariable))
            return EngineLayout.InComponents(root, startDirectory);
        return EngineLayout.Discover(startDirectory) ?? EngineLayout.InComponents(root, startDirectory);
    }

    /// <summary>Why a folder the user picked cannot hold the components, in plain Korean, or null when it can.</summary>
    public static string? LocationProblem(string path, string? appDirectory = null)
    {
        if (!Path.IsPathFullyQualified(path))
            return "전체 경로(예: D:\\TOPST-Components)로 골라 주세요.";
        if (!NameRules.IsAscii(path))
            return "설치 위치에는 영문 · 숫자로 된 경로만 쓸 수 있어요. 한글이 없는 위치를 골라 주세요.";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length > MaxLocationLength)
            return $"경로가 너무 길어요. {MaxLocationLength}자 이하인 위치를 골라 주세요.";
        if (appDirectory is not null && IsSameOrUnder(full, appDirectory))
            return "프로그램 폴더 안에는 설치할 수 없어요. 프로그램을 새로 받으면 함께 지워져요.";
        return null;
    }

    internal static bool IsSameOrUnder(string path, string folder)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary><c>component.json</c>, written into a component folder when its install succeeded.</summary>
public sealed class InstalledComponent
{
    public const string FileName = "component.json";

    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("installedAt")] public DateTimeOffset InstalledAt { get; init; }
    [JsonPropertyName("app")] public string App { get; init; } = "";
    /// <summary>The SHA-256 of every file the install used, by download id; differs from the catalog after an update.</summary>
    [JsonPropertyName("downloads")] public Dictionary<string, string> Downloads { get; init; } = [];
    /// <summary>The SHA-256 of each pip lock file the install used (since 2026-11-25; absent in older records).</summary>
    [JsonPropertyName("locks")] public Dictionary<string, string>? Locks { get; init; }
    [JsonPropertyName("smoke")] public string? Smoke { get; init; }
    [JsonPropertyName("files")] public int Files { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }

    public static InstalledComponent? Read(string componentFolder)
    {
        var path = Path.Combine(componentFolder, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<InstalledComponent>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What a folder picked as the new components location holds (<see cref="ComponentInstaller.Inspect"/>).</summary>
public enum LocationContents
{
    /// <summary>Nothing, or the folder does not exist yet: the components can go there.</summary>
    Empty,
    /// <summary>Components of an earlier install (and only that): the app can use them as they are.</summary>
    Components,
    /// <summary>Other files: not used, so nothing of the user's gets mixed up with the components.</summary>
    Other,
}

public enum ComponentState
{
    /// <summary>Not there.</summary>
    Missing,
    /// <summary>Every required file is there.</summary>
    Installed,
    /// <summary>The folder is there but required files are missing (an install that was interrupted or files removed).</summary>
    Broken,
}
