using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Components;
using TopstToolkit.Core.Text;

namespace TopstToolkit.App.ViewModels;

public enum ComponentsPhase
{
    /// <summary>What will be installed, from where, under which licenses; the user agrees and starts.</summary>
    Setup,
    Installing,
    Done,
    Failed,
}

/// <summary>Where a component the app uses comes from.</summary>
public enum ComponentSource
{
    Missing,
    /// <summary>Installed by the app into its components folder (design D12).</summary>
    Installed,
    /// <summary>In the app's own folder (the full double-click folder) or in the repository during development.</summary>
    Bundled,
    /// <summary>The components folder has it, but files are missing.</summary>
    Broken,
}

/// <summary>One line of the sources table: what is downloaded, from whom, and a link to its license.</summary>
public sealed record SourceRow(string Name, string Origin, string License, string? LicenseUrl, string Size)
{
    public bool HasLicenseUrl => LicenseUrl is not null;
    /// <summary>The license itself is not spelled out on the screen (the user, 2026-11-22): only a link to it.</summary>
    public string LinkText => Origin == "TOPST" ? "이용 약관 보기" : "라이선스 확인";
}

/// <summary>A component (engine, board tools) as the setup screen and the settings list show it.</summary>
public partial class ComponentRowViewModel : ObservableObject
{
    public ComponentRowViewModel(ComponentSpec spec, IReadOnlyList<SourceRow> sources)
    {
        Spec = spec;
        Sources = sources;
        // the model zoo's packages are not picked on the first-run screen: they come when a model is first chosen
        _isSelected = spec.Required || spec.Setup;
    }

    public ComponentSpec Spec { get; }
    public string Id => Spec.Id;
    public string Title => Spec.Title;
    public string Summary => Spec.Summary;
    public bool IsRequired => Spec.Required;
    public string RequiredText => Spec.Required ? "필수" : "선택";
    public string DownloadText => ComponentSteps.Size(Spec.DownloadBytes);
    public string InstalledSizeText => ComponentSteps.Size(Spec.InstalledBytes);
    public IReadOnlyList<SourceRow> Sources { get; }
    public IconGlyph Glyph => Spec.Id switch { ComponentCatalog.BoardTools => IconGlyph.Chip, ComponentCatalog.Zoo => IconGlyph.Grid, _ => IconGlyph.Package };

    /// <summary>Picked for the install. The engine cannot be left out; what is there already is not installed again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosable))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosable), nameof(NeedsInstall), nameof(StateText), nameof(StateKind), nameof(CanReinstall), nameof(CanRemove), nameof(CanInstall))]
    private ComponentSource _source;

    /// <summary>"2026-11-22" for a component the app installed.</summary>
    [ObservableProperty] private string _installedOn = "";
    /// <summary>What is in it, for the list in 설정 ("ENLIGHT SDK 0.9.9 · torch 1.12.0+cpu · onnx 1.12.0").</summary>
    [ObservableProperty] private string _contents = "";
    /// <summary>What the smoke test of the install showed, for the done screen ("gcc 9.2.1 (arm-9.10) · aarch64 공유 라이브러리 빌드 확인").</summary>
    [ObservableProperty] private string _checked = "";
    [ObservableProperty] private string _sizeText = "—";

    public bool NeedsInstall => Source is ComponentSource.Missing or ComponentSource.Broken;
    public bool IsChoosable => !IsRequired && NeedsInstall;
    public bool CanReinstall => Source is ComponentSource.Installed or ComponentSource.Broken;
    public bool CanRemove => !IsRequired && Source is ComponentSource.Installed or ComponentSource.Broken;
    public bool CanInstall => Source == ComponentSource.Missing;

    public string StateText => Source switch
    {
        ComponentSource.Installed => "설치됨",
        ComponentSource.Bundled => "앱과 함께 있음",
        ComponentSource.Broken => "파일 빠짐",
        _ => "없음",
    };

    public ChipKind StateKind => Source switch
    {
        ComponentSource.Installed => ChipKind.Ok,
        ComponentSource.Bundled => ChipKind.Info,
        ComponentSource.Broken => ChipKind.Bad,
        _ => ChipKind.Muted,
    };
}

/// <summary>One line of the install screen (<see cref="InstallStepPlan"/>) and how far it is.</summary>
public partial class InstallStepViewModel(InstallStepPlan plan) : ObservableObject
{
    public InstallStepPlan Plan { get; } = plan;
    public string Title => Plan.Title;
    public string SizeText => Plan.Bytes > 0 ? ComponentSteps.Size(Plan.Bytes) : "";

    [ObservableProperty] private StepState _state;
    [ObservableProperty] private string _detail = plan.Detail ?? "";
    /// <summary>0..1 while a file downloads; -1 when there is no bar.</summary>
    [ObservableProperty] private double _fraction = -1;
    [ObservableProperty] private string _right = "";

    public bool HasBar => Fraction >= 0;
    partial void OnFractionChanged(double value) => OnPropertyChanged(nameof(HasBar));

    /// <summary>Bytes and time when this line's download was first seen, for "남은 시간" (lines can download side by side).</summary>
    public (long Bytes, TimeSpan At)? RateMark { get; set; }

    /// <summary>How much of the line counts for the overall bar: 1 done, its download's share while it runs, 0 before.</summary>
    public double Progress => State == StepState.Ok ? 1 : State == StepState.Running && Fraction >= 0 ? Fraction * ComponentSteps.BytesShare(Plan) : 0;
}

/// <summary>
/// "구성 요소" (design D12, docs/mockups/mockup-components.html): what the app installs from the official sources, the
/// install itself, and the list in 설정. Layout-independent; one instance lives as long as the shell.
/// </summary>
public partial class ComponentsViewModel : ObservableObject, IPageViewModel
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ComponentCatalog? _catalog;
    private ComponentInstaller? _installer;
    private readonly DispatcherTimer _clock;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Stopwatch _watch = new();
    private CancellationTokenSource? _cts;
    private IReadOnlyList<IReadOnlyList<InstallStepPlan>> _plans = [];
    private List<ComponentRowViewModel> _running = [];
    private int _component;

    public ComponentsViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ElapsedText = Clock(_watch.Elapsed), Dispatcher.CurrentDispatcher);
        var catalog = ComponentCatalog.Find();
        _catalog = catalog;
        var root = _services.ComponentsLocation;
        _location = root ?? "";
        if (catalog is null)
            Problem = "구성 요소 목록(engine\\components\\components.json)을 찾지 못했어요. TOPST Toolkit을 다시 설치해 주세요.";
        else if (root is null)
            Problem = "구성 요소를 설치할 영문 경로를 찾지 못했어요. 사용자 이름에 영문이 아닌 글자가 있으면 생길 수 있어요.";
        else
        {
            _installer = new ComponentInstaller(catalog, root, appVersion: AppServices.AppVersion);
            foreach (var spec in catalog.Components)
            {
                var row = new ComponentRowViewModel(spec, SourcesOf(spec, catalog.Directory));
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ComponentRowViewModel.IsSelected)) UpdateSummary(); };
                Rows.Add(row);
            }
        }
        RefreshStates();
    }

    public ObservableCollection<ComponentRowViewModel> Rows { get; } = [];
    public ObservableCollection<InstallStepViewModel> Steps { get; } = [];
    public ObservableCollection<ComponentRowViewModel> Installed { get; } = [];

    /// <summary>Why nothing can be installed (no catalog, no ASCII location); empty when all is well.</summary>
    public string Problem { get; } = "";
    public bool HasProblem => Problem.Length > 0;

    /// <summary>Where the components are or go: the default next to the workspace, or the folder the user chose.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreeSpaceText))]
    private string _location;

    /// <summary>Why the folder just picked cannot be used, or what happened to the move; empty otherwise.</summary>
    [ObservableProperty] private string _locationNote = "";
    [ObservableProperty] private bool _locationNoteIsWarning;

    /// <summary>A move to another folder runs (설정 › 구성 요소 or the setup screen).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeLocationCommand), nameof(InstallCommand))]
    private bool _isMoving;

    /// <summary>
    /// "영문 경로만 쓸 수 있어요 · 남은 공간 541 GB" under the location; for an app Setup installed, where they are instead
    /// ("TOPST Toolkit 설치 폴더 안이라 앱과 함께 지워져요").
    /// </summary>
    public string FreeSpaceText
    {
        get
        {
            var what = InAppFolder ? "TOPST Toolkit 설치 폴더 안에 있어 앱을 지우면 함께 지워져요" : "영문 경로만 쓸 수 있어요";
            try
            {
                if (Location.Length > 0 && Path.GetPathRoot(Location) is { Length: > 0 } drive && new DriveInfo(drive) is { IsReady: true } info)
                    return what + " · 남은 공간 " + ComponentSteps.Size(info.AvailableFreeSpace);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
            return what;
        }
    }

    /// <summary>
    /// The user cannot move the components: TOPST_COMPONENTS_ROOT fixes them (tests), or Setup installed the app and they
    /// belong in its folder (the place is chosen in Setup).
    /// </summary>
    public bool LocationIsFixed => ComponentsRoot.IsFixed();

    /// <summary>Setup installed the app: the components live in its folder (<c>C:\Program Files\TOPST Toolkit\components</c>).</summary>
    public bool InAppFolder => _installer is not null && ComponentsRoot.InAppFolder() is { } folder
                               && string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(_installer.Root), StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup), nameof(IsInstalling), nameof(IsDone), nameof(IsFailed))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(ChangeLocationCommand))]
    private ComponentsPhase _phase = ComponentsPhase.Setup;

    public bool IsSetup => Phase == ComponentsPhase.Setup;
    public bool IsInstalling => Phase == ComponentsPhase.Installing;
    public bool IsDone => Phase == ComponentsPhase.Done;
    public bool IsFailed => Phase == ComponentsPhase.Failed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _agreed;

    /// <summary>One line on the setup screen (the install was cancelled, a build runs).</summary>
    [ObservableProperty] private string _notice = "";

    // ---- setup summary
    [ObservableProperty] private string _downloadTotal = "";
    [ObservableProperty] private string _diskAfter = "";
    [ObservableProperty] private string _diskDuring = "";
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private bool _nothingToInstall;

    // ---- installing
    [ObservableProperty] private string _installTitle = "";
    [ObservableProperty] private string _currentTitle = "";
    [ObservableProperty] private string _currentCount = "";
    [ObservableProperty] private double _overall;
    [ObservableProperty] private string _overallText = "0%";
    [ObservableProperty] private string _elapsedText = "00:00";
    /// <summary>The line of the current component that is running (for screen captures).</summary>
    [ObservableProperty] private int _currentLine = -1;

    // ---- done / failed
    [ObservableProperty] private string _doneTitle = "준비됐어요";
    [ObservableProperty] private string _doneText = "";
    [ObservableProperty] private string _keptText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteKeptCommand))]
    private bool _hasKeptFiles;
    [ObservableProperty] private string _failTitle = "";
    /// <summary>The first sentence of the failure banner, in bold (mockup ④); <see cref="FailText"/> follows it.</summary>
    [ObservableProperty] private string _failHeadline = "";
    [ObservableProperty] private string _failText = "";
    [ObservableProperty] private string _failDetail = "";
    [ObservableProperty] private string _elapsedTotal = "";

    public bool EngineReady => _services.Engine is not null;

    /// <summary>Required components that are not there (both are required since 2026-11-22, the user's wish).</summary>
    private List<ComponentRowViewModel> MissingRequired() => [.. Rows.Where(r => r.IsRequired && r.NeedsInstall)];

    /// <summary>A required component is missing or broken: the app shows this page in place of home.</summary>
    public bool NeedsSetup => _installer is not null && MissingRequired().Count > 0;

    public string SetupTitle => !EngineReady ? "시작하기 전에 엔진을 설치해요"
        : MissingRequired() is { Count: > 0 } missing ? Korean.Object(Korean.List([.. missing.Select(r => r.Title)])) + " 설치해요"
        : "구성 요소 설치";
    public string SetupNote => "처음 한 번만 하면 됩니다";

    public void OnShown()
    {
        if (!IsInstalling)
            RefreshStates();
    }

    /// <summary>Where each component comes from now: the components folder, the app's folder, or nowhere.</summary>
    public void RefreshStates()
    {
        if (_installer is null)
            return;
        var layout = _services.EngineLayout;
        var gcc = layout?.FindCrossGcc();
        foreach (var row in Rows)
        {
            var state = _installer.StateOf(row.Id);
            var inUse = row.Id switch { ComponentCatalog.Engine => layout?.RuntimeRoot, ComponentCatalog.BoardTools => gcc, _ => null };
            var bundled = inUse is not null && !IsUnder(inUse, _installer.Root);
            row.Source = bundled ? ComponentSource.Bundled
                : state == ComponentState.Installed ? ComponentSource.Installed
                : state == ComponentState.Broken ? ComponentSource.Broken : ComponentSource.Missing;
            var record = row.Source is ComponentSource.Installed ? InstalledComponent.Read(_installer.FolderOf(row.Id)) : null;
            row.InstalledOn = record?.InstalledAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            row.Contents = row.Source switch
            {
                ComponentSource.Installed => ContentsOf(row.Id, record?.Smoke),
                ComponentSource.Bundled => row.Id == ComponentCatalog.Engine ? "앱 폴더의 엔진 (" + layout!.RuntimeRoot + ")" : "앱 폴더의 보드 도구",
                _ => row.Summary,
            };
            row.Checked = row.Source == ComponentSource.Installed ? CheckedOf(row.Id, record?.Smoke) : row.Contents;
            row.SizeText = record is not null ? ComponentSteps.Size(record.Bytes) : row.Source == ComponentSource.Missing ? "받을 양 " + row.DownloadText : "—";
            // the engine is always part of the install; an optional component that is there already is not offered again
            if (!row.IsChoosable)
                row.IsSelected = row.NeedsInstall;
        }
        UpdateKept();
        OnPropertyChanged(nameof(EngineReady));
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(SetupTitle));
        OnPropertyChanged(nameof(SetupNote));
        OnPropertyChanged(nameof(FreeSpaceText));
        UpdateSummary();
    }

    private IEnumerable<ComponentRowViewModel> ToInstall() => Rows.Where(r => r.NeedsInstall && (r.IsRequired || r.IsSelected));

    private void UpdateSummary()
    {
        var picked = ToInstall().ToList();
        var download = picked.Sum(r => r.Spec.DownloadBytes);
        var disk = picked.Sum(r => r.Spec.InstalledBytes);
        DownloadTotal = "약 " + ComponentSteps.Size(download);
        DiskAfter = "약 " + ComponentSteps.Size(disk);
        DiskDuring = "약 " + ComponentSteps.Size(download + disk);
        // measured 2026-11-22 with the files already downloaded: engine 105 s, board tools 17 s
        TimeText = picked.Any(r => r.Id == ComponentCatalog.Engine) ? "받는 시간 + 약 2분" : picked.Count > 0 ? "받는 시간 + 1분 안쪽" : "—";
        NothingToInstall = picked.Count == 0;
        SpaceWarning = picked.Count > 0 && FreeBytes(Location) is { } free && free < download + disk
            ? $"이 위치의 드라이브에 빈 공간이 모자라요(남은 공간 {ComponentSteps.Size(free)}). 다른 위치로 바꿔 주세요."
            : "";
        InstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Not enough room for the install at the location; empty when there is.</summary>
    [ObservableProperty] private string _spaceWarning = "";

    private bool CanInstall() => _installer is not null && Agreed && !IsInstalling && !IsMoving && ToInstall().Any();

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => RunAsync(ToInstall().ToList());

    /// <summary>Installs one component again (설정 › 구성 요소), with the consent given for the first install.</summary>
    [RelayCommand]
    private async Task ReinstallAsync(ComponentRowViewModel? row)
    {
        if (row is null || _installer is null || IsInstalling)
            return;
        if (!_services.Dialogs.Confirm("다시 설치", $"{Korean.Object(row.Title)} 공식 출처에서 다시 받아 설치합니다. 받아 둔 파일이 있으면 그것을 씁니다. 계속할까요?"))
            return;
        _shell.ShowComponents();
        await RunAsync([row]);
    }

    /// <summary>"설치" next to a missing component in 설정: the setup screen with that component picked.</summary>
    [RelayCommand]
    private void Choose(ComponentRowViewModel? row)
    {
        if (row is null || IsInstalling)
            return;
        row.IsSelected = true;
        Phase = ComponentsPhase.Setup;
        _shell.ShowComponents();
    }

    [RelayCommand]
    private async Task RemoveAsync(ComponentRowViewModel? row)
    {
        if (row is null || _installer is null || row.IsRequired || IsInstalling)
            return;
        if (_shell.IsBusy)
        {
            Notice = "빌드가 끝난 뒤에 지울 수 있어요.";
            return;
        }
        if (!_services.Dialogs.Confirm("구성 요소 지우기", $"{Korean.Object(row.Title)} 지웁니다. 필요하면 다시 설치할 수 있어요. 지울까요?"))
            return;
        var error = await ComponentRuns.RemoveAsync(_installer, row.Id);
        Notice = error is null ? RightsDeclined : error.Length > 0 ? "지우지 못했어요. 그 파일을 쓰는 프로그램이 있는지 확인해 주세요." : "";
        _services.RefreshEngine();
        _shell.OnEngineChanged();
        RefreshStates();
    }

    /// <summary>The user said no when Windows asked for administrator rights (the components are in Program Files).</summary>
    private const string RightsDeclined = "프로그램 폴더 안이라 관리자 권한이 있어야 바꿀 수 있어요. 다시 하면 Windows가 다시 물어봅니다.";

    private void UpdateKept()
    {
        var bytes = _installer?.KeptBytes() ?? 0;
        KeptText = ComponentSteps.Size(bytes);
        HasKeptFiles = bytes > 0;
    }

    [RelayCommand(CanExecute = nameof(HasKeptFiles))]
    private async Task DeleteKeptAsync()
    {
        if (_installer is null || IsInstalling)
            return;
        if (!_services.Dialogs.Confirm("받아 둔 파일 지우기", $"다시 설치할 때 쓰려고 남겨 둔 파일({KeptText})을 지웁니다. 설치된 구성 요소는 그대로입니다. 지울까요?"))
            return;
        var error = await ComponentRuns.DeleteKeptAsync(_installer);
        Notice = error is null ? RightsDeclined : error.Length > 0 ? "일부 파일을 지우지 못했어요." : "";
        UpdateKept();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenLogs()
    {
        if (_installer is not null)
            _services.Dialogs.ShowInExplorer(_installer.LogsDirectory);
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (_installer is not null && Directory.Exists(_installer.Root))
            _services.Dialogs.ShowInExplorer(_installer.Root);
    }

    private bool CanChangeLocation() => _installer is not null && _catalog is not null && !LocationIsFixed && !IsInstalling && !IsMoving;

    /// <summary>
    /// "바꾸기" next to the location: a folder the user picks (ASCII, short enough, empty or holding the components of an
    /// earlier install). Whatever is installed or kept at the old place moves along, on the same drive by renaming.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeLocation))]
    private async Task ChangeLocationAsync()
    {
        if (_installer is null || _catalog is null)
            return;
        LocationNote = "";
        if (_shell.IsBusy)
        {
            ShowLocationNote("지금 하는 작업이 끝난 뒤에 바꿀 수 있어요.", warning: true);
            return;
        }
        var start = Directory.Exists(Location) ? Location : Path.GetDirectoryName(Location);
        if (_services.Dialogs.PickFolder("구성 요소를 설치할 폴더 고르기", start) is not { } picked)
            return;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked));
        var current = Path.TrimEndingDirectorySeparator(_installer.Root);
        if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            return;
        if (ComponentsRoot.LocationProblem(target, AppContext.BaseDirectory) is { } problem)
        {
            ShowLocationNote(problem, warning: true);
            return;
        }
        if (IsUnder(target, current) || IsUnder(current, target))
        {
            ShowLocationNote("지금 위치의 안쪽이나 바깥쪽 폴더는 고를 수 없어요. 다른 폴더를 골라 주세요.", warning: true);
            return;
        }

        var stored = _installer.StoredBytes();
        switch (ComponentInstaller.Inspect(target, _catalog))
        {
            case LocationContents.Other:
                ShowLocationNote("고른 폴더에 다른 파일이 있어요. 빈 폴더를 골라 주세요(폴더 고르는 창에서 새 폴더를 만들 수 있어요).", warning: true);
                return;
            case LocationContents.Components when stored > 0:
                ShowLocationNote("고른 폴더에 이미 다른 구성 요소가 있어요. 빈 폴더를 골라 주세요.", warning: true);
                return;
            case LocationContents.Components:
                UseLocation(target);
                ShowLocationNote("고른 폴더에 있던 구성 요소를 씁니다.", warning: false);
                return;
        }
        if (stored == 0)
        {
            UseLocation(target);
            return;
        }

        // something to move: room on the other drive, and the user's yes
        var sameDrive = string.Equals(Path.GetPathRoot(target), Path.GetPathRoot(current), StringComparison.OrdinalIgnoreCase);
        if (!sameDrive && FreeBytes(target) is { } free && free < stored)
        {
            ShowLocationNote($"그 드라이브에 빈 공간이 모자라요. 필요한 공간 {ComponentSteps.Size(stored)}, 남은 공간 {ComponentSteps.Size(free)}.", warning: true);
            return;
        }
        if (!_services.Dialogs.Confirm("설치 위치 바꾸기",
                $"설치된 구성 요소와 받아 둔 파일({ComponentSteps.Size(stored)})을 새 위치로 옮깁니다.\n{target}\n\n옮기는 동안에는 빌드할 수 없어요. 계속할까요?"))
            return;
        IsMoving = true;
        _shell.BeginTask("구성 요소 옮기는 중", target);
        var installer = _installer;
        var progress = new Progress<ComponentProgress>(p => _shell.TaskDetail = p.Fraction is { } f ? $"{p.Step} · {Percent(f)}" : p.Step);
        try
        {
            await Task.Run(() => installer.MoveToAsync(target, progress));
            _shell.EndTask("준비됨");
            UseLocation(target);
            ShowLocationNote("새 위치로 옮겼어요.", warning: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.EndTask("옮기지 못했어요", "", StepState.Bad);
            ShowLocationNote("옮기지 못했어요. 구성 요소는 원래 위치에 그대로 있어요. (" + ex.Message + ")", warning: true);
            _services.RefreshEngine();
            _shell.OnEngineChanged();
            RefreshStates();
        }
        finally
        {
            IsMoving = false;
        }
    }

    /// <summary>From now on the components are in <paramref name="folder"/>: kept in the settings, and the engine is looked for there.</summary>
    private void UseLocation(string folder)
    {
        var isDefault = ComponentsRoot.DefaultLocation() is { } standard
                        && string.Equals(Path.TrimEndingDirectorySeparator(standard), folder, StringComparison.OrdinalIgnoreCase);
        _services.Settings.ComponentsRoot = isDefault ? null : folder;
        try
        {
            _services.Settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowLocationNote("설정을 저장하지 못했어요. 다음에 켜면 원래 위치를 봅니다. (" + e.Message + ")", warning: true);
        }
        _installer = new ComponentInstaller(_catalog!, folder, appVersion: AppServices.AppVersion);
        Location = folder;
        _services.RefreshEngine();
        _shell.OnEngineChanged();
        RefreshStates();
    }

    private void ShowLocationNote(string text, bool warning)
    {
        LocationNote = text;
        LocationNoteIsWarning = warning;
    }

    private static long? FreeBytes(string folder)
    {
        try
        {
            return Path.GetPathRoot(folder) is { Length: > 0 } drive && new DriveInfo(drive) is { IsReady: true } info ? info.AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [RelayCommand]
    private void OpenLicense(SourceRow? source)
    {
        if (source?.LicenseUrl is { } url)
            _services.Dialogs.OpenUrl(url);
    }

    [RelayCommand]
    private void Retry()
    {
        Phase = ComponentsPhase.Setup;
        if (InstallCommand.CanExecute(null))
            InstallCommand.Execute(null);
    }

    [RelayCommand]
    private void NewProject() => _shell.Home.NewProjectCommand.Execute(null);

    [RelayCommand]
    private void QuickCheck() => _shell.NavigateTo("check");

    // ---- the install
    /// <summary>
    /// Installs the rows one after another, as the screen shows them; their downloads start together at the beginning
    /// (ComponentBatch). With the components in Program Files it goes through the helper with administrator rights.
    /// </summary>
    private async Task RunAsync(IReadOnlyList<ComponentRowViewModel> rows)
    {
        if (_installer is null || rows.Count == 0)
            return;
        if (_shell.IsBusy)
        {
            Notice = "지금 하는 작업이 끝난 뒤에 설치할 수 있어요.";
            return;
        }
        Notice = "";
        // in the catalog's order: the engine first (the board tools are useless without it, the zoo runs with its Python)
        _running = [.. rows.OrderBy(r => Rows.IndexOf(r))];
        _plans = [.. _running.Select(r => ComponentSteps.For(r.Spec))];
        _cts = new CancellationTokenSource();
        Phase = ComponentsPhase.Installing;
        _watch.Restart();
        ElapsedText = "00:00";
        _clock.Start();
        _shell.BeginTask("구성 요소 설치 중", _running[0].Title);
        ShowComponent(0);
        IReadOnlyList<ComponentInstallResult>? results;
        try
        {
            results = await ComponentRuns.InstallAsync(_installer, [.. _running.Select(r => r.Id)], id => _ui.Invoke(() => Started(id)),
                new Progress<ComponentProgress>(OnProgress), _cts.Token);
        }
        finally
        {
            _clock.Stop();
            _watch.Stop();
            _cts.Dispose();
            _cts = null;
        }

        // whatever got installed is used from now on
        _services.RefreshEngine();
        _shell.OnEngineChanged();
        RefreshStates();
        if (results is null)
        {
            Phase = ComponentsPhase.Setup;
            Notice = "설치하지 않았어요. " + RightsDeclined;
            _shell.EndTask("설치하지 않았어요", "", StepState.Warn);
            return;
        }
        if (results.FirstOrDefault(r => !r.Success && !r.Cancelled) is { } failed)
        {
            ShowFailure(_running.First(r => r.Id == failed.Component), failed);
            Phase = ComponentsPhase.Failed;
            _shell.EndTask("설치 멈춤", "", StepState.Bad); // the page says why; the tool's own message is in the log folder
            return;
        }
        if (results.Any(r => r.Cancelled) || results.Count < _running.Count)
        {
            Phase = ComponentsPhase.Setup;
            Notice = "설치를 취소했어요. 다 받은 파일은 남겨 두어 다시 받지 않습니다.";
            _shell.EndTask("설치를 취소했어요", "", StepState.Warn);
            return;
        }
        foreach (var step in Steps)
            MarkDone(step);
        Overall = 1;
        OverallText = Percent(1);
        ElapsedTotal = Clock(_watch.Elapsed) + " 걸림";
        Installed.Clear();
        foreach (var row in _running)
            Installed.Add(row);
        DoneTitle = "준비됐어요";
        DoneText = Korean.Object(Korean.List([.. _running.Select(r => r.Title)])) + " 확인했어요.";
        Phase = ComponentsPhase.Done;
        _shell.EndTask("준비됨");
        await _shell.Home.CheckEngineAsync();
    }

    /// <summary>The install of a component began: the one before it is done.</summary>
    private void Started(string id)
    {
        var index = _running.FindIndex(r => r.Id == id);
        if (index < 0 || index == _component)
            return;
        foreach (var step in Steps)
            MarkDone(step);
        ShowComponent(index);
    }

    private void ShowComponent(int index)
    {
        _component = index;
        var row = _running[index];
        InstallTitle = $"{Korean.Object(row.Title)} 설치하는 중";
        CurrentTitle = row.Title;
        CurrentCount = _running.Count > 1 ? $"{index + 1} / {_running.Count}" : "";
        Steps.Clear();
        foreach (var plan in _plans[index])
            Steps.Add(new InstallStepViewModel(plan) { Right = plan.Bytes > 0 ? ComponentSteps.Size(plan.Bytes) : "" });
        CurrentLine = -1;
        UpdateOverall();
        _shell.TaskDetail = $"{CurrentTitle} · {OverallText}";
    }

    /// <summary>
    /// A report of the install, or of a download running ahead of it (<see cref="ComponentProgress.Ahead"/>): the install
    /// itself only moves forward, a download line further down can fill up in the meantime.
    /// </summary>
    private void OnProgress(ComponentProgress p)
    {
        if (_component < 0 || _component >= _running.Count || p.Component != _running[_component].Id)
            return; // another component's download, ahead of its turn
        var line = ComponentSteps.IndexOf(_plans[_component], p.Key);
        if (line < 0 || line >= Steps.Count)
            return;
        if (!p.Ahead)
        {
            for (var i = 0; i < line; i++)
                MarkDone(Steps[i]);
            CurrentLine = line;
        }
        var step = Steps[line];
        if (step.State == StepState.Ok)
            return;
        step.State = StepState.Running;
        if (p.Fraction is { } f && p.Total is { } total)
        {
            step.Fraction = f;
            step.Detail = $"{ComponentSteps.Size(p.Done ?? 0)} / {ComponentSteps.Size(total)}" + Remaining(step, p.Done ?? 0, total);
            step.Right = Percent(f);
        }
        else
        {
            step.Detail = p.Step;
        }
        UpdateOverall();
        var current = CurrentLine >= 0 && CurrentLine < Steps.Count ? Steps[CurrentLine].Title : step.Title;
        _shell.TaskDetail = $"{CurrentTitle} · {current} · {OverallText}";
    }

    private void UpdateOverall()
    {
        Overall = ComponentSteps.Overall(_plans, (c, l) => c < _component ? 1 : c > _component || l >= Steps.Count ? 0 : Steps[l].Progress);
        OverallText = Percent(Overall);
    }

    /// <summary>A finished line: no bar and no "…하는 중" left under it; "10 MB · 확인됨" on the right (mockup ②).</summary>
    private static void MarkDone(InstallStepViewModel step)
    {
        if (step.State == StepState.Ok)
            return;
        step.State = StepState.Ok;
        step.Fraction = -1;
        step.Detail = "";
        step.Right = step.SizeText.Length > 0 ? step.SizeText + " · 확인됨" : "확인됨";
    }

    /// <summary>" · 남은 시간 약 30초", from the speed since this line's download was first seen.</summary>
    private string Remaining(InstallStepViewModel step, long done, long total)
    {
        var now = _watch.Elapsed;
        if (step.RateMark is not { } mark || done < mark.Bytes)
        {
            step.RateMark = (done, now);
            return "";
        }
        var seconds = (now - mark.At).TotalSeconds;
        if (seconds < 2 || done <= mark.Bytes)
            return "";
        var left = (total - done) / ((done - mark.Bytes) / seconds);
        return left < 60 ? $" · 남은 시간 약 {Math.Max(5, Math.Round(left / 5) * 5):0}초" : $" · 남은 시간 약 {Math.Ceiling(left / 60):0}분";
    }

    private void ShowFailure(ComponentRowViewModel row, ComponentInstallResult result)
    {
        if (_running.IndexOf(row) is var index and >= 0 && index != _component)
            ShowComponent(index);
        FailTitle = $"{Korean.Object(row.Title)} 설치하지 못했어요";
        var line = result.FailedKey is { } key ? ComponentSteps.IndexOf(_plans[_component], key) : -1;
        for (var i = 0; i < Steps.Count; i++)
        {
            if (i < line)
            {
                MarkDone(Steps[i]);
                continue;
            }
            Steps[i].Fraction = -1;
            Steps[i].State = i == line ? StepState.Bad : StepState.Todo;
            if (i > line)
            {
                Steps[i].Detail = "";
                Steps[i].Right = "하지 않음";
            }
        }
        if (result.Mismatch is { } download)
        {
            FailHeadline = $"받은 {download.Name} 파일이 이 앱이 확인한 파일과 달라요.";
            FailText = "공식 사이트가 같은 주소의 파일을 새 버전으로 바꿨을 수 있어요. 확인하지 않은 파일은 쓰지 않습니다. 새 버전의 TOPST Toolkit이 나오면 그것으로 설치할 수 있어요.";
            if (line >= 0)
            {
                Steps[line].Detail = $"앱이 아는 SHA-256  {Short(download.Sha256)}\n받은 파일의 SHA-256  {Short(result.ActualSha256)}";
                Steps[line].Right = "SHA-256 다름";
            }
        }
        else if (result.Error?.Contains("HttpRequest", StringComparison.Ordinal) == true || result.Error?.Contains("Response status code", StringComparison.Ordinal) == true
                 || result.Error?.Contains("host", StringComparison.OrdinalIgnoreCase) == true)
        {
            FailHeadline = "인터넷에서 파일을 받지 못했어요.";
            FailText = "연결을 확인한 뒤 다시 받아 주세요. 이미 다 받은 파일은 다시 받지 않습니다.";
            if (line >= 0)
            {
                Steps[line].Detail = result.Error ?? "";
                Steps[line].Right = "멈춤";
            }
        }
        else
        {
            FailHeadline = "설치하는 중에 문제가 생겨 멈췄어요.";
            FailText = "설치한 것은 없고 이전 상태 그대로입니다. 기록 폴더에 자세한 내용이 있어요.";
            if (line >= 0)
            {
                Steps[line].Detail = result.Error ?? "";
                Steps[line].Right = "멈춤";
            }
        }
        FailDetail = result.Error ?? "";
    }

    private static string Short(string? sha) => sha is { Length: 64 } ? sha[..16] + "…" + sha[^8..] : sha ?? "?";

    private static string Percent(double fraction) => ((int)Math.Floor(fraction * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static bool IsUnder(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The list in 설정: "ENLIGHT SDK 0.9.9 · torch 1.12.0+cpu · onnx 1.12.0", "Arm GNU-A 9.2-2019.12 (net.so에 쓰는 파일만)".</summary>
    private static string ContentsOf(string id, string? smoke) => id switch
    {
        ComponentCatalog.Engine => EngineSmoke(smoke),
        ComponentCatalog.Zoo => ZooSmoke(smoke),
        _ => "Arm GNU-A 9.2-2019.12 (net.so에 쓰는 파일만)",
    };

    /// <summary>"timm 1.0.30 ultralytics 8.4.216 onnx 1.17.0 onnxslim 0.1.99" as "timm 1.0.30 · Ultralytics 8.4.216 · onnx 1.17.0 · onnxslim 0.1.99".</summary>
    private static string ZooSmoke(string? smoke)
    {
        if (smoke is null)
            return "timm · Ultralytics · onnx · onnxslim";
        var parts = smoke.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pairs = Enumerable.Range(0, parts.Length / 2).Select(i => (parts[2 * i] == "ultralytics" ? "Ultralytics" : parts[2 * i]) + " " + parts[2 * i + 1]);
        return string.Join(" · ", pairs);
    }

    /// <summary>
    /// The done screen: what the smoke test showed. The board tools' smoke line is gcc's own "--version" line plus
    /// "| aarch64 shared object built": "gcc 9.2.1 (arm-9.10) · aarch64 공유 라이브러리 빌드 확인".
    /// </summary>
    private static string CheckedOf(string id, string? smoke)
    {
        if (id == ComponentCatalog.Engine)
            return EngineSmoke(smoke);
        if (id == ComponentCatalog.Zoo)
            return ZooSmoke(smoke) + (smoke is null ? "" : " · 불러 보기 확인");
        if (smoke is null)
            return ContentsOf(id, smoke);
        var version = System.Text.RegularExpressions.Regex.Match(smoke, @"\)\s+(\d+\.\d+\.\d+)\b");
        var release = System.Text.RegularExpressions.Regex.Match(smoke, @"\((arm-[0-9.]+)\)");
        var text = "gcc " + (version.Success ? version.Groups[1].Value : "?") + (release.Success ? $" ({release.Groups[1].Value})" : "");
        return smoke.Contains("shared object built", StringComparison.Ordinal) ? text + " · aarch64 공유 라이브러리 빌드 확인" : text;
    }

    private static string EngineSmoke(string? smoke) =>
        smoke is null ? "ENLIGHT SDK" : "ENLIGHT " + smoke.Replace(" torch ", " · torch ", StringComparison.Ordinal).Replace(" | ", " · ", StringComparison.Ordinal);

    /// <summary>The sources table of a component, from the catalog.</summary>
    private static List<SourceRow> SourcesOf(ComponentSpec spec, string catalogDirectory)
    {
        var rows = new List<SourceRow>();
        if (spec.Id == ComponentCatalog.Engine)
        {
            if (spec.Downloads.FirstOrDefault(d => d.Id == "toolkit") is { } toolkit)
                rows.Add(new SourceRow(toolkit.Name, "TOPST", toolkit.License, toolkit.LicenseUrl, ComponentSteps.Size(toolkit.Bytes)));
            if (spec.Packages is { } packages)
                rows.Add(new SourceRow($"Python 패키지 {ComponentSteps.PackageCount(spec, catalogDirectory)}개 (PyTorch 1.12 · NumPy · OpenCV 등)", "PyPI",
                    "패키지마다 다름 (BSD · MIT · Apache-2.0 등)", "https://pypi.org/", ComponentSteps.Size(packages.Bytes)));
            var small = spec.Downloads.Where(d => d.Id is "python" or "pip").ToList();
            var bytes = small.Sum(d => d.Bytes) + spec.Extras.Sum(e => e.Bytes);
            rows.Add(new SourceRow("Python 3.8.10 · pip 24.0 · onnx 1.12.0", "python.org · PyPI", "PSF-2.0 · MIT · Apache-2.0",
                small.FirstOrDefault()?.LicenseUrl, ComponentSteps.Size(bytes)));
        }
        else if (spec.Id == ComponentCatalog.Zoo)
        {
            foreach (var extra in spec.Extras)
            {
                var lockFile = Path.Combine(catalogDirectory, extra.Lock);
                var count = File.Exists(lockFile) ? File.ReadLines(lockFile).Count(l => l.Contains("--hash=", StringComparison.Ordinal)) : 0;
                rows.Add(new SourceRow($"Python 패키지 {count}개 (timm · onnx 1.17 · onnxslim · OpenCV 등)", "PyPI", extra.License, "https://pypi.org/", ComponentSteps.Size(extra.Bytes)));
            }
            rows.Add(new SourceRow("Ultralytics 8.4.216 (위 패키지 중 하나)", "PyPI", "AGPL-3.0", "https://github.com/ultralytics/ultralytics/blob/main/LICENSE", ""));
        }
        else
        {
            foreach (var d in spec.Downloads)
                rows.Add(new SourceRow(d.Name, new Uri(d.Url).Host.Replace("developer.", "", StringComparison.Ordinal).Split('.')[0] switch { "arm" => "Arm", var h => h },
                    d.License, d.LicenseUrl, ComponentSteps.Size(d.Bytes)));
        }
        return rows;
    }
}
