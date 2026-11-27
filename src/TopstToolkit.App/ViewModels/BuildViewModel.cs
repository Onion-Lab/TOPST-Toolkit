using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Projects;
using TopstToolkit.Engine.Board;
using TopstToolkit.Engine.Errors;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.App.ViewModels;

/// <summary>One stage of the build as a card or a pipeline node shows it.</summary>
public partial class StageCardViewModel(BuildStage stage, string name, string idleText) : ObservableObject
{
    public BuildStage Stage { get; } = stage;
    public string Name { get; } = name;
    public string IdleText { get; } = idleText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipText), nameof(ChipKind), nameof(IsRunning), nameof(IsTodo))]
    private BuildStageState _buildState = BuildStageState.Pending;

    [ObservableProperty] private StepState _state = StepState.Todo;
    [ObservableProperty] private string _detail = idleText;
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private string _percentText = "";

    /// <summary>When the stage started running, for the ticking clock.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    public bool IsRunning => BuildState == BuildStageState.Running;
    public bool IsTodo => BuildState is BuildStageState.Pending or BuildStageState.Skipped;

    public string ChipText => BuildState switch
    {
        BuildStageState.Running => "진행 중",
        BuildStageState.Done => "완료",
        BuildStageState.Reused => "재사용",
        BuildStageState.Failed => "실패",
        BuildStageState.Skipped => "건너뜀",
        _ => "대기",
    };

    public ChipKind ChipKind => BuildState switch
    {
        BuildStageState.Running => ChipKind.Running,
        BuildStageState.Done => ChipKind.Ok,
        BuildStageState.Failed => ChipKind.Bad,
        _ => ChipKind.Muted,
    };

    public void Reset()
    {
        BuildState = BuildStageState.Pending;
        State = StepState.Todo;
        Detail = IdleText;
        TimeText = "";
        Progress = 0;
        HasProgress = false;
        PercentText = "";
        StartedAt = null;
    }

    public void Apply(BuildUpdate u)
    {
        if (u.State == BuildStageState.Running && BuildState != BuildStageState.Running)
            StartedAt = DateTimeOffset.Now;
        BuildState = u.State;
        State = u.State switch
        {
            BuildStageState.Running => StepState.Running,
            BuildStageState.Done or BuildStageState.Reused => StepState.Ok,
            BuildStageState.Failed => StepState.Bad,
            _ => StepState.Todo,
        };
        if (u.Detail is not null) Detail = u.Detail;
        HasProgress = u.State == BuildStageState.Running && u.Fraction is not null;
        Progress = u.Fraction ?? 0;
        PercentText = HasProgress ? $"{Progress * 100:0}%" : "";
        if (u.Elapsed is { } t) TimeText = Clock(t);
        if (u.State != BuildStageState.Running) StartedAt = null;
    }

    public static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
}

/// <summary>A layer the NPU cannot run, with the reason in plain words.</summary>
public sealed record LayerProblem(int Index, string Name, string Type, string Reason, string Original);

/// <summary>A layer of the converted network the user can cut the network at.</summary>
public partial class CutCandidate(int index, string name, string kind, bool incompatible) : ObservableObject
{
    public int Index { get; } = index;
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public bool IsIncompatible { get; } = incompatible;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>Step 4 "빌드": runs the pipeline and shows each stage, the log and what went wrong. Layout-independent.</summary>
public partial class BuildViewModel : ObservableObject, IPageViewModel
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ProjectFile _project;
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _cancel;
    private DateTimeOffset _startedAt;
    private bool _loadingThresholds;
    private bool _openedLog;

    public BuildViewModel(AppServices services, ShellViewModel shell, ProjectFile project)
    {
        _services = services;
        _shell = shell;
        _project = project;
        Stages =
        [
            new(BuildStage.Compatibility, "호환성 검사", "NPU에서 실행할 수 있는지 확인"),
            new(BuildStage.Convert, "변환 · 캘리브레이션", "사진으로 값의 범위 재기"),
            new(BuildStage.Quantize, "양자화", "FP32 → INT8"),
            new(BuildStage.Compile, "컴파일", "NPU 명령으로 바꾸기"),
            new(BuildStage.BoardLibrary, "보드 라이브러리", "net.so 만들기"),
            new(BuildStage.Simulate, "PC 추론", "결과 확인용 사진으로 실행"),
            new(BuildStage.Perf, "성능 추정", "보드에서의 추론 시간"),
        ];
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _clock.Stop();
        OnShown();
    }

    public ObservableCollection<StageCardViewModel> Stages { get; }
    public ObservableCollection<LayerProblem> IncompatibleLayers { get; } = [];
    public ObservableCollection<CutCandidate> CutCandidates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild), nameof(BuildButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCutCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _statusText = "아직 빌드하지 않았습니다";
    [ObservableProperty] private ChipKind _statusKind = ChipKind.Muted;
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _executedText = "—";
    [ObservableProperty] private string _reusedText = "—";
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private string _errorTitle = "";
    [ObservableProperty] private string _errorDetail = "";
    [ObservableProperty] private string _errorEvidence = "";
    [ObservableProperty] private bool _hasIncompatibleLayers;
    [ObservableProperty] private bool _hasCutCandidates;
    [ObservableProperty] private string _cutHint = "";

    /// <summary>The layers ticked in the cut list, in the order they will be passed to the toolkit.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCutCommand))]
    private string _chosenText = "";
    [ObservableProperty] private bool _isDetection;
    [ObservableProperty] private bool _succeeded;

    // "보드에 고정되는 값"
    [ObservableProperty] private string _thConfText = "";
    [ObservableProperty] private string _thIouText = "";
    [ObservableProperty] private string _thresholdError = "";

    public bool CanBuild => !IsRunning && !_shell.IsBusy && _services.Engine is not null && _project.Model is not null;
    public string BuildButtonText => _project.Headline.IsBuilt ? "다시 빌드" : "빌드 시작";
    public bool EngineMissing => _services.Engine is null;
    public bool BoardToolsMissing => !_services.BoardToolsInstalled;

    /// <summary>The last build's outcome, for the result and performance pages.</summary>
    public BuildOutcome? LastOutcome { get; private set; }

    public void OnShown()
    {
        IsDetection = _project.Task == "obj";
        _loadingThresholds = true;
        ThConfText = _project.Options.Compile.ThConf.ToString("0.00", CultureInfo.InvariantCulture);
        ThIouText = _project.Options.Compile.ThIou.ToString("0.00", CultureInfo.InvariantCulture);
        _loadingThresholds = false;
        ThresholdError = "";
        if (!IsRunning && LastOutcome is null)
            ShowLastBuild();
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(BuildButtonText));
    }

    partial void OnThConfTextChanged(string value) => ApplyThresholds();
    partial void OnThIouTextChanged(string value) => ApplyThresholds();

    private void ApplyThresholds()
    {
        if (_loadingThresholds)
            return;
        if (!TryUnit(ThConfText, out var conf) || !TryUnit(ThIouText, out var iou))
        {
            ThresholdError = "0과 1 사이의 숫자를 넣어 주세요. 예: 0.5";
            return;
        }
        ThresholdError = "";
        if (conf.Equals(_project.Options.Compile.ThConf) && iou.Equals(_project.Options.Compile.ThIou))
            return;
        _project.Options.Compile.ThConf = conf;
        _project.Options.Compile.ThIou = iou;
        _shell.MarkSettingsChanged();
        if (!IsRunning && _project.Headline.IsBuilt)
        {
            StatusText = "다시 빌드 필요";
            StatusKind = ChipKind.Warn;
        }
    }

    private static bool TryUnit(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value is > 0 and < 1;

    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    [RelayCommand]
    public async Task BuildAsync()
    {
        if (!CanBuild) return;
        IsRunning = true;
        Succeeded = false;
        _startedAt = DateTimeOffset.Now;
        _cancel = new CancellationTokenSource();
        foreach (var s in Stages) s.Reset();
        IncompatibleLayers.Clear();
        CutCandidates.Clear();
        CutHint = ChosenText = "";
        HasIncompatibleLayers = HasCutCandidates = false;
        ErrorTitle = ErrorDetail = ErrorEvidence = "";
        ExecutedText = ReusedText = MemoryText = "—";
        StatusText = "빌드 중";
        StatusKind = ChipKind.Running;
        _shell.ClearLog();
        _shell.IsBuilding = true;
        _openedLog = !_shell.IsLogOpen;
        _shell.IsLogOpen = true;
        _shell.BeginTask("빌드 중", "준비하는 중");
        _shell.RefreshStepStates();
        _clock.Start();

        var builder = new ProjectBuilder(_services.Engine!, _services.Projects);
        BuildOutcome outcome;
        try
        {
            var token = _cancel.Token;
            outcome = await Task.Run(() => builder.BuildAsync(_project, u => OnUi(() => Apply(u)), OnToolEvent, token));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = new BuildOutcome { Error = new ToolError(ToolErrorCode.Unexpected, "예상하지 못한 오류가 났어요", ex.Message, SuggestedAction.MakeDiagnostics) };
        }
        LastOutcome = outcome;
        await Finish(outcome);
    }

    private void Apply(BuildUpdate u)
    {
        var card = Stages.First(s => s.Stage == u.Stage);
        card.Apply(u);
        if (u.PeakMemoryBytes is { } peak)
            MemoryText = (peak / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        if (u.State == BuildStageState.Running)
        {
            var index = Stages.IndexOf(card) + 1;
            StatusText = $"{index} / {Stages.Count} 단계 진행 중";
            _shell.TaskTitle = card.Name + " 중";
            _shell.TaskDetail = u.Detail ?? "";
            _shell.TaskProgress = u.Fraction ?? 0;
        }
    }

    /// <summary>Once a second while a build runs: the clocks of the page, the running card and the task bar.</summary>
    private void Tick()
    {
        if (!IsRunning) return;
        var now = DateTimeOffset.Now;
        TimeText = $"시작 {_startedAt.ToLocalTime():HH:mm} · 경과 {Friendly(now - _startedAt)}";
        if (Stages.FirstOrDefault(s => s.IsRunning) is { StartedAt: { } started } running)
        {
            running.TimeText = StageCardViewModel.Clock(now - started);
            _shell.TaskClock = running.TimeText;
        }
    }

    /// <summary>Called on the tool's reader thread: lines go to the log queue, nothing else is touched.</summary>
    private void OnToolEvent(ToolEvent e)
    {
        if (e is OutputLineEvent line && line.Text.Trim().Length > 0)
            _shell.PostLog(line.Text.TrimEnd(), line.IsUpdate);
    }

    private async Task Finish(BuildOutcome outcome)
    {
        _clock.Stop();
        IsRunning = false;
        _cancel?.Dispose();
        _cancel = null;
        var elapsed = DateTimeOffset.Now - _startedAt;
        ExecutedText = outcome.Executed == 0 ? "없음" : $"{outcome.Executed}단계";
        ReusedText = outcome.Reused == 0 ? "없음" : $"{outcome.Reused}단계";
        if (outcome.PeakMemoryBytes > 0)
            MemoryText = (outcome.PeakMemoryBytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        TimeText = $"시작 {_startedAt.ToLocalTime():HH:mm} · 걸린 시간 {Friendly(elapsed)}";
        _shell.IsBuilding = false;
        _shell.TaskClock = "";
        if (outcome.Success)
        {
            Succeeded = true;
            StatusText = "빌드 완료";
            StatusKind = ChipKind.Ok;
            _shell.EndTask("빌드 완료", Friendly(elapsed), StepState.Ok);
            if (_openedLog)
                _shell.IsLogOpen = false; // it opened for this build; a failed build keeps it open, the log is the evidence
        }
        else if (outcome.Cancelled)
        {
            StatusText = "취소함";
            StatusKind = ChipKind.Muted;
            foreach (var s in Stages.Where(s => s.IsRunning || s.BuildState == BuildStageState.Failed)) s.Reset();
            _shell.EndTask("빌드를 취소했습니다", "", StepState.Todo);
        }
        else
        {
            StatusText = "빌드 실패";
            StatusKind = ChipKind.Bad;
            foreach (var s in Stages.Where(s => s.IsRunning)) s.Reset(); // nothing runs any more
            ErrorTitle = outcome.Error!.Title;
            ErrorDetail = outcome.Error.Detail;
            ErrorEvidence = outcome.Error.Evidence ?? "";
            _shell.EndTask("빌드 실패", outcome.Error.Title, StepState.Bad);
            if (outcome.Compatibility is { Layers.Count: > 0 } report)
            {
                // here the page itself says what is wrong and what to do; the log would only take its room
                if (_openedLog)
                    _shell.IsLogOpen = false;
                await ShowIncompatibleAsync(report);
            }
        }
        _shell.OnProjectChanged();
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(BuildButtonText));
    }

    // ---- incompatible layers: show the table and let the user cut the network before them (design §5.4)
    private async Task ShowIncompatibleAsync(CompatibilityReport report)
    {
        foreach (var layer in report.Layers)
            IncompatibleLayers.Add(new LayerProblem(layer.Index, layer.Name, layer.Type, Explain(layer), layer.Description));
        HasIncompatibleLayers = true;

        var paths = BuildPaths.For(_services.Projects, _project);
        if (paths is null || _services.Engine is null || !System.IO.File.Exists(paths.Fp32Network))
            return;
        try
        {
            var folders = _services.Projects.FoldersOf(_project);
            var info = await Task.Run(() => _services.Engine.For(folders).ReadEnlightAsync(paths.Fp32Network));
            if (info.Value is not { Ok: true } network)
                return;
            // only layers with a name can be cut at (the name is what --force-output takes); constants are no layers
            var named = network.Layers.Where(l => l.Name.Length > 0 && l.Name != "None" && l.Kind != "ConstTensor").ToList();
            // the table counts layers from 1, the network file from 0: a layer both of them name tells the difference
            var offset = report.Layers
                .Select(b => (Bad: b, Layer: named.FirstOrDefault(l => l.Name == b.Name)))
                .Where(p => p.Layer is not null).Select(p => p.Bad.Index - p.Layer!.Index).DefaultIfEmpty(1).First();
            var firstBad = report.Layers.Min(l => l.Index) - offset;
            // candidates: the layers before the first one the NPU cannot run, nearest first
            foreach (var layer in named.Where(l => l.Index < firstBad).OrderByDescending(l => l.Index).Take(40))
                CutCandidates.Add(new CutCandidate(layer.Index + offset, layer.Name, PlainKind(layer.Kind), false));

            // SSD keeps its box maths inside the model; the scores and the boxes are each gathered by a Concat just before it
            if (_project.Preset == "ssd-anchor")
            {
                var concats = CutCandidates.Where(c => c.Kind.Equals("Concat", StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                if (concats.Count == 2)
                {
                    foreach (var c in concats) c.IsChecked = true;
                    CutHint = "SSD 모델은 보통 점수와 박스를 모으는 Concat 두 개에서 자릅니다. 아래에 미리 골라 두었어요. 맞으면 그대로 적용하세요.";
                }
            }
            if (CutHint.Length == 0)
                CutHint = "NPU가 실행하지 못하는 부분의 바로 앞 레이어를 고르세요. 고른 레이어까지만 NPU가 실행하고, 그 뒤의 박스 계산은 보드용 후처리 코드가 맡습니다.";
            foreach (var candidate in CutCandidates)
                candidate.PropertyChanged += (_, _) => UpdateChosen();
            UpdateChosen();
            HasCutCandidates = CutCandidates.Count > 0;
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // the table alone is still useful
        }
    }

    /// <summary>"QConcat" → "Concat": the network file marks quantizable layers with a Q; the compatibility table does not.</summary>
    private static string PlainKind(string kind) => kind.Length > 1 && kind[0] == 'Q' && char.IsUpper(kind[1]) ? kind[1..] : kind;

    /// <summary>The toolkit's reason for a layer, in plain Korean where the wording is known.</summary>
    internal static string Explain(IncompatibleLayer layer) => LayerReasons.Explain(layer.Type, layer.Description);

    /// <summary>By name: the order of the toolkit's own example (Concat_0 … Concat_1 …).</summary>
    private List<string> ChosenLayers() => CutCandidates.Where(c => c.IsChecked).Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private void UpdateChosen() => ChosenText = string.Join(", ", ChosenLayers());

    private bool CanApplyCut() => ChosenText.Length > 0 && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanApplyCut))]
    private async Task ApplyCutAsync()
    {
        var chosen = ChosenLayers();
        if (chosen.Count == 0)
            return;
        _project.Options.Convert.ForceOutput = chosen;
        _shell.MarkSettingsChanged();
        await BuildAsync();
    }

    [RelayCommand]
    private void GoToResult() => _shell.NavigateTo("result");

    /// <summary>Shows what project.json remembers of the last build when the page opens.</summary>
    private void ShowLastBuild()
    {
        if (!_project.Headline.IsBuilt)
        {
            if (_project.Steps.Values.FirstOrDefault(s => s.Status == "failed") is { } failed)
            {
                StatusText = "지난 빌드 실패";
                StatusKind = ChipKind.Bad;
                ErrorTitle = failed.ErrorTitle ?? "";
                ErrorDetail = "다시 빌드하면 자세한 내용을 볼 수 있어요.";
            }
            return;
        }
        var stepNames = new Dictionary<BuildStage, string>
        {
            [BuildStage.Compatibility] = "convert", [BuildStage.Convert] = "convert", [BuildStage.Quantize] = "quantize", [BuildStage.Compile] = "compile",
            [BuildStage.BoardLibrary] = "netso", [BuildStage.Simulate] = "simulate", [BuildStage.Perf] = "perf",
        };
        foreach (var card in Stages)
        {
            if (!_project.Steps.TryGetValue(stepNames[card.Stage], out var record) || record.Status != "done")
                continue;
            card.BuildState = BuildStageState.Done;
            card.State = StepState.Ok;
            card.TimeText = card.Stage == BuildStage.Compatibility ? "" : StageCardViewModel.Clock(TimeSpan.FromSeconds(record.ElapsedSec));
            card.Detail = card.Stage switch
            {
                BuildStage.Compatibility when record.Facts.GetValueOrDefault("compatible") == record.Facts.GetValueOrDefault("total") =>
                    $"레이어 {record.Facts.GetValueOrDefault("total")}개 모두 NPU에서 실행됩니다",
                BuildStage.Compatibility => $"레이어 {record.Facts.GetValueOrDefault("compatible")} / {record.Facts.GetValueOrDefault("total")}개 호환",
                BuildStage.Convert => $"사진 {record.Facts.GetValueOrDefault("images")}장으로 값의 범위를 쟀습니다",
                BuildStage.Perf when _project.Headline.InferenceTimeMs is { } ms => ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
                // the app's own code went into net.so, or the part behind the cut could not (design §5.8): say so when
                // the project is opened again
                BuildStage.BoardLibrary when ProjectBuilder.BoardStageSuffix(record.Facts) is { Length: > 0 } suffix => "net.so" + suffix,
                _ => card.IdleText,
            };
        }
        Succeeded = !_project.Headline.NeedsRebuild;
        StatusText = _project.Headline.NeedsRebuild ? "다시 빌드 필요" : "빌드 완료";
        StatusKind = _project.Headline.NeedsRebuild ? ChipKind.Warn : ChipKind.Ok;
        if (_project.Headline.BuiltAt is { } at)
            TimeText = "마지막 빌드 " + ProjectRow.Friendly(at, DateTimeOffset.Now);
    }

    private static string Friendly(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초" : $"{Math.Max(1, (int)t.TotalSeconds)}초";

    /// <summary>Build updates arrive on background threads; screens are touched on the UI thread only.</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
