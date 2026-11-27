using System.IO;
using TopstToolkit.Core.Components;
using TopstToolkit.Core.Export;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Projects;
using TopstToolkit.Core.Settings;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine;
using TopstToolkit.Engine.Helpers;

namespace TopstToolkit.App.Services;

/// <summary>
/// What every screen layout shares (decision D9): settings, the workspace, the project store and the engine.
/// View models take this; nothing here knows which layout is on screen.
/// </summary>
public sealed class AppServices
{
    private AppServices(AppSettings settings, WorkspaceRoot workspace, EngineLayout? engineLayout)
    {
        Settings = settings;
        Workspace = workspace;
        Projects = new ProjectStore(workspace);
        Diagnostics = new DiagnosticsPackager(Projects);
        UseEngine(engineLayout);
    }

    public AppSettings Settings { get; }
    public WorkspaceRoot Workspace { get; }
    public ProjectStore Projects { get; }

    /// <summary>
    /// Null when no engine runtime was found: not next to the app, not in the repository and not installed by the app into
    /// its components folder (design D12, <see cref="ComponentsRoot"/>).
    /// </summary>
    public EngineLayout? EngineLayout { get; private set; }
    public EngineSession? Engine { get; private set; }

    public DataPreparer Data { get; private set; } = null!;
    public BoardPackager Packager { get; private set; } = null!;
    public DiagnosticsPackager Diagnostics { get; }

    /// <summary>Raised after <see cref="RefreshEngine"/> found a different engine (or none).</summary>
    public event EventHandler? EngineChanged;

    private void UseEngine(EngineLayout? engineLayout)
    {
        EngineLayout = engineLayout;
        Engine = engineLayout is not null && engineLayout.FindMissing().Count == 0 ? new EngineSession(engineLayout, Workspace) : null;
        Data = new DataPreparer(Projects, engineLayout);
        Packager = new BoardPackager(Projects, engineLayout);
    }

    /// <summary>Where the engine and the board tools are installed: the folder the user chose, or the default (design D12).</summary>
    public string? ComponentsLocation => ComponentsRoot.DefaultLocation(configured: Settings.ComponentsRoot);

    /// <summary>Looks for the engine again, after components were installed, moved or removed. True when one is usable now.</summary>
    public bool RefreshEngine()
    {
        UseEngine(ComponentsRoot.FindEngine(configured: Settings.ComponentsRoot));
        LastEngineCheck = null;
        EngineChanged?.Invoke(this, EventArgs.Empty);
        return Engine is not null;
    }

    /// <summary>File pickers and shell actions; replaced in screen captures.</summary>
    public DialogService Dialogs { get; set; } = new();

    /// <summary>The last engine self-check, kept for the settings screen.</summary>
    public EngineCheckReport? LastEngineCheck { get; private set; }

    public static string AppVersion =>
        typeof(AppServices).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public const string SdkVersion = ProjectBuilder.SdkVersion;

    public bool BoardToolsInstalled => EngineLayout?.FindCrossGcc() is not null;

    /// <param name="workspaceOverride">Use this folder instead of the configured workspace (screen captures, tests).</param>
    /// <param name="settingsPath">Read and save the settings here instead of the user's own file (tests).</param>
    public static AppServices Create(string? workspaceOverride = null, string? settingsPath = null)
    {
        var settings = AppSettings.Load(settingsPath);
        var root = workspaceOverride ?? settings.WorkspaceRoot ?? WorkspaceRoot.DefaultLocation()
                   ?? throw new InvalidOperationException("작업 공간으로 쓸 수 있는 위치를 찾지 못했어요. 영문 경로를 직접 지정해 주세요.");
        return new AppServices(settings, new WorkspaceRoot(root), ComponentsRoot.FindEngine(configured: settings.ComponentsRoot));
    }

    /// <summary>Imports the SDK once (2–5 s) and reports versions and problems. Null when there is no engine.</summary>
    public async Task<EngineCheckReport?> CheckEngineAsync(CancellationToken ct = default)
    {
        if (Engine is null)
            return null;
        var folders = Workspace.OpenProject("engine-check");
        var result = await Engine.For(folders).EngineCheckAsync(ct).ConfigureAwait(false);
        LastEngineCheck = result.Value;
        return result.Value;
    }
}
