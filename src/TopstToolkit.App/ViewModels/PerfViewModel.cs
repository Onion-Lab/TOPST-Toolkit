using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Projects;
using TopstToolkit.Engine.Helpers;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.App.ViewModels;

/// <summary>
/// How the estimate of 6 성능 was made: corrected for what the board was measured to do (since 2026-11-28, BoardTime), the
/// board's clocks alone (no correction could be made), or the toolkit's example clocks (not estimated again yet).
/// </summary>
public enum EstimateBasis { BoardCorrected, BoardClocks, Example }

/// <summary>A named amount with a bar: one board buffer, or one group of layers.</summary>
/// <param name="Fraction">0..1 of the largest row, for the bar.</param>
public sealed record BarRow(string Name, string Hint, string ValueText, double Fraction);

/// <summary>
/// Step 6 "성능": the estimated inference time on the board, the memory the board has to set aside and where the
/// work goes. Every number is an estimate from the toolkit, never a measurement (decision D4). Layout-independent.
/// </summary>
public partial class PerfViewModel : ObservableObject, IPageViewModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ProjectFile _project;

    public PerfViewModel(AppServices services, ShellViewModel shell, ProjectFile project)
    {
        _services = services;
        _shell = shell;
        _project = project;
    }

    public ObservableCollection<BarRow> Buffers { get; } = [];
    public ObservableCollection<BarRow> Groups { get; } = [];
    public ObservableCollection<FactRow> Clocks { get; } = [];
    public ObservableCollection<FactRow> Network { get; } = [];

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _fpsText = "";
    [ObservableProperty] private string _totalMemoryText = "";
    [ObservableProperty] private string _opsText = "";
    [ObservableProperty] private string _groupsCaption = "";
    [ObservableProperty] private bool _hasGroups;
    /// <summary>The clocks the estimate assumed, one per line: a tooltip, not a table (user feedback 2026-11-23, no jargon on the page).</summary>
    [ObservableProperty] private string _clocksText = "";

    /// <summary>How the estimate was made (<see cref="EstimateBasis"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeCaption), nameof(ClockChipText), nameof(ClockChipKind), nameof(ClockNote), nameof(PageNote))]
    private EstimateBasis _basis = EstimateBasis.BoardCorrected;

    public string TimeCaption => Basis switch
    {
        EstimateBasis.BoardCorrected => "사진 한 장을 NPU가 계산하는 시간 · TOPST AI-G 보드에서 잰 값으로 맞춘 추정",
        EstimateBasis.BoardClocks => "사진 한 장을 NPU가 계산하는 시간 · TOPST AI-G 보드의 클럭으로 계산",
        _ => "사진 한 장을 NPU가 계산하는 시간 · 예제 클럭 기준 — 보드 기준 아님",
    };
    public string ClockChipText => Basis switch { EstimateBasis.BoardCorrected => "보드 실측 보정", EstimateBasis.BoardClocks => "보드 클럭", _ => "예제 클럭" };
    public Controls.ChipKind ClockChipKind => Basis == EstimateBasis.Example ? Controls.ChipKind.Warn : Controls.ChipKind.Info;
    public string ClockNote => Basis switch
    {
        EstimateBasis.BoardCorrected => CorrectedNote(),
        EstimateBasis.BoardClocks => "TOPST AI-G 보드의 실제 클럭으로 계산한 시간이에요. 정확한 시간은 보드 화면에서 속도를 재 보세요.",
        _ => "툴킷에 들어 있는 예제 클럭으로 계산한 시간이에요. 보드의 실제 클럭에 따라 달라질 수 있어서, 정확한 시간은 보드에서 재야 합니다.",
    };
    /// <summary>The line at the top right of the page.</summary>
    public string PageNote => Basis == EstimateBasis.BoardCorrected
        ? "보드에서 잰 값으로 맞춘 추정이에요 — 이 모델을 직접 잰 값은 아니에요"
        : "툴킷이 계산한 값입니다 — 보드에서 잰 값이 아닙니다";

    /// <summary>"…모델 47개를 직접 재서 맞춘 계산이고, 평균 오차는 약 3%예요…": the numbers of the coefficients the engine carries.</summary>
    private string CorrectedNote()
    {
        var check = "";
        try
        {
            if (_services.EngineLayout?.BoardTimeConfig is { } file && File.Exists(file))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("check", out var c) && c.TryGetProperty("models", out var n) && c.TryGetProperty("meanPercent", out var mean))
                    check = $" 모델 {n.GetInt32()}개를 보드에서 직접 재서 맞춘 계산이고, 평균 오차는 약 {mean.GetDouble().ToString("0", Inv)}%예요.";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        return "툴킷이 계산한 시간에 TOPST AI-G 보드에서 잰 차이를 더한 값이에요." + check + " 정확한 시간은 보드 화면에서 속도를 재 보세요.";
    }

    public void OnShown()
    {
        Show();
        // a project built with other clocks than today's (before 2026-11-28, or another board): its estimate again, about a second
        Refreshed = HasData && _services.Engine is { } engine && !_shell.IsBusy ? RefreshAsync(engine) : Task.CompletedTask;
    }

    /// <summary>The estimate computed again with today's clocks, when it had to be (captures wait for it).</summary>
    public Task Refreshed { get; private set; } = Task.CompletedTask;

    private async Task RefreshAsync(TopstToolkit.Core.Pipeline.EngineSession engine)
    {
        try
        {
            if (await new ProjectBuilder(engine, _services.Projects).RefreshPerfAsync(_project) is not null)
                Show();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _shell.PostLog("성능: 추정을 다시 계산하지 못했어요 · " + e.Message);
        }
    }

    private void Show()
    {
        Buffers.Clear();
        Groups.Clear();
        Clocks.Clear();
        Network.Clear();
        IsStale = _project.Headline is { IsBuilt: true, NeedsRebuild: true };
        var paths = BuildPaths.For(_services.Projects, _project);
        var perf = paths is null ? null : PerfResult.ParseFile(paths.PerfJson);
        HasData = _project.Headline.IsBuilt && perf is not null;
        if (!HasData || paths is null || perf is null)
            return;

        var estimate = BoardTimeEstimate.ParseFile(paths.BoardTimeJson);
        var ms = estimate?.EstimateMs ?? perf.InferenceTimeMs;
        TimeText = ms.ToString("0.0", Inv);
        FpsText = $"약 {1000 / ms:0} FPS";

        if (perf.Cclk is { } cclk) Clocks.Add(new FactRow("NPU 코어 (CCLK)", $"{cclk:0} MHz"));
        if (perf.Aclk is { } aclk) Clocks.Add(new FactRow("버스 (ACLK)", $"{aclk:0} MHz"));
        if (perf.Pclk is { } pclk) Clocks.Add(new FactRow("주변 장치 (PCLK)", $"{pclk:0} MHz"));
        if (perf.CpuClk is { } cpu) Clocks.Add(new FactRow("CPU", $"{cpu:0} MHz"));
        if (perf.DramUtilization is { } dram) Clocks.Add(new FactRow("메모리 대역 사용률", $"{dram * 100:0}%"));
        var boardClocks = PerfClocks.PresetOf(_project.Options.Perf.ClockPreset) == PerfClocks.BoardPreset && perf.Aclk is not 666;
        Basis = boardClocks && estimate is not null ? EstimateBasis.BoardCorrected : boardClocks ? EstimateBasis.BoardClocks : EstimateBasis.Example;
        ClocksText = (boardClocks ? "계산에 쓴 보드 클럭\n" : "계산에 쓴 예제 값\n") + string.Join("\n", Clocks.Select(c => $"{c.Label}: {c.Value}"))
            + (estimate is { } e ? "\n\n" + CorrectionText(e) : "");

        if (NetworkBuffers.ParseFile(System.IO.Path.Combine(paths.CodeDir, "network_buf.txt")) is { } b)
        {
            (string Name, string Hint, long Bytes)[] rows =
            [
                ("가중치", "모델이 배운 값", b.Weight), ("작업 버퍼", "계산 중간 결과", b.Work), ("출력", "NPU가 내놓는 값", b.OutputActivation),
                ("입력", "사진 한 장", b.InputActivation), ("NPU 명령", "실행 순서", b.Code),
            ];
            var largest = Math.Max(1, rows.Max(r => r.Bytes));
            foreach (var (name, hint, bytes) in rows)
                Buffers.Add(new BarRow(name, hint, Size(bytes), (double)bytes / largest));
            TotalMemoryText = Size(b.Total);
        }

        if (NetworkGroupInfo.ParseFile(System.IO.Path.Combine(paths.CodeDir, "network_group_info.csv")) is { } groups)
        {
            OpsText = Ops(groups.TotalConvOps);
            var top = groups.Groups.Where(g => g.ConvOps > 0).OrderByDescending(g => g.ConvOps).Take(6).ToList();
            var largest = Math.Max(1, top.Count == 0 ? 1 : top[0].ConvOps);
            foreach (var g in top)
                Groups.Add(new BarRow(OriginalName(g.Name), "", Ops(g.ConvOps), (double)g.ConvOps / largest));
            HasGroups = Groups.Count > 0;
            GroupsCaption = $"묶음 {groups.Groups.Count}개 가운데 일이 많은 순서";
        }

        var header = NetworkHeaderSummary.ParseFile(System.IO.Path.Combine(paths.CodeDir, "network.h"));
        if (header?.Input is { } input)
            Network.Add(new FactRow("NPU 입력", string.Join(" × ", input.Shape)));
        if (_project.Headline is { CompatibleLayers: { } ok, TotalLayers: { } total })
            Network.Add(new FactRow("NPU에서 실행되는 레이어", $"{ok} / {total}"));
        if (OpsText.Length > 0)
            Network.Add(new FactRow("합성곱 연산량", OpsText));
        if (FileSize(System.IO.Path.Combine(paths.CodeDir, "quantized_network.bin")) is { } weights)
            Network.Add(new FactRow("가중치 파일", Size(weights)));
        if (_project.Model is { } model)
            Network.Add(new FactRow("원본 모델 파일", Size(model.SizeBytes)));
        Network.Add(new FactRow("보드 라이브러리 (net.so)", FileSize(paths.NetSo) is { } so ? Size(so) : "없음"));
    }

    /// <summary>The tooltip's account of the estimate: the simulator's value and what the board adds or takes off.</summary>
    private static string CorrectionText(BoardTimeEstimate e)
    {
        static string Signed(double ms) => (ms >= 0 ? "+" : "−") + Math.Abs(ms).ToString("0.00", Inv) + " ms";
        var lines = new List<string> { $"툴킷 계산: {e.SimulatorMs.ToString("0.00", Inv)} ms", $"보드에서 잰 차이: {Signed(e.CorrectionMs)}" };
        if (e.Terms is { } t)
        {
            if (t.FixedMs != 0) lines.Add($"  한 번 돌릴 때마다 {Signed(t.FixedMs)}");
            if (t.AvgPoolMs != 0) lines.Add($"  평균 풀링 {Signed(t.AvgPoolMs)}");
            if (t.MaxPoolMs != 0) lines.Add($"  맥스 풀링 {Signed(t.MaxPoolMs)}");
            if (t.BigStemMs != 0) lines.Add($"  큰 첫 합성곱 {Signed(t.BigStemMs)}");
            if (t.DepthwiseMs != 0) lines.Add($"  깊이별 합성곱 {Signed(t.DepthwiseMs)}");
        }
        return string.Join("\n", lines);
    }

    private static long? FileSize(string path) => System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : null;

    private static string Size(long bytes) => bytes >= 1048576
        ? (bytes / 1048576.0).ToString("0.0", Inv) + " MB"
        : (bytes / 1024.0).ToString("0", Inv) + " KB";

    /// <summary>Multiply-accumulate operations: "26.0 G", "310 M".</summary>
    private static string Ops(long ops) => ops >= 1_000_000_000
        ? (ops / 1e9).ToString("0.0", Inv) + " G"
        : (ops / 1e6).ToString("0", Inv) + " M";

    /// <summary>"Mul_1___model_1_act_Mul" → "model_1_act_Mul": the name the layer has in the original model.</summary>
    private static string OriginalName(string enlightName)
    {
        var at = enlightName.IndexOf("__", StringComparison.Ordinal);
        var original = at >= 0 ? enlightName[at..].TrimStart('_') : "";
        return original.Length > 0 ? original : enlightName;
    }

    [RelayCommand]
    private void GoToBuild() => _shell.NavigateTo("build");

    [RelayCommand]
    private void GoToExport() => _shell.NavigateTo("export");
}
