using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Presets;
using TopstToolkit.Core.Projects;
using TopstToolkit.Engine.Helpers;

namespace TopstToolkit.App.ViewModels;

/// <summary>How the check card draws the model as one strip from input to output.</summary>
public enum CheckStripMode
{
    /// <summary>No strip: nothing is known about where the layers are.</summary>
    None,
    /// <summary>Everything runs.</summary>
    Clean,
    /// <summary>Cut: the NPU part, the cut, the tail the board computes.</summary>
    Cut,
    /// <summary>A tail cannot run and the cut has not been chosen yet.</summary>
    Undecided,
    /// <summary>What cannot run sits in the middle: no cut helps.</summary>
    Blocked,
}

/// <summary>One of the three summary tiles of the check card.</summary>
public sealed record CheckTile(string Label, string Value, string Note, ChipKind Kind);

/// <summary>An operator (or layer) the NPU cannot run, for the list on the model step.</summary>
public sealed record CheckLayerRow(string Type, string Reason, string Tip);

/// <summary>
/// Step 1 "모델": bring a model file in, see what it is, get the settings filled in — and, check first (design §5.7),
/// see at once what the NPU cannot run and what is done about it. Layout-independent.
/// </summary>
public partial class ModelStepViewModel : ObservableObject, IPageViewModel
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ProjectFile _project;
    private readonly ModelImporter _importer;

    public ModelStepViewModel(AppServices services, ShellViewModel shell, ProjectFile project)
    {
        _services = services;
        _shell = shell;
        _project = project;
        _importer = new ModelImporter(services.Projects, services.Engine);
        Load(null);
    }

    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty] private bool _hasModel;
    [ObservableProperty] private string _fileName = "";
    /// <summary>Where the model came from, as a tooltip on its name (the page shows no paths).</summary>
    [ObservableProperty] private string _fileOrigin = "";
    [ObservableProperty] private string _fileSummary = "";
    [ObservableProperty] private string _guessTitle = "";
    [ObservableProperty] private string _guessReason = "";
    /// <summary>Tone of the guidance box: Info, Warn or Bad.</summary>
    [ObservableProperty] private ChipKind _guessKind = ChipKind.Info;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "모델을 살펴보는 중입니다. 몇 초 걸려요.";
    [ObservableProperty] private string _error = "";

    /// <summary>The model in the project is an exported YOLOv8 that was not prepared (brought in by an older version, or the engine was missing).</summary>
    [ObservableProperty] private bool _canPrepare;

    // ---- the check card
    public ObservableCollection<CheckTile> Tiles { get; } = [];
    public ObservableCollection<FactRow> After { get; } = [];
    public ObservableCollection<CheckLayerRow> BadLayers { get; } = [];

    [ObservableProperty] private string _stateChipText = "";
    [ObservableProperty] private ChipKind _stateChipKind = ChipKind.Muted;
    [ObservableProperty] private string _checkChipText = "";
    [ObservableProperty] private ChipKind _checkChipKind = ChipKind.Muted;
    [ObservableProperty] private string _checkCaption = "";
    [ObservableProperty] private CheckStripMode _stripMode;
    [ObservableProperty] private string _stripRunText = "";
    [ObservableProperty] private string _stripTailText = "";
    [ObservableProperty] private string _stripCutText = "";
    [ObservableProperty] private IReadOnlyList<double> _stripTicks = [];
    /// <summary>Shown instead of the strip when there is nothing to draw: the check failed, or was never run.</summary>
    [ObservableProperty] private string _checkMessageTitle = "";
    [ObservableProperty] private string _checkMessageDetail = "";
    [ObservableProperty] private bool _hasTiles;
    [ObservableProperty] private bool _hasAfter;
    [ObservableProperty] private bool _hasBadLayers;
    [ObservableProperty] private string _badLayersTitle = "";
    [ObservableProperty] private string _graphButtonText = "";
    [ObservableProperty] private bool _graphIsPrimary;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenGraphCommand))]
    private bool _canOpenGraph;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecheckCommand))]
    private bool _canRecheck;

    [ObservableProperty] private string _recheckText = "다시 점검";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _canGoNext;

    [ObservableProperty] private string _nextText = "다음: 2 설정";
    [ObservableProperty] private string _nextNote = "";
    /// <summary>The model has to change before anything else: "다른 모델 가져오기" becomes the way forward.</summary>
    [ObservableProperty] private bool _suggestOtherModel;

    // which button of the card is the main one (a style cannot be swapped by a trigger, so each look is its own button)
    [ObservableProperty] private bool _showGraphPrimary;
    [ObservableProperty] private bool _showGraphSecondary;
    [ObservableProperty] private bool _showNextPrimary;
    [ObservableProperty] private bool _showNextSecondary;
    [ObservableProperty] private bool _showRecheck;

    public void OnShown() => Load(null); // the cut may have been changed on the graph meanwhile

    [RelayCommand]
    private async Task PickAsync()
    {
        if (_services.Dialogs.PickModelFile() is { } path)
            await ImportAsync(path);
    }

    /// <summary>Copies the file into the project, inspects and checks it, and fills the settings from the guessed family.</summary>
    public async Task ImportAsync(string path)
    {
        if (IsBusy || _shell.IsBusy)
            return;
        Error = "";
        if (ModelImporter.FormatOf(path) is null)
        {
            Error = "지원하지 않는 모델 형식이에요. .onnx, .tflite, .cfg, .enlight 파일을 골라 주세요.";
            return;
        }
        if (_project.Headline.IsBuilt && !_services.Dialogs.Confirm("모델 바꾸기", "모델을 바꾸면 지금까지의 빌드 결과는 쓸 수 없게 됩니다. 계속할까요?"))
            return;

        await RunAsync("모델 살펴보는 중", System.IO.Path.GetFileName(path), "모델을 가져오지 못했어요", async progress =>
        {
            _services.Projects.Materialize(_project); // a new project gets its folder with its first model
            return await _importer.ImportAsync(_project, path, default, progress);
        });
    }

    /// <summary>Brings the same model in again, which checks and prepares it: from the user's own file when it is still there, else from the copy in the project.</summary>
    [RelayCommand]
    private async Task PrepareAsync()
    {
        if (_project.Model is not { } model)
            return;
        var path = System.IO.File.Exists(model.Source) ? model.Source : _services.Projects.ToAbsolute(_project, model.Original ?? model.Staged);
        await ImportAsync(path);
    }

    /// <summary>Runs the check for the model the project already holds; the staged model and the settings stay.</summary>
    [RelayCommand(CanExecute = nameof(CanRecheck))]
    private async Task RecheckAsync()
    {
        if (_project.Model is null || IsBusy || _shell.IsBusy)
            return;
        await RunAsync("모델 점검 중", FileName, "점검하지 못했어요", async progress =>
        {
            await _importer.CheckAgainAsync(_project, default, progress);
            return null;
        });
    }

    [RelayCommand(CanExecute = nameof(CanOpenGraph))]
    private void OpenGraph() => _shell.ShowCutGraph();

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => _shell.NavigateTo("config");

    private async Task RunAsync(string title, string detail, string failedTitle, Func<ImportProgress, Task<ModelImportResult?>> work)
    {
        IsBusy = true;
        BusyText = "모델을 살펴보는 중입니다. 몇 초 걸려요.";
        _shell.ClearLog();
        _shell.BeginTask(title, detail);
        var progress = new ImportProgress(
            stage => OnUi(() => { BusyText = stage + "입니다…"; _shell.TaskTitle = stage; }),
            e =>
            {
                if (e is TopstToolkit.Engine.Parsing.OutputLineEvent line && line.Text.Trim().Length > 0)
                    _shell.PostLog(line.Text.TrimEnd(), line.IsUpdate);
            });
        try
        {
            var result = await Task.Run(() => work(progress));
            Load(result);
            _shell.EndTask("준비됨");
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
            Load(null);
            _shell.EndTask(failedTitle, "", StepState.Bad);
        }
        finally
        {
            IsBusy = false;
            _shell.OnProjectChanged();
        }
    }

    private void Load(ModelImportResult? import)
    {
        Notes.Clear();
        var model = _project.Model;
        HasModel = model is not null;
        if (model is null)
        {
            LoadCheck(null, null);
            return;
        }

        // the card names the model as the user knows it; the copies inside the project (the cut model, the grid file)
        // are the app's business and are not shown (user feedback 2026-11-23)
        FileName = ShellViewModel.ModelTitle(_project);
        FileOrigin = _project.Zoo is not null ? "모델 모음에서 받음" : "가져온 파일: " + model.Source;

        // the card names the file the user brought in, so it gives that file's size (the staged model may be a cut copy)
        var size = model.SizeBytes;
        if (model.Original is not null && !ProjectStore.IsDraft(_project))
        {
            var broughtIn = new System.IO.FileInfo(_services.Projects.ToAbsolute(_project, model.Original));
            if (broughtIn.Exists)
                size = broughtIn.Length;
        }
        // one line says what the model is; tensor names and shapes are not shown (user feedback 2026-11-23: a beginner's
        // tool shows nothing that only confuses — the check card says whether it runs)
        var summary = new List<string> { (size / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB", FormatName(model.Format) };
        if (model.Facts is { } facts)
        {
            if (ShellViewModel.InputSizeOf(facts) is { } inputSize) summary.Add("입력 " + inputSize);
            if (facts.ParameterCount > 0) summary.Add("파라미터 " + (facts.ParameterCount / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " M");
        }
        if (model.Check is { Ops: > 0 } counted) summary.Add($"연산 {counted.Ops:N0}개");
        FileSummary = string.Join(" · ", summary);

        var preset = PresetCatalog.Find(_project.Preset);
        var guess = import?.Guess ?? PresetGuesser.Guess(model.Format, model.Facts);
        CanPrepare = guess.RawExport == "yolo-v8" && model.Prepared is null && model.Check is null && _services.Engine is not null;
        GuessKind = ChipKind.Info;
        GuessTitle = preset is null || preset.Task == "unknown" ? "종류를 정하지 못했어요"
            : $"{PresetCatalog.DescribeKind(_project.Task, _project.Preset)} 모델로 보여요";
        GuessReason = guess.PresetId == _project.Preset || guess.RawExport is not null ? guess.Reason : $"프리셋 \"{preset?.DisplayName}\"의 값으로 설정을 채웠어요.";
        if (CanPrepare)
            (GuessTitle, GuessKind) = ("준비가 필요한 YOLO 모델이에요", ChipKind.Warn);

        var graph = import?.Graph ?? _importer.ReadSavedGraph(_project);
        LoadCheck(model, graph);

        // what the check already says in full is not repeated as a note
        var checkedOnGraph = model.Check is { Outcome: "clean" or "tail" or "blocked" };
        // and settings are not the question while the model itself cannot go on
        var guessNotes = model.IsBlocked || !(guess.PresetId == _project.Preset || guess.RawExport is not null) ? [] : guess.Notes;
        foreach (var note in guessNotes.Concat(import?.Problems ?? []))
            if (!(checkedOnGraph && PresetGuesser.IsAboutBoxMathsInside(note)))
                Notes.Add(note);
    }

    /// <summary>Fills the check card and the guidance that depends on it.</summary>
    private void LoadCheck(ProjectModel? model, ModelGraphReport? graph)
    {
        Tiles.Clear();
        After.Clear();
        BadLayers.Clear();
        (StripMode, StripRunText, StripTailText, StripCutText) = (CheckStripMode.None, "", "", "");
        StripTicks = [];
        (CheckMessageTitle, CheckMessageDetail, CheckCaption, NextNote) = ("", "", "", "");
        (StateChipText, CheckChipText) = ("", "");
        (CanOpenGraph, GraphIsPrimary, GraphButtonText) = (false, false, "그래프 보기");
        (CanRecheck, RecheckText, SuggestOtherModel) = (false, "다시 점검", false);
        CanGoNext = model is not null;
        NextText = "다음: 2 설정";
        if (model is null)
        {
            (HasTiles, HasAfter, HasBadLayers) = (false, false, false);
            UpdateButtons();
            return;
        }

        var engine = _services.Engine is not null;
        var check = model.Check;
        var cut = model.Cut;
        var hasGraph = model.Format == "onnx" && (graph is not null || engine);
        CanRecheck = engine;

        if (check is null)
        {
            // a project from before the check existed, or brought in without an engine
            (CheckChipText, CheckChipKind) = ("점검 전", ChipKind.Muted);
            CheckMessageTitle = engine ? "이 모델은 아직 점검하지 않았어요" : "엔진이 아직 설치되지 않아 점검하지 못했어요";
            CheckMessageDetail = engine
                ? "점검하면 NPU가 실행하지 못하는 연산이 있는지, 있다면 어디서 잘라야 하는지 알려 드립니다. 몇 초 걸려요."
                : "프로그램을 다시 설치하면 점검할 수 있습니다. 빌드도 엔진이 있어야 합니다.";
            RecheckText = "지금 점검하기";
            FinishLists(model, check, graph);
            return;
        }

        CheckCaption = check.ElapsedSec > 0
            ? $"점검 {check.ElapsedSec.ToString("0.#", CultureInfo.InvariantCulture)}초" + (check.TotalLayers > 0 ? $" · 레이어 {check.TotalLayers:N0}개" : "")
            : "";
        var bad = check.Layers.Count;
        var ops = check.Ops;
        var effect = graph is null ? null : Effect(graph, cut?.Nodes ?? check.Proposal?.Nodes ?? []);
        IReadOnlyList<double> ticks = graph is null || effect is null ? [] : Ticks(graph, effect, cut is not null || check.Outcome == "tail");

        switch (check.Outcome)
        {
            case "clean":
                (StateChipText, StateChipKind) = ("점검 끝남", ChipKind.Ok);
                (CheckChipText, CheckChipKind) = ("모두 실행됩니다", ChipKind.Ok);
                if (cut is null)
                {
                    StripMode = CheckStripMode.Clean;
                    StripRunText = ops > 0 ? $"NPU가 실행 · 연산 {ops:N0}개" : $"NPU가 실행 · 레이어 {check.TotalLayers:N0}개";
                    Tiles.Add(new CheckTile("NPU가 실행하지 못하는 연산", "없음", check.TotalLayers > 0 ? $"레이어 {check.TotalLayers:N0}개 모두 실행됩니다" : "", ChipKind.Ok));
                    Tiles.Add(new CheckTile("자르는 곳", "필요 없음", "모델을 그대로 씁니다", ChipKind.Muted));
                    Tiles.Add(new CheckTile("남는 문제", "없음", "바로 설정으로 가면 됩니다", ChipKind.Ok));
                }
                else
                {
                    FillCut(cut, ticks, "실행 못 하는 연산은 없지만 직접 잘랐어요", 0, effect);
                }
                (CanOpenGraph, GraphButtonText) = (hasGraph, cut is null ? "그래프 보기" : "그래프에서 자를 곳 보기");
                break;

            case "tail" when cut is not null:
                (StateChipText, StateChipKind) = ("점검 끝남", ChipKind.Ok);
                (CheckChipText, CheckChipKind) = ("자르면 모두 실행됩니다", ChipKind.Ok);
                FillCut(cut, ticks, "모두 끝부분에 있어요", bad, effect);
                (CanOpenGraph, GraphIsPrimary, GraphButtonText) = (hasGraph, true, "그래프에서 자를 곳 보기");
                NextText = "이대로 다음: 2 설정";
                NextNote = "자를 곳은 언제든 그래프에서 바꿀 수 있습니다";
                break;

            case "tail":
                (StateChipText, StateChipKind) = ("자를 곳을 정해야 해요", ChipKind.Warn);
                (CheckChipText, CheckChipKind) = ("이대로는 변환되지 않습니다", ChipKind.Bad);
                StripMode = CheckStripMode.Undecided;
                StripRunText = "앞부분 · NPU가 실행할 수 있어요";
                StripTailText = "끝부분";
                StripCutText = "자를 곳 미정";
                StripTicks = ticks;
                Tiles.Add(new CheckTile("NPU가 실행하지 못하는 연산", $"{bad}개", "모두 끝부분에 있어요", ChipKind.Bad));
                Tiles.Add(new CheckTile("자르는 곳", "정해야 해요", "앱이 아는 계열이 아니에요", ChipKind.Warn));
                Tiles.Add(new CheckTile("자른 뒤 남는 문제", "고르면 알려 드려요", "고르는 즉시 다시 판정합니다", ChipKind.Muted));
                (CanOpenGraph, GraphIsPrimary, GraphButtonText) = (hasGraph, true, "그래프에서 자를 곳 고르기");
                CanGoNext = false;
                NextNote = "자를 곳을 정해야 다음으로 갈 수 있습니다";
                (GuessKind, GuessTitle) = (ChipKind.Warn, "어떤 방식의 모델인지 알아보지 못했어요");
                GuessReason = "SSD나 YOLO처럼 아는 계열이면 자를 곳을 앱이 고릅니다. 이 모델은 그래프에서 직접 골라 주세요. 빨간 연산의 바로 앞이 후보입니다.";
                break;

            case "blocked":
                (StateChipText, StateChipKind) = ("NPU에 맞지 않는 구조", ChipKind.Bad);
                (CheckChipText, CheckChipKind) = ("잘라서 해결할 수 없습니다", ChipKind.Bad);
                StripMode = CheckStripMode.Blocked;
                StripRunText = "앞부분";
                StripTailText = check.BlockedOps > 0 ? $"실행 못 하는 연산과 그 뒤 {check.BlockedOps:N0}개" : "실행 못 하는 연산";
                StripTicks = ticks;
                Tiles.Add(new CheckTile("NPU가 실행하지 못하는 연산", $"{bad}개", check.BlockedReason == "front" ? "모델의 맨 앞에 있어요" : "모델 중간에 있어요", ChipKind.Bad));
                Tiles.Add(new CheckTile("자르는 곳", "자를 수 없어요", check.BlockedReason == "front" ? "앞에 남길 부분이 없어요" : "그 뒤에 학습된 층이 있어요", ChipKind.Bad));
                Tiles.Add(new CheckTile("할 수 있는 일", "다른 모델", "YOLOv8 · SSD 같은 모델", ChipKind.Muted));
                (CanOpenGraph, GraphIsPrimary, GraphButtonText) = (hasGraph, true, "그래프에서 이유 보기");
                CanGoNext = false;
                SuggestOtherModel = true;
                NextNote = "이 모델로는 다음 단계로 갈 수 없습니다";
                (GuessKind, GuessTitle) = (ChipKind.Bad, "이 모델은 NPU에 맞지 않아요");
                GuessReason = check.BlockedReason == "front"
                    ? "NPU가 실행하지 못하는 연산이 모델의 맨 앞에 있어서, 잘라도 NPU에 남는 부분이 없습니다."
                    : "잘라낼 수 있는 것은 끝부분의 계산뿐입니다. 이 모델은 실행하지 못하는 연산이 중간에 있고 그 뒤에 모델의 학습된 부분이 이어져서, 자르면 그 부분이 통째로 사라집니다.";
                break;

            case "listed":
                (StateChipText, StateChipKind) = ("점검 끝남", ChipKind.Warn);
                (CheckChipText, CheckChipKind) = ("일부는 실행하지 못합니다", ChipKind.Warn);
                Tiles.Add(new CheckTile("NPU가 실행하지 못하는 레이어", $"{bad}개", check.TotalLayers > 0 ? $"전체 {check.TotalLayers:N0}개 가운데" : "", ChipKind.Bad));
                Tiles.Add(new CheckTile("자르는 곳", "4 빌드에서 정해요", model.Format == "onnx" ? "그래프에 위치를 놓지 못했어요" : "이 형식은 그래프를 그리지 못해요", ChipKind.Warn));
                Tiles.Add(new CheckTile("자른 뒤 남는 문제", "빌드에서 확인", "잘라낼 곳을 고르면 다시 검사합니다", ChipKind.Muted));
                NextNote = "모델 끝의 박스 계산이라면 4 빌드에서 잘라낼 곳을 고를 수 있습니다";
                break;

            default: // failed
                (StateChipText, StateChipKind) = ("점검하지 못했어요", ChipKind.Warn);
                (CheckChipText, CheckChipKind) = ("점검 실패", ChipKind.Warn);
                CheckMessageTitle = check.ErrorTitle ?? "점검하지 못했어요";
                CheckMessageDetail = (check.ErrorDetail ?? "") + (cut is not null
                    ? " 다만 아는 계열의 구조여서 헤드에서 잘라 두었고, 실제로 실행되는지는 빌드에서 다시 확인합니다."
                    : " 빌드에서 같은 문제가 날 수 있습니다. 아래 로그에서 내용을 볼 수 있어요.");
                (CanOpenGraph, GraphButtonText) = (hasGraph && cut is not null, "그래프에서 자를 곳 보기");
                break;
        }
        FinishLists(model, check, graph);
    }

    private void FillCut(ModelCut cut, IReadOnlyList<double> ticks, string badNote, int bad, CutEffect? effect)
    {
        var kept = cut.KeptOps > 0 ? cut.KeptOps : effect?.KeptOps ?? 0;
        var away = cut.AwayOps > 0 ? cut.AwayOps : effect?.AwayOps ?? 0;
        var tail = cut.Decoder is null ? "잘라낸 부분" : "박스 계산";
        StripMode = CheckStripMode.Cut;
        StripRunText = $"NPU가 실행 · 연산 {kept:N0}개";
        StripTailText = $"{tail} · {away:N0}개";
        StripCutText = $"자르는 곳 {cut.Nodes.Count}개";
        StripTicks = ticks;
        var family = cut.Decoder switch { "yolo-v8" => "YOLOv8의 헤드", "ssd" => "SSD의 출력", _ => "새 출력" };
        Tiles.Add(new CheckTile("NPU가 실행하지 못하는 연산", bad > 0 ? $"{bad}개" : "없음", badNote, bad > 0 ? ChipKind.Bad : ChipKind.Ok));
        Tiles.Add(new CheckTile("자르는 곳", cut.By == "user" ? "직접 골랐어요" : "앱이 골랐어요", $"{family} {cut.Nodes.Count}곳", ChipKind.Muted));
        Tiles.Add(new CheckTile("자른 뒤 남는 문제", "없음", $"남은 {kept:N0}개는 모두 실행됩니다", ChipKind.Ok));
    }

    /// <summary>The right column: what computes the cut-off part, and the operators that cannot run.</summary>
    private void FinishLists(ProjectModel model, ModelCheckRecord? check, ModelGraphReport? graph)
    {
        var cut = model.Cut;
        if (cut is not null)
        {
            // what computes the part that was cut off; the files that go with it (grid, anchor) are the app's business
            // and show only when one is missing (user feedback 2026-11-23)
            var convert = _project.Options.Convert;
            After.Add(new FactRow("계산 방식", cut.Decoder switch { "yolo-v8" => "YOLOv8 박스 계산", "ssd" => "SSD 박스 계산", _ => "출력값 그대로" }));
            After.Add(new FactRow("계산하는 곳", cut.Decoder is null ? "보드 (직접 작성)" : "보드 (앱이 코드를 넣음)"));
            if (cut.Decoder == "ssd" && convert.PostProcess is null)
                After.Add(new FactRow("필요한 파일", "2 설정에서 넣어 주세요"));
            if (cut.Decoder is not null && convert.NumClass is { } classes)
                After.Add(new FactRow("클래스", $"{classes}개"));
            if (cut.Decoder is not null)
            {
                GuessReason = cut.Decoder == "yolo-v8"
                    ? $"Ultralytics에서 내보낸 원본이라 박스 계산이 모델 안에 들어 있어요. 그 부분(연산 {cut.AwayOps}개)은 NPU 대신 보드용 코드가 계산합니다."
                    : $"박스 계산이 모델 안에 들어 있어서 그 앞에서 잘랐어요. 잘라낸 부분(연산 {cut.AwayOps}개)은 보드용 코드가 대신 계산합니다.";
            }
            else
            {
                (GuessKind, GuessTitle) = (ChipKind.Info, "출력값을 그대로 내는 모델로 설정했어요");
                GuessReason = "자른 출력이 툴킷이 아는 박스 계산 방식(SSD, YOLO)과 맞지 않아 \"기타\"로 변환합니다. NPU는 자른 곳까지 계산하고, 그 뒤는 보드에서 직접 작성해야 합니다.";
            }
        }
        else if (check is { Outcome: "tail" })
        {
            After.Add(new FactRow("계산 방식", "자를 곳을 고르면 정해짐"));
        }
        HasAfter = After.Count > 0;

        if (check is not null)
        {
            foreach (var layer in check.Layers)
            {
                // the operator of the model file when it is known, the converter's layer otherwise
                var name = graph?.Bad.FirstOrDefault(b => b.Layer == layer.Name)?.Op ?? layer.Type;
                BadLayers.Add(new CheckLayerRow(name, LayerReasons.Explain(layer.Type, layer.Description), (layer.Node ?? layer.Name) + "\n" + layer.Description));
            }
        }
        // the list helps to decide; once a cut takes those operators away (the app's or the user's), the strip and the
        // tiles say enough, and the converter's reasons would only worry (user feedback 2026-11-23)
        var solved = cut is not null && check is { Outcome: "tail" or "clean" };
        HasBadLayers = BadLayers.Count > 0 && !solved;
        BadLayersTitle = $"실행하지 못하는 연산 {BadLayers.Count}개";
        HasTiles = Tiles.Count > 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        ShowGraphPrimary = CanOpenGraph && GraphIsPrimary;
        ShowGraphSecondary = CanOpenGraph && !GraphIsPrimary;
        ShowNextPrimary = HasModel && !GraphIsPrimary && !SuggestOtherModel;
        ShowNextSecondary = HasModel && GraphIsPrimary && !SuggestOtherModel;
        ShowRecheck = CanRecheck && CheckMessageTitle.Length > 0;
    }

    private static CutEffect Effect(ModelGraphReport report, IReadOnlyList<string> nodes)
    {
        var graph = new CutGraph(report);
        return graph.Evaluate(graph.IdsOf(nodes));
    }

    /// <summary>
    /// Where the operators that cannot run sit inside the tail of the strip, 0 = at the cut, 1 = at the output.
    /// <paramref name="tailIsGone"/>: the tail is what a cut removes; otherwise it is everything that cannot reach the NPU.
    /// </summary>
    private static IReadOnlyList<double> Ticks(ModelGraphReport report, CutEffect effect, bool tailIsGone)
    {
        var tail = report.Graph.Nodes
            .Where(n => tailIsGone ? effect.StateOf(n.Id) is CutState.Gone : n.State != "ok")
            .OrderBy(n => n.Id).ToList();
        if (tail.Count == 0)
            tail = report.Graph.Nodes.Where(n => n.State != "ok").OrderBy(n => n.Id).ToList();
        var ticks = new List<double>();
        for (var i = 0; i < tail.Count; i++)
            if (tail[i].State == "bad")
                ticks.Add((i + 0.5) / tail.Count);
        return ticks;
    }

    private static string FormatName(string format) => format switch
    {
        "onnx" => "ONNX", "tflite" => "TensorFlow Lite", "darknet" => "Darknet (.cfg)", "enlight" => "ENLIGHT (.enlight)", var f => f,
    };

    /// <summary>Import stages arrive on a background thread.</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
