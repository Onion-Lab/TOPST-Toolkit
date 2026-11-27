using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Components;
using TopstToolkit.Core.Projects;
using TopstToolkit.Core.Zoo;

namespace TopstToolkit.App.ViewModels;

/// <summary>One model of the list: name, source, input, parameters and the board time the review estimated.</summary>
public partial class ZooRowViewModel : ObservableObject
{
    public ZooRowViewModel(ZooModel model, double longestMs)
    {
        Model = model;
        var ms = model.Review.Ms ?? 0;
        BarWidth = ms > 0 ? 6 + 46 * Math.Log(1 + ms) / Math.Log(1 + Math.Max(longestMs, 1)) : 0;
    }

    public ZooModel Model { get; }
    public string Id => Model.Id;
    public string Display => Model.Display;
    public string Library => Model.Library;
    public string LibraryName => Model.LibraryName;
    /// <summary>"ImageNet 1000개", and "입력을 224에서 줄임" when the compiler needed a smaller picture.</summary>
    public string SubText => Model.DatasetText + (Model.Export.Smaller ? $" · 입력을 {Model.Export.NominalSize}에서 줄임" : "");
    public string SizeText => Model.Export.Size.ToString(CultureInfo.InvariantCulture);
    public string ParamsText => ZooViewModel.Params(Model.Review.Params);
    public string MsText => ZooViewModel.Ms(Model.Review.Ms);
    public double BarWidth { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>One line of "누르면" on the chosen model's card.</summary>
public sealed record ZooFlowStep(int Number, string Text);

/// <summary>A source chip above the list: "전체 159", "timm 107" ...</summary>
public partial class ZooFilter(string key, string label, int count) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public int Count { get; } = count;
    [ObservableProperty] private bool _isChecked;
}

public enum ZooPhase
{
    /// <summary>The list and the chosen model.</summary>
    List,
    /// <summary>Component, weights, export, check, project: one line each.</summary>
    Preparing,
    /// <summary>A step stopped; what happened and what to do.</summary>
    Failed,
}

/// <summary>
/// "모델 모음" (design §5.9, docs/mockups/mockup-model-zoo.html ②–④): the models the review checked and built, the
/// chosen one's card, and the preparation that ends in a project ready to build (or building). Layout-independent.
/// </summary>
public partial class ZooViewModel : ObservableObject, IPageViewModel
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ZooCatalog? _catalog;
    private readonly ComponentInstaller? _installer;
    private readonly List<ZooRowViewModel> _rows = [];
    private readonly DispatcherTimer _clock;
    private readonly Stopwatch _watch = new();
    private CancellationTokenSource? _cts;
    private IReadOnlyList<InstallStepPlan> _plan = [];
    private ProjectFile? _preparing;

    public ZooViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ElapsedText = Clock(_watch.Elapsed), Dispatcher.CurrentDispatcher);
        _catalog = ZooCatalog.Locate();
        if (ComponentCatalog.Find() is { } components && services.ComponentsLocation is { } root)
            _installer = new ComponentInstaller(components, root, appVersion: AppServices.AppVersion);
        if (_catalog is null)
            Problem = "모델 모음 목록(engine\\zoo\\zoo.json)을 찾지 못했어요. TOPST Toolkit을 다시 설치해 주세요.";
        else
        {
            var longest = _catalog.Models.Max(m => m.Review.Ms ?? 0);
            _rows.AddRange(_catalog.Models.Select(m => new ZooRowViewModel(m, longest)));
            Reviewed = _catalog.Reviewed;
        }
        ClassCount = _rows.Count(r => r.Model.Task == "class");
        ObjectCount = _rows.Count(r => r.Model.Task == "obj");
        ApplyFilter();
    }

    public string Problem { get; } = "";
    public bool HasProblem => Problem.Length > 0;
    /// <summary>The day of the review the numbers come from.</summary>
    public string Reviewed { get; } = "";
    public string Note => $"모두 이 앱으로 내보내 NPU 점검과 빌드를 통과한 모델이에요 ({Reviewed} 검토)";

    // ------------------------------------------------------------------------------------------------ the list

    public int ClassCount { get; }
    public int ObjectCount { get; }

    /// <summary><c>class</c> or <c>obj</c>: the tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClassTab), nameof(IsObjectTab), nameof(Foot))]
    private string _tab = "obj";

    public bool IsClassTab { get => Tab == "class"; set { if (value) Tab = "class"; } }
    public bool IsObjectTab { get => Tab == "obj"; set { if (value) Tab = "obj"; } }

    [ObservableProperty] private string _search = "";

    /// <summary>The source chip that is on: "" for all.</summary>
    [ObservableProperty] private string _library = "";

    public ObservableCollection<ZooRowViewModel> Visible { get; } = [];
    public ObservableCollection<ZooFilter> Filters { get; } = [];
    public bool HasNothing => Visible.Count == 0;

    /// <summary>The line under the list: what is not in it, and why.</summary>
    public string Foot => Tab == "obj"
        ? "모두 이 앱으로 내보내 점검 · 빌드한 모델이에요. YOLO26 · YOLO12 · YOLO11 · YOLOv10처럼 어텐션이 들어 있어 NPU가 실행하지 못하는 모델과 공식 가중치가 없는 YOLOv6은 넣지 않았어요."
        : "모두 이 앱으로 내보내 점검 · 빌드한 모델이에요. EfficientNet처럼 큰 입력에서 컴파일되지 않는 모델은 되는 크기로 넣었고, 어텐션이 들어 있는 모델(ConvNeXt · ViT 등)은 NPU가 실행하지 못해 넣지 않았어요.";

    partial void OnTabChanged(string value)
    {
        Library = "";
        ApplyFilter();
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnLibraryChanged(string value) => ApplyFilter(rebuildFilters: false);

    [RelayCommand]
    private void ChooseLibrary(ZooFilter? filter)
    {
        if (filter is not null)
            Library = filter.Key;
        foreach (var f in Filters)
            f.IsChecked = f.Key == Library;
    }

    private void ApplyFilter(bool rebuildFilters = true)
    {
        var inTask = _rows.Where(r => r.Model.Task == Tab).ToList();
        if (rebuildFilters)
        {
            Filters.Clear();
            Filters.Add(new ZooFilter("", "전체", inTask.Count) { IsChecked = Library == "" });
            foreach (var lib in new[] { "ultralytics", "torchvision", "timm" })
                if (inTask.Count(r => r.Library == lib) is var n and > 0)
                    Filters.Add(new ZooFilter(lib, inTask.First(r => r.Library == lib).LibraryName, n) { IsChecked = Library == lib });
        }
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Visible.Clear();
        foreach (var row in inTask)
        {
            if (Library.Length > 0 && row.Library != Library)
                continue;
            var text = row.Display + " " + row.Model.Name + " " + row.Model.Family + " " + row.LibraryName;
            if (words.Any(w => !text.Contains(w, StringComparison.OrdinalIgnoreCase)))
                continue;
            Visible.Add(row);
        }
        OnPropertyChanged(nameof(HasNothing));
        if (Selected is null || !Visible.Contains(Selected))
            Selected = Visible.FirstOrDefault(r => r.Id is "ul-yolov8n" or "tv-mobilenet_v2") ?? Visible.FirstOrDefault();
    }

    // ------------------------------------------------------------------------------------------------ the chosen model

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(Title), nameof(TaskText), nameof(About), nameof(MsText), nameof(InputText),
        nameof(MemoryText), nameof(HasMemoryText), nameof(WeightsText), nameof(FlowSteps), nameof(IsUltralytics), nameof(NeedsConsent),
        nameof(NeedTitle), nameof(NeedNote), nameof(ConsentLead), nameof(FixText), nameof(HasFixText))]
    [NotifyCanExecuteChangedFor(nameof(CreateAndBuildCommand), nameof(CreateOnlyCommand))]
    private ZooRowViewModel? _selected;

    partial void OnSelectedChanged(ZooRowViewModel? oldValue, ZooRowViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
        Agreed = false;
    }

    [RelayCommand]
    private void Select(ZooRowViewModel? row)
    {
        if (row is not null)
            Selected = row;
    }

    private ZooModel? Model => Selected?.Model;
    public bool HasSelection => Model is not null;
    public string Title => Model?.Display ?? "";
    public string TaskText => Model?.TaskText ?? "";
    public string About => Model is null ? "" : (Model.About.Length > 0 ? Model.About + " " : "") +
        (Model.Task == "obj" ? "사람 · 차 · 개처럼 COCO의 80가지를 찾습니다." : "ImageNet의 1000가지를 구분합니다.");
    public string MsText => Model is null ? "" : $"약 {Ms(Model.Review.Ms)} (성능 추정)";
    public string InputText => Model is null ? "" : $"{Model.Export.Size} × {Model.Export.Size}" +
        (Model.Export.Size != Model.Export.NominalSize ? $" (원래 {Model.Export.NominalSize})" : "");
    public string WeightsText => Model?.Weights.Caption ?? "";

    /// <summary>
    /// What the build took on the PC in the review, said only when it is a lot (the big models: the compiler alone took
    /// 8-12 GB for YOLOv8x), so that someone with a 16 GB PC knows before pressing.
    /// </summary>
    public string MemoryText => Model?.Review.PeakMemoryBytes is { } peak && peak >= MemoryNoteBytes
        ? $"빌드할 때 PC 메모리를 약 {Math.Ceiling(peak / 1e9):0} GB 씁니다. 다른 프로그램을 닫고 누르세요."
        : "";
    public bool HasMemoryText => MemoryText.Length > 0;
    private const long MemoryNoteBytes = 6_000_000_000;

    /// <summary>What the export changes so that the NPU runs it, in a few words (for the log; the card does not show it).</summary>
    public string FixText => Model is null ? "" : string.Join(" · ", Model.Export.Fixes.Select(f => f switch
    {
        "hardSigmoidToRelu6" => "hard-sigmoid를 ReLU6 꼴로",
        "meanToPool" => "평균을 전역 풀링으로",
        "shuffleToConv" => "채널 섞기를 1×1 합성곱으로",
        "reparameterized" => "배포 형태로 합쳐서",
        "odd" => "입력을 32a+1로 (양쪽 같은 패딩)",
        "smaller" => "입력을 줄여서",
        var other => other,
    }));
    public bool HasFixText => FixText.Length > 0;

    /// <summary>"누르면": what happens, in order.</summary>
    public IReadOnlyList<ZooFlowStep> FlowSteps => Model is null ? [] :
    [
        new(1, "가중치를 받아"),
        new(2, $"ONNX로 내보내고 ({Model.Export.Size} × {Model.Export.Size})"),
        new(3, Model.Task == "obj" ? "NPU 점검 뒤 끝부분은 잘라 보드 코드로" : "NPU에서 모두 도는지 점검한 뒤"),
        new(4, "프로젝트를 만들어 빌드합니다"),
    ];

    public bool IsUltralytics => Model?.Library == "ultralytics";
    /// <summary>The export packages are not installed yet: they come first, once.</summary>
    public bool NeedsComponent => _installer is null || (_installer.StateOf(ComponentCatalog.Zoo) != ComponentState.Installed && !HasSiteOverride);
    private static bool HasSiteOverride => Environment.GetEnvironmentVariable(ZooPreparer.SiteVariable) is { Length: > 0 } site && Directory.Exists(site);

    /// <summary>Development and screen captures only: export with random weights instead of downloading (the review's structure-only export).</summary>
    public const string RandomWeightsVariable = "TOPST_ZOO_RANDOM_WEIGHTS";
    private static bool RandomWeights => Environment.GetEnvironmentVariable(RandomWeightsVariable) == "1";
    public bool NeedsConsent => HasSelection && (NeedsComponent || (IsUltralytics && !_services.Settings.UltralyticsAgreed));
    public string NeedTitle => NeedsComponent ? "모델 모음 구성 요소가 필요해요" : "Ultralytics 라이선스에 동의해 주세요";
    public string NeedNote => NeedsComponent ? "처음 한 번 · 받을 양 " + ComponentSteps.Size(ComponentBytes) : "처음 한 번";
    public string ConsentLead => IsUltralytics ? "Ultralytics " : "";
    private long ComponentBytes => _installer?.Catalog.Components.FirstOrDefault(c => c.Id == ComponentCatalog.Zoo)?.DownloadBytes ?? 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateAndBuildCommand), nameof(CreateOnlyCommand))]
    private bool _agreed;

    /// <summary>Why a button does nothing right now (a build runs, ...); empty otherwise.</summary>
    [ObservableProperty] private string _notice = "";

    private bool CanCreate() => HasSelection && _catalog is not null && _services.Engine is not null && Phase != ZooPhase.Preparing
                                && (!NeedsConsent || Agreed);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAndBuildAsync() => CreateAsync(build: true);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateOnlyAsync() => CreateAsync(build: false);

    [RelayCommand]
    private void OpenLicense()
    {
        if (Model?.License.Url is { Length: > 0 } url)
            _services.Dialogs.OpenUrl(url);
    }

    public void OnShown()
    {
        if (Phase != ZooPhase.Preparing)
        {
            OnPropertyChanged(nameof(NeedsConsent));
            OnPropertyChanged(nameof(NeedTitle));
            OnPropertyChanged(nameof(NeedNote));
            CreateAndBuildCommand.NotifyCanExecuteChanged();
            CreateOnlyCommand.NotifyCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------------------------------------------ preparing

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList), nameof(IsPreparing), nameof(IsFailed))]
    [NotifyCanExecuteChangedFor(nameof(CreateAndBuildCommand), nameof(CreateOnlyCommand))]
    private ZooPhase _phase = ZooPhase.List;

    public bool IsList => Phase == ZooPhase.List;
    public bool IsPreparing => Phase == ZooPhase.Preparing;
    public bool IsFailed => Phase == ZooPhase.Failed;

    public ObservableCollection<InstallStepViewModel> Steps { get; } = [];
    [ObservableProperty] private string _prepareTitle = "";
    [ObservableProperty] private string _prepareLibrary = "";
    [ObservableProperty] private double _overall;
    [ObservableProperty] private string _overallText = "0%";
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private string _failTitle = "";
    [ObservableProperty] private string _failDetail = "";
    [ObservableProperty] private string _failEvidence = "";
    private string? _logs;

    private async Task CreateAsync(bool build)
    {
        if (Model is not { } model || _catalog is null || _services.Engine is not { } session)
            return;
        if (_shell.IsBusy)
        {
            Notice = "지금 하는 작업이 끝난 뒤에 만들 수 있어요.";
            return;
        }
        if (_installer is null)
        {
            Notice = "구성 요소를 설치할 위치를 찾지 못했어요. 설정 › 구성 요소를 확인해 주세요.";
            return;
        }
        Notice = "";
        if (IsUltralytics && !_services.Settings.UltralyticsAgreed)
        {
            _services.Settings.UltralyticsAgreed = true;
            _services.Settings.Save();
        }

        var needsComponent = NeedsComponent;
        var plans = new List<InstallStepPlan>();
        if (needsComponent)
            plans.Add(new("모델 모음 구성 요소 설치", "처음 한 번만 · 엔진과 따로 둡니다", ["component"], 30, ComponentBytes));
        plans.Add(new($"가중치 받기 · {model.Weights.File}", $"{model.Weights.Host} · 받은 뒤 해시를 확인해요", ["weights"], 30, model.Weights.Bytes));
        plans.Add(new("ONNX로 내보내기", $"{model.Export.Size} × {model.Export.Size}", ["export"], 15));
        plans.Add(new("NPU 점검", model.Task == "obj" ? "박스 계산 부분은 잘라서 보드 코드로" : "모두 NPU에서 도는지", ["import"], 15));
        plans.Add(new("프로젝트 만들기", "설정은 " + (model.Task == "obj" ? "YOLOv8 · COCO 80개" : "ImageNet 1000개") + "로 채우고 전처리는 가중치에 맞춰요", ["settings"], 5));
        if (build)
            plans.Add(new("빌드", "빌드 화면에서 이어서 해요", ["build"], 5));
        _plan = plans;
        Steps.Clear();
        foreach (var plan in plans)
            Steps.Add(new InstallStepViewModel(plan) { State = StepState.Todo, Right = plan.Key() == "build" ? "약 1분" : "" });
        PrepareTitle = $"{model.Display} 준비하는 중";
        PrepareLibrary = model.LibraryName;
        Overall = 0;
        OverallText = "0%";
        _logs = null;
        Phase = ZooPhase.Preparing;
        _watch.Restart();
        ElapsedText = "00:00";
        _clock.Start();
        _shell.ClearLog();
        _shell.BeginTask("모델 모음 준비 중", model.Display);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _preparing = null;
        try
        {
            if (needsComponent)
            {
                Enter("component");
                // in Program Files (installed for all users) this goes through the helper with administrator rights
                var results = await ComponentRuns.InstallAsync(_installer, [ComponentCatalog.Zoo], _ => { },
                    new Progress<ComponentProgress>(p => Report("component", p.Step, p.Done, p.Total)), ct);
                _shell.Components.RefreshStates();
                if (results is null)
                    throw new ZooPrepareException("component", "모델 모음 구성 요소를 설치하지 않았어요",
                        "TOPST Toolkit이 프로그램 폴더에 설치되어 있어 관리자 권한이 있어야 해요. 다시 하면 Windows가 다시 물어봅니다.");
                var installed = results.FirstOrDefault() ?? new ComponentInstallResult { Component = ComponentCatalog.Zoo, Cancelled = true, Error = "cancelled" };
                if (installed.Cancelled)
                    throw new OperationCanceledException(ct);
                if (!installed.Success)
                {
                    _logs = installed.LogDirectory;
                    throw new ZooPrepareException("component", "모델 모음 구성 요소를 설치하지 못했어요",
                        "인터넷 연결을 확인하고 다시 해 주세요. 같으면 기록을 열어 내용을 알려 주세요.", installed.Error);
                }
                Done("component", ComponentSteps.Size(ComponentBytes));
            }

            var preparer = new ZooPreparer(_catalog, _installer, session, _services.Projects);
            var project = _services.Projects.NewDraft(model.Display, AppServices.AppVersion, AppServices.SdkVersion);
            _preparing = project;
            var progress = new Progress<ZooProgress>(p => Report(p.Key, p.Step, p.Done, p.Total));
            var importProgress = new ImportProgress(stage => OnUi(() => Detail("import", stage)), e =>
            {
                if (e is TopstToolkit.Engine.Parsing.OutputLineEvent line && line.Text.Trim().Length > 0)
                    _shell.PostLog(line.Text.TrimEnd(), line.IsUpdate);
            });
            Enter("weights");
            var prepared = await System.Threading.Tasks.Task.Run(() => preparer.PrepareAsync(model, project, progress, importProgress, ct, RandomWeights), ct);
            Done("settings", "");
            var check = project.Model?.Check?.Outcome;
            StepAt("import")?.Let(s => s.Right = check == "clean" ? "모두 NPU" : project.Model?.Cut is not null ? "끝부분 잘라 냄" : check ?? "");

            _clock.Stop();
            _shell.EndTask("준비됨", model.Display);
            Phase = ZooPhase.List;
            _preparing = null;
            _shell.OpenProject(project);
            if (build && project.Model?.IsReady == true && _shell.BuildCommand.CanExecute(null))
                _shell.BuildCommand.Execute(null);
        }
        catch (OperationCanceledException)
        {
            _clock.Stop();
            Forget();
            _shell.EndTask("취소했어요", model.Display, StepState.Todo);
            Phase = ZooPhase.List;
        }
        catch (ZooPrepareException e)
        {
            Fail(e.Title, e.Detail, e.Evidence ?? "");
            _logs ??= _preparing is { } p && !ProjectStore.IsDraft(p) ? _services.Projects.FoldersOf(p).LogsDir : null;
            Forget();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            Fail("준비하지 못했어요", "기록을 열어 내용을 확인하거나 진단 패키지를 만들어 알려 주세요.", e.Message);
            Forget();
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>A project that did not get ready is not kept (it would only stand in the list as a broken one).</summary>
    private void Forget()
    {
        if (_preparing is { } project && !ProjectStore.IsDraft(project))
        {
            try
            {
                _services.Projects.Delete(project.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _preparing = null;
    }

    private void Fail(string title, string detail, string evidence)
    {
        _clock.Stop();
        var failed = Steps.FirstOrDefault(s => s.State == StepState.Running);
        if (failed is not null)
            failed.State = StepState.Bad;
        FailTitle = title;
        FailDetail = detail;
        FailEvidence = evidence;
        _shell.EndTask(title, "", StepState.Bad);
        Phase = ZooPhase.Failed;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Back()
    {
        Phase = ZooPhase.List;
        OnShown();
    }

    [RelayCommand]
    private void OpenLogs()
    {
        if (_logs is { } folder && Directory.Exists(folder))
            _services.Dialogs.ShowInExplorer(folder);
        else if (_installer is not null && Directory.Exists(_installer.LogsDirectory))
            _services.Dialogs.ShowInExplorer(_installer.LogsDirectory);
    }

    private InstallStepViewModel? StepAt(string key)
    {
        var i = ComponentSteps.IndexOf(_plan, key);
        return i >= 0 && i < Steps.Count ? Steps[i] : null;
    }

    /// <summary>A line starts: the ones before it are done.</summary>
    private void Enter(string key)
    {
        var index = ComponentSteps.IndexOf(_plan, key);
        for (var i = 0; i < Steps.Count; i++)
        {
            if (i < index && Steps[i].State != StepState.Ok)
            {
                Steps[i].State = StepState.Ok;
                Steps[i].Fraction = -1;
            }
            else if (i == index && Steps[i].State != StepState.Running)
                Steps[i].State = StepState.Running;
        }
        UpdateOverall(index, 0);
    }

    private void Done(string key, string right)
    {
        if (StepAt(key) is { } step)
        {
            step.State = StepState.Ok;
            step.Fraction = -1;
            if (right.Length > 0)
                step.Right = right;
        }
    }

    private void Detail(string key, string text)
    {
        if (StepAt(key) is { } step)
            step.Detail = text;
    }

    private void Report(string key, string text, long? done, long? total)
    {
        OnUi(() =>
        {
            Enter(key);
            var step = StepAt(key);
            if (step is null)
                return;
            var fraction = total is > 0 && done is { } d ? Math.Clamp((double)d / total.Value, 0, 1) : (double?)null;
            if (fraction is { } f)
            {
                step.Fraction = f;
                step.Right = $"{f:P0}";
                step.Detail = $"{ComponentSteps.Size(done ?? 0)} / {ComponentSteps.Size(total ?? 0)}";
            }
            else if (key is not "weights")
                step.Detail = text;
            UpdateOverall(ComponentSteps.IndexOf(_plan, key), fraction ?? 0);
            _shell.TaskDetail = text;
        });
    }

    private void UpdateOverall(int line, double fraction)
    {
        Overall = ComponentSteps.Overall([_plan], 0, line, fraction);
        OverallText = Overall.ToString("P0", CultureInfo.InvariantCulture);
        _shell.TaskProgress = Overall;
    }

    // ------------------------------------------------------------------------------------------------ words

    public static string Params(long p) => p >= 1_000_000
        ? (p / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " M"
        : (p / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " M";

    public static string Ms(double? ms) => ms is not { } v || v <= 0 ? "—"
        : (v < 10 ? v.ToString("0.00", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture)) + " ms";

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}

internal static class ZooStepExtensions
{
    public static string Key(this InstallStepPlan plan) => plan.Keys.FirstOrDefault() ?? "";

    public static void Let<T>(this T value, Action<T> action) => action(value);
}
