using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Controls;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Projects;
using TopstToolkit.Engine.Helpers;

namespace TopstToolkit.App.ViewModels;

/// <summary>An operator the NPU cannot run, in the list next to the graph.</summary>
public sealed record GraphBadRow(int Id, string Op, string Brief, string StateText, bool IsLeft, bool IsPicked, string Tip);

/// <summary>A chosen cut, in the list next to the graph.</summary>
public sealed record GraphCutRow(int Id, string Op, string Detail, string ShortName, string Tip);

/// <summary>
/// "자를 곳" (design §5.7): the end of the model as a graph. Operators the NPU cannot run are marked, a click on a
/// runnable operator cuts the model there, and the side panel says at once what the cut means: what stays on the NPU,
/// what goes, and what computes the part that goes. A model no cut can help is only explained here.
/// Layout-independent: the drawing itself is <see cref="GraphLayout"/>, the rules are <see cref="CutGraph"/>.
/// </summary>
public partial class GraphCutViewModel : ObservableObject, IPageViewModel
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ProjectFile _project;
    private readonly ModelImporter _importer;
    private readonly HashSet<int> _cut = [];
    private CutGraph? _graph;
    private string _loadedFor = "";
    /// <summary>Why the last attempt to make the cut failed; shown until the cut is changed.</summary>
    private string _applyError = "";

    public GraphCutViewModel(AppServices services, ShellViewModel shell, ProjectFile project)
    {
        _services = services;
        _shell = shell;
        _project = project;
        _importer = new ModelImporter(services.Projects, services.Engine);
    }

    public ObservableCollection<GraphBadRow> Bads { get; } = [];
    public ObservableCollection<GraphCutRow> Cuts { get; } = [];
    public ObservableCollection<string> WhyNot { get; } = [];

    /// <summary>The view scrolls the drawing so that this operator is in sight.</summary>
    public event Action<int>? ScrollRequested;

    /// <summary>Finished when the graph is on screen (or could not be made); the capture tool waits for it.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _hasGraph;
    [ObservableProperty] private GraphLayout? _layout;
    [ObservableProperty] private CutEffect? _effect;
    [ObservableProperty] private int? _picked;
    /// <summary>No cut helps this model: the graph only explains.</summary>
    [ObservableProperty] private bool _locked;

    [ObservableProperty] private string _chipText = "";
    [ObservableProperty] private ChipKind _chipKind = ChipKind.Muted;
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private string _bannerTitle = "";
    [ObservableProperty] private string _bannerText = "";

    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _keptLabel = "";
    [ObservableProperty] private string _keptText = "";
    [ObservableProperty] private ChipKind _keptKind = ChipKind.Muted;
    [ObservableProperty] private string _awayLabel = "잘라냄";
    [ObservableProperty] private string _awayText = "";
    [ObservableProperty] private ChipKind _awayKind = ChipKind.Muted;

    [ObservableProperty] private bool _hasBads;
    [ObservableProperty] private string _badCountText = "";
    [ObservableProperty] private string _badChipText = "";
    [ObservableProperty] private ChipKind _badChipKind = ChipKind.Muted;

    [ObservableProperty] private string _cutCountText = "";
    [ObservableProperty] private bool _hasCuts;
    [ObservableProperty] private string _suggestText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SuggestCommand))]
    private bool _canSuggest;

    [ObservableProperty] private bool _hasVerdict;
    [ObservableProperty] private string _verdictTitle = "";
    [ObservableProperty] private string _verdictBody = "";
    [ObservableProperty] private string _verdictNote = "";
    [ObservableProperty] private ChipKind _verdictKind = ChipKind.Info;

    [ObservableProperty] private string _zoneTitle = "";
    [ObservableProperty] private string _zoneBody = "";
    [ObservableProperty] private ChipKind _zoneKind = ChipKind.Muted;
    [ObservableProperty] private string _popTitle = "";
    [ObservableProperty] private string _popName = "";
    [ObservableProperty] private string _popBody = "";
    [ObservableProperty] private ChipKind _popKind = ChipKind.Bad;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApply;

    [ObservableProperty] private bool _isBusy;

    public void OnShown()
    {
        var model = _project.Model;
        var signature = model is null ? "" : $"{model.Check?.CheckedAt.Ticks}|{model.Original}|{string.Join(",", model.Cut?.Nodes ?? [])}";
        if (signature == _loadedFor && (_graph is not null || Message.Length > 0))
            return;
        _loadedFor = signature;
        Ready = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        Message = "";
        HasGraph = false;
        try
        {
            var report = _importer.ReadSavedGraph(_project) ?? await Task.Run(() => _importer.LoadGraphAsync(_project));
            if (report is null)
            {
                Message = _project.Model?.Format == "onnx"
                    ? "이 모델의 그래프를 읽지 못했어요. 1 모델에서 다시 점검해 주세요."
                    : "그래프는 ONNX 모델에서만 볼 수 있어요.";
                return;
            }
            _graph = new CutGraph(report);
            Layout = GraphLayout.Compute(report);
            Locked = report.Verdict == "blocked";
            _cut.Clear();
            foreach (var id in _graph.IdsOf(_project.Model?.Cut?.Nodes ?? []))
                _cut.Add(id);
            Picked = null;
            HasGraph = true;
            Refresh();
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Message = "그래프를 읽지 못했어요: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---------------------------------------------------------------- what the user does

    /// <summary>A click on an operator of the drawing: cut there (or take the cut back), or show why it cannot run.</summary>
    [RelayCommand]
    private void NodeClicked(int id)
    {
        if (_graph?.Find(id) is not { } node || IsBusy)
        {
            ClearPick();
            return;
        }
        if (CutGraph.CanCutAt(node))
        {
            if (!Locked && !_cut.Remove(id))
                _cut.Add(id);
            Picked = null;
            _applyError = "";
        }
        else
        {
            Picked = Picked == id ? null : id;
        }
        Refresh();
    }

    [RelayCommand]
    private void ClearPick()
    {
        if (Picked is null)
            return;
        Picked = null;
        Refresh();
    }

    /// <summary>The cut the app suggests: the head of a known detector, or the last supported points.</summary>
    [RelayCommand(CanExecute = nameof(CanSuggest))]
    private void Suggest()
    {
        if (_graph is null)
            return;
        var names = _graph.Report.Proposal?.Nodes ?? _graph.Report.Frontier;
        _applyError = "";
        _cut.Clear();
        foreach (var id in _graph.IdsOf(names))
            _cut.Add(id);
        Picked = null;
        Refresh();
        if (_cut.Count > 0)
            ScrollRequested?.Invoke(_cut.Min());
    }

    [RelayCommand]
    private void RemoveCut(GraphCutRow? row)
    {
        if (row is null || IsBusy || !_cut.Remove(row.Id))
            return;
        _applyError = "";
        Refresh();
    }

    /// <summary>A row of the lists: bring that operator into sight; for one that cannot run, show why.</summary>
    [RelayCommand]
    private void Focus(object? row)
    {
        switch (row)
        {
            case GraphBadRow bad:
                Picked = bad.Id;
                Refresh();
                ScrollRequested?.Invoke(bad.Id);
                break;
            case GraphCutRow cut:
                ScrollRequested?.Invoke(cut.Id);
                break;
        }
    }

    [RelayCommand]
    private void Back() => _shell.NavigateTo("model");

    /// <summary>For a model no cut helps: back to step 1 and straight to the file picker.</summary>
    [RelayCommand]
    private void OtherModel()
    {
        _shell.NavigateTo("model");
        if (_shell.CurrentPage is ModelStepViewModel model)
            model.PickCommand.Execute(null);
    }

    /// <summary>Makes the cut: the model as it was brought in is cut at the chosen operators and the settings follow.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_graph is null || IsBusy || _shell.IsBusy)
            return;
        var names = _graph.NamesOf(_cut);
        var applied = _project.Model?.Cut?.Nodes ?? [];
        if (names.ToHashSet().SetEquals(applied))
        {
            _shell.NavigateTo("model"); // nothing changed
            return;
        }
        if (_project.Headline.IsBuilt && !_services.Dialogs.Confirm("자를 곳 바꾸기", "자를 곳을 바꾸면 지금까지의 빌드 결과는 쓸 수 없게 됩니다. 계속할까요?"))
            return;

        IsBusy = true;
        _shell.ClearLog();
        _shell.BeginTask(names.Count > 0 ? "모델을 자르는 중" : "원본 모델로 되돌리는 중", ShellViewModel.ModelTitle(_project));
        try
        {
            var result = await Task.Run(() => _importer.CutAsync(_project, names));
            if (names.Count > 0 && result.Cut is null)
            {
                _applyError = result.Problems.Count > 0 ? string.Join(" ", result.Problems) : "원인을 알 수 없어요. 아래 로그를 확인해 주세요.";
                _shell.EndTask("모델을 자르지 못했어요", "", StepState.Bad);
                return;
            }
            _shell.EndTask("준비됨");
            _shell.OnProjectChanged();
            _shell.NavigateTo("model");
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            _applyError = ex.Message;
            _shell.EndTask("모델을 자르지 못했어요", "", StepState.Bad);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- what the screen says

    private void Refresh()
    {
        if (_graph is null)
            return;
        var report = _graph.Report;
        var effect = _graph.Evaluate(_cut);
        Effect = effect;
        var model = _project.Model;
        var applied = _graph.IdsOf(model?.Cut?.Nodes ?? []).ToHashSet();
        var changed = !applied.SetEquals(_cut);
        var proposal = report.Proposal;
        var proposed = proposal is null ? [] : _graph.IdsOf(proposal.Nodes).ToHashSet();
        var decoder = proposal is { Decoder: not null } && proposed.Count > 0 && proposed.SetEquals(_cut) ? proposal.Decoder : null;
        var badTotal = report.Bad.Count;
        var usable = _cut.Count > 0 && !effect.Overlap && effect.BadLeft == 0;

        // header
        Hint = Locked ? "실행하지 못하는 연산이 끝이 아니라 그 앞에 있습니다" : "연산을 누르면 그 출력에서 자릅니다 · 다시 누르면 취소";
        (ChipText, ChipKind) = Locked ? ("NPU에 맞지 않는 구조", ChipKind.Bad)
            : changed ? ("바꾸는 중 · 아직 적용 전", ChipKind.Warn)
            : _cut.Count == 0 ? (badTotal > 0 ? ("직접 골라 주세요", ChipKind.Warn) : ("자를 필요 없음", ChipKind.Ok))
            : model?.Cut?.By == "user" ? ("직접 고른 곳", ChipKind.Info) : ("앱이 골라 두었어요", ChipKind.Info);
        if (Locked)
        {
            BannerTitle = "이 모델은 잘라서 해결할 수 없어요.";
            BannerText = report.Reason == "front"
                ? "NPU가 실행하지 못하는 연산이 모델의 맨 앞에 있어서, 잘라도 NPU에 남는 부분이 없습니다. 다른 모델을 써 주세요."
                : "잘라낼 수 있는 것은 끝부분의 계산뿐입니다. 이 모델은 실행하지 못하는 연산이 중간에 있고, 그 뒤에 모델의 학습된 부분이 이어집니다. 한 번에 검출하는 모델(YOLOv8, SSD)을 써 주세요.";
        }
        else
        {
            (BannerTitle, BannerText) = ("", "");
        }

        // the three numbers
        TotalText = $"{report.Ops:N0}개";
        if (Locked)
        {
            (KeptLabel, KeptText, KeptKind) = ("실행 못 함", $"{badTotal}개", ChipKind.Bad);
            (AwayLabel, AwayText, AwayKind) = ("앞이 막힘", $"{report.BlockedOps:N0}개", ChipKind.Warn);
        }
        else
        {
            (KeptLabel, KeptText, KeptKind) = (usable || badTotal == 0 ? "NPU가 실행" : "NPU 쪽에 남음", $"{effect.KeptOps:N0}개", usable ? ChipKind.Ok : ChipKind.Muted);
            (AwayLabel, AwayText, AwayKind) = ("잘라냄", effect.AwayOps > 0 ? $"{effect.AwayOps:N0}개" : "없음", ChipKind.Muted);
        }

        // operators that cannot run
        Bads.Clear();
        foreach (var node in _graph.Nodes.Where(n => n.State == "bad"))
        {
            var left = effect.StateOf(node.Id) == CutState.Keep;
            Bads.Add(new GraphBadRow(node.Id, node.Op, LayerReasons.Brief(node.LayerType ?? node.Op, node.Why ?? ""),
                Locked ? "" : left ? "남음" : "잘라냄", left, Picked == node.Id, node.Name + " — 누르면 그래프에서 보여 줍니다"));
        }
        HasBads = Bads.Count > 0;
        BadCountText = $"{badTotal}개";
        (BadChipText, BadChipKind) = Locked ? ("잘라낼 수 없음", ChipKind.Bad)
            : effect.BadLeft > 0 ? ($"{effect.BadLeft}개 남음", ChipKind.Bad) : ("모두 잘라냄", ChipKind.Ok);

        // the cuts
        Cuts.Clear();
        foreach (var node in _graph.Nodes.Where(n => _cut.Contains(n.Id)))
            Cuts.Add(new GraphCutRow(node.Id, node.Op, GraphLayout.DetailOf(node), ShortName(node.Name), node.Name));
        HasCuts = Cuts.Count > 0;
        CutCountText = Cuts.Count > 0 ? $"{Cuts.Count}개" : "";
        SuggestText = proposal is { Decoder: not null } ? "제안으로 되돌리기" : "지원되는 마지막 지점 고르기";
        CanSuggest = !Locked && !IsBusy && (proposal is not null || report.Frontier.Count > 0) && !(proposed.Count > 0 && proposed.SetEquals(_cut));

        // why not (a model no cut helps)
        WhyNot.Clear();
        if (Locked)
        {
            WhyNot.Add($"실행하지 못하는 연산 {badTotal}개가 모델의 {(report.Reason == "front" ? "맨 앞" : "중간")}에 있습니다.");
            if (report.Reason != "front" && report.FrontierAway is { } away)
            {
                var share = report.WeightValues > 0 ? away.WeightValues / (double)report.WeightValues : 0;
                WhyNot.Add($"그 뒤에 학습된 층 {away.WeightLayers}개가 이어집니다 (모델 가중치의 {(share * 100).ToString("0", CultureInfo.InvariantCulture)}%). 연산은 지원되지만 앞이 막혀 NPU로 갈 수 없어요.");
                WhyNot.Add("자르면 그 부분이 통째로 사라져서, 남는 것으로는 모델의 결과를 낼 수 없습니다.");
            }
        }

        // the verdict
        HasVerdict = !Locked;
        VerdictNote = effect.DroppedOps > 0 && !effect.Overlap
            ? $"고른 출력에 필요 없는 가지(연산 {effect.DroppedOps}개)는 함께 빠집니다. 그 가지의 값도 필요하면 거기서도 잘라 주세요."
            : "";
        if (_cut.Count == 0)
        {
            (VerdictKind, VerdictTitle, VerdictBody) = badTotal > 0
                ? (ChipKind.Warn, "자르지 않으면 변환되지 않아요", $"실행하지 못하는 연산이 {badTotal}개 있습니다. 그 앞에서 잘라야 NPU가 나머지를 실행할 수 있어요.")
                : (ChipKind.Info, "자를 필요가 없어요", "모든 연산을 NPU가 실행합니다. 그래도 모델의 끝을 떼어내고 싶다면 연산을 눌러 자를 곳을 고르세요.");
        }
        else if (effect.Overlap)
        {
            (VerdictKind, VerdictTitle, VerdictBody) = (ChipKind.Bad, "자르는 곳이 겹쳐요", "고른 곳 가운데 하나가 다른 곳의 뒤쪽에 있습니다. 앞쪽 것만 남기세요.");
        }
        else if (effect.BadLeft > 0)
        {
            (VerdictKind, VerdictTitle, VerdictBody) = (ChipKind.Bad, "실행하지 못하는 연산이 남아요",
                $"NPU 쪽에 빨간 연산이 {effect.BadLeft}개 남습니다. 이대로는 변환되지 않아요. 빨간 연산보다 위에서 자르세요.");
        }
        else
        {
            (VerdictKind, VerdictTitle, VerdictBody) = decoder switch
            {
                "yolo-v8" => (ChipKind.Ok, "YOLOv8 방식과 맞아요",
                    $"출력 {_cut.Count}개(스케일 3개 × 클래스 · 박스)가 툴킷의 YOLOv8 방식과 맞습니다. 박스 계산에 필요한 것은 앱이 만들고, 2 설정도 채워 둡니다" +
                    (proposal?.NumClass is { } nc ? $" (클래스 {nc}개)." : ".")),
                "ssd" => (ChipKind.Ok, "SSD 방식과 맞아요",
                    "점수와 박스 값이 따로 나오는 출력 2개가 툴킷의 SSD 방식과 맞습니다. 박스 계산에는 모델과 함께 받은 .anchor 파일이 필요합니다 (모델 옆에 있으면 앱이 함께 가져옵니다)."),
                _ => (ChipKind.Muted, "맞는 박스 계산 방식이 없어요",
                    "이 출력 모양은 툴킷이 아는 방식(SSD, YOLO)과 맞지 않습니다. \"기타\"로 변환되어 출력값만 나오고, 그 뒤의 계산은 보드에서 직접 작성해야 합니다." +
                    (proposal?.Decoder switch { "yolo-v8" => " 제안으로 되돌리면 YOLOv8 방식을 쓸 수 있어요.", "ssd" => " 제안으로 되돌리면 SSD 방식을 쓸 수 있어요.", _ => "" })),
            };
        }

        if (_applyError.Length > 0)
            (VerdictKind, VerdictTitle, VerdictBody, VerdictNote) = (ChipKind.Bad, "모델을 자르지 못했어요", _applyError, "");

        // the note inside the part that is cut off
        (ZoneKind, ZoneTitle, ZoneBody) = !usable ? (ChipKind.Muted, "잘라낸 구간", _cut.Count > 0 ? "자르는 곳을 먼저 바로잡아 주세요." : "")
            : decoder switch
            {
                "yolo-v8" => (ChipKind.Ok, "잘라낸 구간 → 보드용 코드", "YOLOv8 방식으로 대신 계산합니다. 필요한 격자 좌표는 앱이 만듭니다."),
                "ssd" => (ChipKind.Ok, "잘라낸 구간 → 보드용 코드", "SSD 방식으로 대신 계산합니다. 기준 상자는 모델과 함께 받은 .anchor 파일에서 읽습니다."),
                _ => (ChipKind.Muted, "잘라낸 구간 → 직접 작성", "맞는 방식이 없어 보드에서 직접 계산해야 합니다."),
            };

        // the note next to a picked operator
        if (Picked is { } picked && _graph.Find(picked) is { } chosen && chosen.State != "ok")
        {
            var bad = chosen.State == "bad";
            PopKind = bad ? ChipKind.Bad : ChipKind.Warn;
            PopTitle = chosen.Op + (bad ? " · 실행 못 함" : " · 앞이 막힘");
            PopName = chosen.Name;
            var why = bad ? LayerReasons.Explain(chosen.LayerType ?? chosen.Op, chosen.Why ?? "")
                : "연산 자체는 지원되지만, 앞쪽에 실행하지 못하는 연산이 있어 NPU로 갈 수 없습니다." + (Locked ? "" : " 여기서는 자를 수 없어요.");
            if (why.Length > 0 && why[^1] is not ('.' or ')'))
                why += ".";
            PopBody = why + (effect.StateOf(picked) is CutState.Gone or CutState.Dropped ? " 지금은 잘라낸 구간 안에 있습니다." : "");
        }
        else
        {
            (PopTitle, PopName, PopBody) = ("", "", "");
        }

        // going back to the model as it came in is a choice too, when nothing has to be cut
        CanApply = !Locked && !IsBusy && (usable || (_cut.Count == 0 && badTotal == 0 && applied.Count > 0));
    }

    partial void OnIsBusyChanged(bool value) => Refresh();

    /// <summary>"cv3.0.2/Conv" for "/model.22/cv3.0/cv3.0.2/Conv": enough to recognize the operator in a narrow list.</summary>
    private static string ShortName(string name)
    {
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? string.Join("/", parts) : string.Join("/", parts[^2..]);
    }
}
