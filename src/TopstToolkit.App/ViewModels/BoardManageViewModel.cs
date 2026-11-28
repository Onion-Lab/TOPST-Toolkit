using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Board;

namespace TopstToolkit.App.ViewModels;

/// <summary>A model folder on the board: one the app put there, or one of the board's own.</summary>
public sealed record BoardModelRow(BoardModelInfo Info, string Title, string Caption, bool IsApp);

/// <summary>
/// 보드 › 올린 모델 · 관리 (docs/mockups/mockup-board.html ⑤): the models on the board, the speed of the NPU alone,
/// a long run that watches the temperature, and cleaning up.
/// </summary>
public partial class BoardManageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly BoardViewModel _page;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _soak;
    private readonly List<BoardSoakPoint> _soakPoints = [];
    private string _soakTitle = "";

    public BoardManageViewModel(AppServices services, ShellViewModel shell, BoardViewModel page)
    {
        _services = services;
        _shell = shell;
        _page = page;
    }

    private BoardConnection Board => _shell.Board;

    // ---------------------------------------------------------------- models on the board
    public ObservableCollection<BoardModelRow> Models { get; } = [];
    [ObservableProperty] private bool _isLoadingModels;
    [ObservableProperty] private string _modelsText = "";
    [ObservableProperty] private string _error = "";

    public Task Ready { get; private set; } = Task.CompletedTask;

    public Task RefreshAsync() => Ready = LoadModelsAsync();

    private async Task LoadModelsAsync()
    {
        if (!Board.IsConnected || Board.Agent is not { } agent)
            return;
        IsLoadingModels = true;
        try
        {
            var models = await agent.ListModelsAsync();
            Models.Clear();
            foreach (var m in models.OrderByDescending(m => m.ByApp).ThenByDescending(m => m.ModifiedUnix ?? 0))
                Models.Add(new BoardModelRow(m, TitleOf(m), CaptionOf(m), m.ByApp));
            ModelsText = Models.Count == 0 ? "보드에 모델이 없어요" : "";
        }
        catch (BoardException ex)
        {
            ModelsText = ex.Message;
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    /// <summary>"YOLOv8n" for a model the app put there (its manifest), else the folder without "_quantized".</summary>
    public static string TitleOf(BoardModelInfo m)
    {
        if (m.Manifest is { Model.Length: > 0 } manifest)
            return manifest.Model;
        const string suffix = "_quantized";
        return m.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? m.Name[..^suffix.Length] : m.Name;
    }

    /// <summary>"객체 검출 · 오늘 23:58", "이미지 분류 · 보드 기본".</summary>
    public static string CaptionOf(BoardModelInfo m, DateTimeOffset? now = null)
    {
        var kind = m.Type switch
        {
            "obj" => "객체 검출",
            "class" => "이미지 분류",
            "custom" => "직접 만든 후처리",
            _ => "후처리 없음",
        };
        if (!m.ByApp)
            return $"{kind} · 보드 기본";
        if (DateTimeOffset.TryParse(m.Manifest?.Deployed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return $"{kind} · {When(at, now ?? DateTimeOffset.Now)}";
        return kind;
    }

    /// <summary>"오늘 23:58", "어제", "11월 20일".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var day = at.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        return day == today ? $"오늘 {at.ToLocalTime():HH:mm}" : day == today.AddDays(-1) ? "어제" : $"{day.Month}월 {day.Day}일";
    }

    [RelayCommand]
    private void RunModel(BoardModelRow? row)
    {
        if (row is null)
            return;
        // the open project's own model goes back to the project's way (letterbox, names) rather than the bare folder
        if (row.IsApp && _shell.Project is { } project && row.Info.Manifest?.Project == project.Id)
            _page.Run.UseProjectModelCommand.Execute(null);
        else
            _page.Run.UseBoardModel(row.Info);
        _page.ShowRunTab();
    }

    [RelayCommand]
    private async Task DeleteModelAsync(BoardModelRow? row)
    {
        if (row is null || !row.IsApp || Board.Agent is not { } agent)
            return;
        if (!_services.Dialogs.Confirm("보드에서 지우기", $"보드에서 {row.Title}을(를) 지울까요? PC의 프로젝트는 그대로 있어요."))
            return;
        try
        {
            await agent.DeleteModelAsync(row.Info.Name);
            await LoadModelsAsync();
        }
        catch (BoardException ex)
        {
            Error = ex.Message;
        }
    }

    // ---------------------------------------------------------------- speed on the board
    public ObservableCollection<FactRow> BenchRows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BenchCommand), nameof(StartSoakCommand))]
    private bool _isBusy;

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task BenchAsync()
    {
        if (Board.IsRunning || Board.Agent is not { } agent)
            return;
        IsBusy = true;
        Error = "";
        Board.BeginRun(_page.Run.RunLabel("속도 재기"));
        _shell.BeginTask("보드에서 속도 재는 중", "100번");
        try
        {
            using var cts = new CancellationTokenSource();
            if (await _page.Run.PrepareTargetAsync(cts.Token) is not { } target)
            {
                Error = _page.Run.Error;
                _shell.EndTask("속도를 재지 못했어요", "", StepState.Bad);
                return;
            }
            var npu = _page.Run.Npu;
            var bench = await agent.BenchAsync(target.RemoteDir, npu, 100, cts.Token);
            if (bench.Error is { } error)
                throw new BoardException(BoardFailure.Command, "보드에서 모델을 돌리지 못했어요", error);
            var text = $"평균 {Ms(bench.Average)} ms" + (target.PcEstimateMs is { } pc ? $" (PC 추정 {Ms(pc)} ms)" : "");
            var label = $"{target.Title} · NPU {npu}";
            if (BenchRows.FirstOrDefault(r => r.Label == label) is { } old)
                BenchRows.Remove(old);
            BenchRows.Insert(0, new FactRow(label, text));
            _shell.PostLog($"보드 속도 {label}: {bench.Count}번 · 평균 {Ms(bench.Average)} · 최소 {Ms(bench.Min)} · 최대 {Ms(bench.Max)} ms");
            _shell.EndTask("보드 속도 재기 끝", text);
        }
        catch (BoardException ex)
        {
            Error = ex.Message;
            if (ex.Detail is { Length: > 0 } d)
                _shell.PostLog("보드: " + d.Trim());
            _shell.EndTask("속도를 재지 못했어요", "", StepState.Bad);
        }
        finally
        {
            Board.EndRun();
            IsBusy = false;
        }
    }

    private static string Ms(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- long run
    public IReadOnlyList<string> SoakChoices { get; } = ["1분", "10분", "30분", "1시간"];
    private static readonly int[] SoakMinutes = [1, 10, 30, 60];

    [ObservableProperty] private int _soakChoice = 2;
    [ObservableProperty] private bool _isSoaking;
    [ObservableProperty] private double _soakFraction;
    [ObservableProperty] private string _soakText = "";
    [ObservableProperty] private string _soakResult = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSoakCommand))]
    private bool _hasSoakLog;

    /// <summary>The long run's length; captures and tests set a shorter one.</summary>
    public TimeSpan? SoakOverride { get; set; }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartSoakAsync()
    {
        if (Board.IsRunning || Board.Agent is not { } agent)
            return;
        var duration = SoakOverride ?? TimeSpan.FromMinutes(SoakMinutes[Math.Clamp(SoakChoice, 0, SoakMinutes.Length - 1)]);
        _soak = new CancellationTokenSource();
        var ct = _soak.Token;
        IsBusy = IsSoaking = true;
        Error = SoakResult = "";
        _soakPoints.Clear();
        HasSoakLog = false;
        SoakFraction = 0;
        SoakText = "시작하는 중…";
        Board.BeginRun(_page.Run.RunLabel("오래 돌리기"));
        var startTemperature = Board.Status?.MaxTemperature;
        _shell.BeginTask("오래 돌려 보는 중", Clock(duration));
        try
        {
            if (await _page.Run.PrepareTargetAsync(ct) is not { } target)
            {
                Error = _page.Run.Error;
                _shell.EndTask("오래 돌려 보지 못했어요", "", StepState.Bad);
                return;
            }
            _soakTitle = target.Title;
            var every = TimeSpan.FromSeconds(duration.TotalMinutes <= 1 ? 2 : 5);
            var points = await Task.Run(() => agent.SoakAsync(target.RemoteDir, _page.Run.Npu, duration, every, point =>
            {
                lock (_soakPoints)
                    _soakPoints.Add(point);
                _ui.BeginInvoke(() =>
                {
                    SoakFraction = Math.Min(1, point.Seconds / duration.TotalSeconds);
                    SoakText = $"{Clock(TimeSpan.FromSeconds(point.Seconds))} / {Clock(duration)} · 평균 {Ms(point.AverageMs)} ms · {point.Temperature:0}°C";
                    _shell.TaskProgress = SoakFraction;
                });
            }, ct), ct);
            SoakResult = Summary(points, startTemperature, ct.IsCancellationRequested);
            HasSoakLog = points.Count > 0;
            _shell.EndTask(ct.IsCancellationRequested ? "오래 돌려 보기를 멈췄어요" : "오래 돌려 보기 끝", SoakResult, ct.IsCancellationRequested ? StepState.Todo : StepState.Ok);
        }
        catch (OperationCanceledException)
        {
            List<BoardSoakPoint> points;
            lock (_soakPoints)
                points = [.. _soakPoints];
            SoakResult = Summary(points, startTemperature, stopped: true);
            HasSoakLog = points.Count > 0;
            _shell.EndTask("오래 돌려 보기를 멈췄어요", "", StepState.Todo);
        }
        catch (BoardException ex)
        {
            Error = ex.Message;
            if (ex.Detail is { Length: > 0 } d)
                _shell.PostLog("보드: " + d.Trim());
            _shell.EndTask("오래 돌려 보지 못했어요", "", StepState.Bad);
        }
        finally
        {
            Board.EndRun();
            IsBusy = IsSoaking = false;
            SoakText = "";
        }
    }

    /// <summary>"10분 · 6만 2천 번 · 평균 9.6 ms · 45 → 52°C".</summary>
    private static string Summary(IReadOnlyList<BoardSoakPoint> points, double? startTemperature, bool stopped)
    {
        var real = points.Where(p => p.Runs > 0).ToList();
        if (real.Count == 0)
            return stopped ? "멈췄어요" : "";
        var last = real[^1];
        var average = real.Average(p => p.AverageMs);
        var first = startTemperature ?? points[0].Temperature;
        var hottest = points.Max(p => p.Temperature);
        return $"{Clock(TimeSpan.FromSeconds(last.Seconds))} · {last.Runs:N0}번 · 평균 {Ms(average)} ms · {first:0} → {hottest:0}°C";
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    [RelayCommand]
    private void StopSoak() => _soak?.Cancel();

    [RelayCommand(CanExecute = nameof(HasSoakLog))]
    private void SaveSoak()
    {
        if (_services.Dialogs.PickFolder("기록을 저장할 폴더",
                TopstToolkit.Core.Workspace.SaveFolders.Prepared(TopstToolkit.Core.Workspace.SaveFolders.BoardResults(_services.Settings.SaveRoot))) is not { } folder)
            return;
        List<BoardSoakPoint> points;
        lock (_soakPoints)
            points = [.. _soakPoints];
        var csv = new StringBuilder("seconds,runs,avg_ms,temperature_c\n");
        foreach (var p in points)
            csv.Append(string.Join(',', p.Seconds.ToString("0.0", CultureInfo.InvariantCulture), p.Runs,
                p.AverageMs.ToString("0.000", CultureInfo.InvariantCulture), p.Temperature.ToString("0.0", CultureInfo.InvariantCulture))).Append('\n');
        var name = $"board-long-run-{DateTime.Now:yyyyMMdd-HHmm}.csv";
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        SoakResult += $" · {name} 저장함";
        _services.Dialogs.ShowInExplorer(path);
    }

    // ---------------------------------------------------------------- cleaning up
    [ObservableProperty] private string _cleanText = "";

    [RelayCommand]
    private async Task CleanTempAsync()
    {
        if (Board.Agent is not { } agent || Board.IsRunning)
            return;
        try
        {
            await agent.CleanTempAsync();
            CleanText = "지웠어요";
        }
        catch (BoardException ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        if (Board.Agent is not { } agent || Board.IsRunning)
            return;
        if (!_services.Dialogs.Confirm("보드 다시 시작", "보드를 다시 시작할까요? 1분쯤 뒤에 다시 연결할 수 있어요."))
            return;
        try
        {
            await agent.RebootAsync();
        }
        catch (BoardException)
        {
            // the board may drop the connection before it answers
        }
        await Board.DisconnectAsync();
        Board.LostText = "보드를 다시 시작하는 중이에요. 1분쯤 뒤에 다시 연결하세요.";
        _page.OnShown();
    }
}
