using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopstToolkit.Core.Settings;

/// <summary>
/// Per-user app settings, kept outside the workspace (the workspace location is one of them):
/// <c>%APPDATA%\TOPST Toolkit\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Screen layout (decision D9): <c>rail</c> (A), <c>pipeline</c> (B) or <c>focus</c> (C). Phase 1 ships <c>rail</c> only.</summary>
    public const string LayoutRail = "rail";
    public const string LayoutPipeline = "pipeline";
    public const string LayoutFocus = "focus";

    /// <summary>The layouts this build can show. B and C join this list when they are implemented.</summary>
    public static readonly IReadOnlyList<string> AvailableLayouts = [LayoutRail];

    public string? WorkspaceRoot { get; set; }

    /// <summary>Where the app installs the engine and the board tools (design D12); null = the default next to the workspace.</summary>
    public string? ComponentsRoot { get; set; }

    [JsonPropertyName("ui.layout")]
    public string Layout { get; set; } = LayoutRail;

    public int LogRetentionDays { get; set; } = 30;

    /// <summary>Where the last board package was written; the export screen starts there.</summary>
    public string? ExportFolder { get; set; }

    /// <summary>The folder for what the user keeps (exports, board results); null = <c>내 문서\TOPST Toolkit</c> (<see cref="Workspace.SaveFolders"/>).</summary>
    public string? SaveRoot { get; set; }

    /// <summary>The user agreed to Ultralytics' license in the model zoo (asked once, before the first Ultralytics model).</summary>
    public bool UltralyticsAgreed { get; set; }

    /// <summary>The board the 보드 screen connected to last (design §5.10): its address, the SSH user, its name.</summary>
    public string? BoardHost { get; set; }
    public string? BoardUser { get; set; }
    public string? BoardName { get; set; }

    /// <summary>"켤 때 새 버전 확인" (설정 › 정보, design §7.5): look at the update address when the app starts.</summary>
    [JsonPropertyName("update.check")]
    public bool UpdateCheck { get; set; } = true;

    /// <summary>When the app last looked for a new version ("오늘 14:02에 확인").</summary>
    [JsonPropertyName("update.checkedAt")]
    public DateTimeOffset? UpdateCheckedAt { get; set; }

    /// <summary>The stored layout when this build has it, otherwise the rail layout.</summary>
    [JsonIgnore]
    public string EffectiveLayout => AvailableLayouts.Contains(Layout) ? Layout : LayoutRail;

    /// <summary>The file these settings were read from, and are saved to (<see cref="DefaultPath"/>, or a test's own file).</summary>
    [JsonIgnore]
    public string? FilePath { get; private set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Tests: another settings file (with its own workspace) for the app and for every app it starts — the update test
    /// (design §7.5), where Setup starts the app again, never touches the user's own settings and projects.
    /// </summary>
    public const string PathVariable = "TOPST_SETTINGS";

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } test ? Path.GetFullPath(test)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TOPST Toolkit", "settings.json");

    /// <summary>Missing or unreadable settings give defaults; the app must always start.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        AppSettings? settings = null;
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        settings ??= new AppSettings();
        settings.FilePath = path;
        return settings;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
    }
}
