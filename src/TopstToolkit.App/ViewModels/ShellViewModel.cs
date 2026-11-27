using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Presets;
using TopstToolkit.Core.Projects;
using TopstToolkit.Core.Workspace;

namespace TopstToolkit.App.ViewModels;

/// <summary>State of a pipeline step as every layout shows it: dot, chip, colour.</summary>
public enum StepState
{
    Todo,
    Ok,
    Running,
    Warn,
    Bad,
}

/// <summary>One destination: a project step (numbered), a tool or an app page.</summary>
public partial class NavItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    /// <summary>Step number ("1".."7") for project steps, null otherwise.</summary>
    public string? Number { get; init; }
    public IconGlyph Glyph { get; init; }
    public bool IsStep => Number is not null;

    [ObservableProperty] private StepState _state = StepState.Todo;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isEnabled = true;
}

/// <summary>A page that is planned but not built yet; keeps navigation whole while the app grows.</summary>
public sealed class PlaceholderViewModel(string title, string description)
{
    public string Title { get; } = title;
    public string Description { get; } = description;
}

/// <summary>Pages that read the project or the disk again each time they come on screen.</summary>
public interface IPageViewModel
{
    void OnShown();
}

/// <summary>One line of the log drawer. <see cref="IsUpdate"/> marks a progress line that the next one replaces.</summary>
public sealed record LogLine(string Text, bool IsUpdate);

/// <summary>A label and a value, for small fact tables.</summary>
public sealed record FactRow(string Label, string Value, bool Mono = false);

/// <summary>
/// Navigation, the open project and the running task. Layout-independent (decision D9): layout A's rail, a pipeline
/// strip (B) or a stepper (C) all show <see cref="Steps"/>, <see cref="CurrentPage"/> and the task properties.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private const int MaxLogLines = 600;

    private readonly AppServices _services;
    private readonly Dictionary<string, object> _projectPages = new();
    private readonly Dictionary<string, object> _toolPages = new();
    private readonly ConcurrentQueue<LogLine> _pendingLog = new();
    private readonly DispatcherTimer _logTimer;

    public ShellViewModel(AppServices services)
    {
        _services = services;
        HomeItem = new NavItem { Key = "home", Label = "홈", Glyph = IconGlyph.Home };
        Steps =
        [
            new NavItem { Key = "model", Label = "모델", Number = "1" },
            new NavItem { Key = "config", Label = "설정", Number = "2" },
            new NavItem { Key = "data", Label = "데이터", Number = "3" },
            new NavItem { Key = "build", Label = "빌드", Number = "4" },
            new NavItem { Key = "result", Label = "결과", Number = "5" },
            new NavItem { Key = "perf", Label = "성능", Number = "6" },
            new NavItem { Key = "export", Label = "내보내기", Number = "7" },
        ];
        Tools =
        [
            new NavItem { Key = "zoo", Label = "모델 모음", Glyph = IconGlyph.Grid },
            new NavItem { Key = "check", Label = "모델 점검", Glyph = IconGlyph.Shield },
            new NavItem { Key = "board", Label = "보드", Glyph = IconGlyph.Chip },
        ];
        SettingsItem = new NavItem { Key = "settings", Label = "설정", Glyph = IconGlyph.Settings };
        Board = new BoardConnection(services);
        Board.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BoardConnection.IsConnected))
                OnPropertyChanged(nameof(ShowsBoardOffPill));
        };
        Home = new HomeViewModel(services, this);
        Components = new ComponentsViewModel(services, this);
        _update = new UpdateViewModel(services, this, TopstToolkit.Core.Updates.UpdateSource.Find(), TopstToolkit.Core.Updates.SetupRecord.Read());
        // tool output arrives in bursts from a background thread; the drawer takes it in at a steady pace
        _logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => FlushLog(), Dispatcher.CurrentDispatcher);
        RefreshStepStates(); // no project yet: the steps are closed
        RefreshIdleStatus();
        Navigate(HomeItem);
    }

    public AppServices Services => _services;
    public NavItem HomeItem { get; }
    public ObservableCollection<NavItem> Steps { get; }
    public ObservableCollection<NavItem> Tools { get; }
    public NavItem SettingsItem { get; }
    public HomeViewModel Home { get; }

    /// <summary>The board the app is connected to (도구 › 보드); the header shows it on every page once connected.</summary>
    public BoardConnection Board { get; }

    /// <summary>The header pill: the board when connected; "보드 연결 안 됨" only on the board page itself.</summary>
    public bool ShowsBoardOffPill => !Board.IsConnected && CurrentItem?.Key == "board";

    [RelayCommand]
    private void OpenBoard() => NavigateTo("board");

    /// <summary>App updates (design §7.5): the pill in the header, the card in 설정 › 정보.</summary>
    [ObservableProperty] private UpdateViewModel _update;

    /// <summary>The header pill: 설정 › 정보, where the update card is.</summary>
    [RelayCommand]
    private void OpenUpdate()
    {
        NavigateTo("settings");
        (CurrentPage as SettingsViewModel)?.ShowAboutTab();
    }

    /// <summary>
    /// What the app installs from the official sources (design D12). Shown in place of Home while there is no engine, and
    /// while an install runs or after it failed; also the list in 설정.
    /// </summary>
    public ComponentsViewModel Components { get; }

    /// <summary>The build of the open project. It lives as long as the project is open, so a build keeps running while other pages show.</summary>
    public BuildViewModel? Build { get; private set; }

    [ObservableProperty] private object? _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBoardOffPill))]
    private NavItem? _currentItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(ProjectName), nameof(ProjectCaption))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private ProjectFile? _project;

    public bool HasProject => Project is not null;
    public string ProjectName => Project?.Name ?? "프로젝트 열기";

    /// <summary>"yolov5m.onnx · 객체 검출 · YOLOv5 · 640×640" next to the project name.</summary>
    public string ProjectCaption
    {
        get
        {
            if (Project is null) return "";
            var parts = new List<string>();
            if (Project.Model is { } m)
            {
                parts.Add(ModelTitle(Project));
                if (!string.IsNullOrEmpty(Project.Task)) parts.Add(PresetCatalog.DescribeKind(Project.Task, Project.Preset));
                if (InputSizeOf(m.Facts) is { } size) parts.Add(size);
            }
            else
            {
                parts.Add("모델을 가져오면 시작합니다");
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The name a model goes by on screen: the zoo's name ("YOLOv8n"), else the file the user brought in. Never the copy
    /// the build uses: the app's cut writes "&lt;name&gt;_extracted.onnx", and "_extracted" is the toolkit's naming, which
    /// means nothing to someone who never ran its scripts (user feedback 2026-11-23) — so it is dropped from any name.
    /// </summary>
    public static string ModelTitle(ProjectFile project)
    {
        if (project.Zoo is { Display.Length: > 0 } zoo)
            return zoo.Display;
        if (project.Model is not { } m)
            return "";
        var name = System.IO.Path.GetFileName(string.IsNullOrEmpty(m.Source) ? m.Original ?? m.Staged : m.Source);
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        const string extracted = "_extracted";
        return stem.EndsWith(extracted, StringComparison.OrdinalIgnoreCase) && stem.Length > extracted.Length
            ? stem[..^extracted.Length] + System.IO.Path.GetExtension(name)
            : name;
    }

    /// <summary>"640×640" from an NCHW input, when the model says so.</summary>
    public static string? InputSizeOf(ModelFacts? facts)
    {
        if (facts?.Inputs.FirstOrDefault()?.Shape is { Count: 4 } shape && int.TryParse(shape[2], out var h) && int.TryParse(shape[3], out var w))
            return $"{w}×{h}";
        return null;
    }

    // ---- the task bar at the bottom
    [ObservableProperty] private string _taskTitle = "준비됨";
    [ObservableProperty] private string _taskDetail = "";
    [ObservableProperty] private string _taskClock = "";
    [ObservableProperty] private StepState _taskState = StepState.Ok;
    [ObservableProperty] private double _taskProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private bool _isBusy;

    /// <summary>A build is running: the header offers "빌드 취소" instead of "빌드".</summary>
    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private bool _isLogOpen;
    [ObservableProperty] private string _engineCaption = "";

    public ObservableCollection<LogLine> Log { get; } = [];

    public void BeginTask(string title, string detail = "")
    {
        IsBusy = true;
        TaskState = StepState.Running;
        TaskTitle = title;
        TaskDetail = detail;
        TaskClock = "";
        TaskProgress = 0;
    }

    public void EndTask(string title, string detail = "", StepState state = StepState.Ok)
    {
        FlushLog();
        IsBusy = false;
        TaskState = state;
        TaskTitle = title;
        TaskDetail = detail;
        TaskProgress = 0;
    }

    /// <summary>Queues a line for the log drawer. Safe to call from any thread.</summary>
    public void PostLog(string text, bool isUpdate = false) => _pendingLog.Enqueue(new LogLine(text, isUpdate));

    public void ClearLog()
    {
        while (_pendingLog.TryDequeue(out _)) { }
        Log.Clear();
    }

    /// <summary>Moves queued lines into the drawer. A run of progress rewrites collapses into its last line.</summary>
    public void FlushLog()
    {
        if (_pendingLog.IsEmpty)
            return;
        while (_pendingLog.TryDequeue(out var line))
        {
            if (line.IsUpdate && Log.Count > 0 && Log[^1].IsUpdate)
                Log[^1] = line;
            else
                Log.Add(line);
        }
        while (Log.Count > MaxLogLines)
            Log.RemoveAt(0);
    }

    // ---- navigation
    [RelayCommand]
    private void Navigate(NavItem? item)
    {
        if (item is null || !item.IsEnabled || (item.IsStep && Project is null))
            return;
        foreach (var other in AllItems())
            other.IsSelected = ReferenceEquals(other, item);
        CurrentItem = item;
        var page = PageFor(item.Key);
        (page as IPageViewModel)?.OnShown();
        CurrentPage = page;
    }

    public void NavigateTo(string key) => Navigate(Find(key));

    private object PageFor(string key)
    {
        if (key == "home")
        {
            if (_services.Engine is null || Components.NeedsSetup || Components.IsInstalling || Components.IsFailed)
                return Components;
            Home.Refresh();
            return Home;
        }
        var cache = key is "check" or "settings" or "zoo" or "board" ? _toolPages : _projectPages;
        if (cache.TryGetValue(key, out var cached))
            return cached;
        object page = key switch
        {
            "model" when Project is not null => new ModelStepViewModel(_services, this, Project),
            "config" when Project is not null => new ConfigViewModel(_services, this, Project),
            "data" when Project is not null => new DataViewModel(_services, this, Project),
            "build" when Build is not null => Build,
            "result" when Project is not null => new ResultViewModel(_services, this, Project),
            "perf" when Project is not null => new PerfViewModel(_services, this, Project),
            "export" when Project is not null => new ExportViewModel(_services, this, Project),
            "check" => new ModelCheckViewModel(_services, this),
            "zoo" => new ZooViewModel(_services, this),
            "board" => new BoardViewModel(_services, this),
            "settings" => new SettingsViewModel(_services, this),
            _ => Home,
        };
        if (!ReferenceEquals(page, Home))
            cache[key] = page;
        return page;
    }

    /// <summary>
    /// "자를 곳": the graph of the open project's model, where the cut is seen and chosen (design §5.7). It belongs to
    /// step 1, so that step stays selected; "← 1 모델" on the page leads back.
    /// </summary>
    public void ShowCutGraph()
    {
        if (Project?.Model is null)
            return;
        foreach (var other in AllItems())
            other.IsSelected = other.Key == "model";
        CurrentItem = Find("model");
        if (!_projectPages.TryGetValue("cut-graph", out var page))
            _projectPages["cut-graph"] = page = new GraphCutViewModel(_services, this, Project);
        (page as IPageViewModel)?.OnShown();
        CurrentPage = page;
    }

    /// <summary>The components page (설치 · 다시 설치), under "홈" in the rail.</summary>
    public void ShowComponents()
    {
        foreach (var other in AllItems())
            other.IsSelected = ReferenceEquals(other, HomeItem);
        CurrentItem = HomeItem;
        Components.OnShown();
        CurrentPage = Components;
    }

    /// <summary>Call after components were installed or removed: the commands, the engine card and the caption follow.</summary>
    public void OnEngineChanged()
    {
        BuildCommand.NotifyCanExecuteChanged();
        Home.RefreshEngineFacts();
        RefreshIdleStatus();
    }

    /// <summary>
    /// The task bar while nothing runs (mockup-components): "엔진 없음" with a warning dot until the engine is installed,
    /// then "준비됨". The engine caption on the right is left empty then, so the bar does not say it twice.
    /// </summary>
    public void RefreshIdleStatus()
    {
        if (IsBusy)
            return;
        if (_services.Engine is null)
        {
            TaskState = StepState.Warn;
            TaskTitle = "엔진 없음";
            TaskDetail = "";
            EngineCaption = "";
        }
        else if (TaskTitle == "엔진 없음")
        {
            TaskState = StepState.Ok;
            TaskTitle = "준비됨";
        }
    }

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    public IEnumerable<NavItem> AllItems() => new[] { HomeItem }.Concat(Steps).Concat(Tools).Append(SettingsItem);

    public NavItem? Find(string key) => AllItems().FirstOrDefault(i => i.Key == key);

    // ---- the open project
    /// <summary>Opens a project. Refused while a task runs: a build belongs to the project it started in.</summary>
    public bool OpenProject(ProjectFile project)
    {
        if (IsBusy)
            return false;
        _projectPages.Clear();
        Project = project;
        Build = new BuildViewModel(_services, this, project);
        RefreshStepStates();
        if (!ProjectStore.IsDraft(project))
        {
            // old tool logs and working folders go when a project is opened (설정 → 실행 기록 보관)
            var folders = _services.Projects.FoldersOf(project);
            var days = _services.Settings.LogRetentionDays;
            _ = Task.Run(() => Housekeeping.Clean(folders, days));
        }
        var landing = project.Model is null ? "model"
            : project.Headline is { IsBuilt: true, NeedsRebuild: false } ? "result"
            : project.Headline.IsBuilt ? "build" : "model";
        Navigate(Find(landing));
        return true;
    }

    /// <summary>
    /// Starts a project that is not on disk yet; it gets its folder when a model is brought in.
    /// With <paramref name="modelPath"/> the model is brought in right away (dropped on the home screen).
    /// </summary>
    public bool StartNewProject(string? modelPath = null)
    {
        if (!OpenProject(_services.Projects.NewDraft("새 프로젝트", AppServices.AppVersion, AppServices.SdkVersion)))
            return false;
        if (modelPath is not null && CurrentPage is ModelStepViewModel model)
            _ = model.ImportAsync(modelPath);
        return true;
    }

    public void CloseProject()
    {
        if (IsBusy)
            return;
        _projectPages.Clear();
        Project = null;
        Build = null;
        RefreshStepStates();
        Navigate(HomeItem);
    }

    /// <summary>Call after the project object changed (model imported, build finished): header, step dots and commands follow.</summary>
    public void OnProjectChanged()
    {
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(ProjectCaption));
        RefreshStepStates();
        BuildCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Call after a setting that the build depends on changed: saves, and marks an existing build as out of date.</summary>
    public void MarkSettingsChanged()
    {
        if (Project is null || ProjectStore.IsDraft(Project))
            return;
        if (Project.Headline.IsBuilt)
            Project.Headline.NeedsRebuild = true;
        _services.Projects.Save(Project);
        OnProjectChanged();
    }

    // ---- the one primary action of the header
    private bool CanBuild() => Project?.Model is { IsReady: true } && !IsBusy && _services.Engine is not null;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        if (Build is null)
            return;
        NavigateTo("build");
        await Build.BuildAsync();
    }

    [RelayCommand]
    private void CancelBuild() => Build?.CancelCommand.Execute(null);

    /// <summary>Step dots and which steps can be opened, from what project.json records. A running build overrides its own dot.</summary>
    public void RefreshStepStates()
    {
        // a model that still has to be cut, or that no cut helps, keeps the later steps closed (design §5.7)
        var ready = Project?.Model is { IsReady: true };
        foreach (var step in Steps)
        {
            step.State = StepState.Todo;
            step.IsEnabled = ready || step.Key == "model";
        }
        if (Project is null)
            return;

        var hasModel = Project.Model is not null;
        if (hasModel) Steps[0].State = Project.Model!.IsBlocked ? StepState.Bad : Project.Model.NeedsCut ? StepState.Warn : StepState.Ok;
        if (!ready)
            return;
        if (hasModel && !string.IsNullOrEmpty(Project.Task)) Steps[1].State = StepState.Ok;
        if (hasModel) Steps[2].State = StepState.Ok;
        var built = Project.Headline.IsBuilt;
        var stale = Project.Headline.NeedsRebuild;
        var failed = Project.Steps.Values.Any(s => s.Status == "failed");
        Steps[3].State = IsBuilding ? StepState.Running : built ? (stale ? StepState.Warn : StepState.Ok) : failed ? StepState.Bad : StepState.Todo;
        Steps[4].State = built && !stale ? StepState.Ok : StepState.Todo;
        Steps[5].State = built && !stale && Project.Headline.InferenceTimeMs is not null ? StepState.Ok : StepState.Todo;
        Steps[6].State = Project.Headline.ExportedAt is not null && !stale ? StepState.Ok : StepState.Todo;
    }
}
