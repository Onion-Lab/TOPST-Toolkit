using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TopstToolkit.App.Services;
using TopstToolkit.Core.Pipeline;
using TopstToolkit.Core.Projects;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.App.ViewModels;

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

    public void OnShown()
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

        TimeText = perf.InferenceTimeMs.ToString("0.0", Inv);
        FpsText = $"약 {1000 / perf.InferenceTimeMs:0} FPS";

        if (perf.Cclk is { } cclk) Clocks.Add(new FactRow("NPU 코어 (CCLK)", $"{cclk:0} MHz"));
        if (perf.Aclk is { } aclk) Clocks.Add(new FactRow("버스 (ACLK)", $"{aclk:0} MHz"));
        if (perf.Pclk is { } pclk) Clocks.Add(new FactRow("주변 장치 (PCLK)", $"{pclk:0} MHz"));
        if (perf.CpuClk is { } cpu) Clocks.Add(new FactRow("CPU", $"{cpu:0} MHz"));
        if (perf.DramUtilization is { } dram) Clocks.Add(new FactRow("메모리 대역 사용률", $"{dram * 100:0}%"));
        ClocksText = "계산에 쓴 예제 값\n" + string.Join("\n", Clocks.Select(c => $"{c.Label}: {c.Value}"));

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
