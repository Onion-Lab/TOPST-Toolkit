using System.Globalization;
using System.Text;

namespace TopstToolkit.Core.Board;

/// <summary>One status line of the board, with the time the PC received it.</summary>
public sealed record BoardSample(DateTimeOffset At, BoardStatus Status);

/// <summary>A stretch the app ran something on the board's NPU ("영상 · YOLOv8n"); <see cref="To"/> is null while it runs.</summary>
public sealed record BoardSpan(DateTimeOffset From, DateTimeOffset? To, string Label);

/// <summary>
/// The board's status over the last minutes (design §5.10 "지난 몇 분 그래프"): every status line of the monitor while
/// the app is connected, for the last <see cref="Keep"/>, and the stretches when the app ran something on the NPU. The
/// times are the PC's (the board's clock may be anything), so the stretches the app marks line up with the samples.
/// </summary>
public sealed class BoardHistory
{
    /// <summary>How far back the graphs of 보드 › 상태 can look (their longest choice).</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(15);

    /// <summary>Two samples further apart than this are not joined by a line: the connection was down in between.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(7);

    private readonly List<BoardSample> _samples = [];
    private readonly List<BoardSpan> _spans = [];

    public IReadOnlyList<BoardSample> Samples => _samples;
    public IReadOnlyList<BoardSpan> Spans => _spans;

    /// <summary>The board the samples come from (its host name).</summary>
    public string Board { get; private set; } = "";

    /// <summary>Starts over for another board; the same board keeps what it had, and a dropped connection leaves a gap.</summary>
    public void UseBoard(string board)
    {
        if (board == Board)
            return;
        Board = board;
        _samples.Clear();
        _spans.Clear();
    }

    public void Add(BoardStatus status, DateTimeOffset at)
    {
        _samples.Add(new BoardSample(at, status));
        Forget(at);
    }

    public void BeginSpan(string label, DateTimeOffset at)
    {
        EndSpan(at);
        _spans.Add(new BoardSpan(at, null, label));
    }

    public void EndSpan(DateTimeOffset at)
    {
        if (_spans.Count > 0 && _spans[^1].To is null)
            _spans[^1] = _spans[^1] with { To = at };
    }

    /// <summary>The stretch going on at <paramref name="at"/>, or null.</summary>
    public BoardSpan? SpanAt(DateTimeOffset at) =>
        _spans.LastOrDefault(s => s.From <= at && (s.To ?? DateTimeOffset.MaxValue) >= at);

    private void Forget(DateTimeOffset now)
    {
        var cut = now - Keep;
        var first = _samples.FindIndex(s => s.At >= cut);
        if (first > 0)
            _samples.RemoveRange(0, first);
        _spans.RemoveAll(s => s.To is { } to && to < cut);
    }

    /// <summary>
    /// "기록 저장": one row per sample — CPU per core, NPU busy, memory, every temperature sensor and what the app ran.
    /// Numbers with a dot whatever the PC's language, so a spreadsheet or a script reads them the same everywhere.
    /// </summary>
    public string ToCsv()
    {
        var cores = _samples.Count == 0 ? 0 : _samples.Max(s => s.Status.Cpu.Length);
        var npus = _samples.Count == 0 ? 0 : _samples.Max(s => s.Status.Npus.Length);
        var sensors = _samples.SelectMany(s => s.Status.Temperatures.Select(t => t.Name)).Distinct().ToList();
        var csv = new StringBuilder("time,cpu_pct");
        for (var c = 0; c < cores; c++)
            csv.Append($",cpu{c + 1}_pct");
        for (var n = 0; n < npus; n++)
            csv.Append($",npu{n}_busy_pct");
        csv.Append(",mem_used_pct,mem_available_mb,npu_mem_used_pct,npu_mem_free_mb");
        foreach (var name in sensors)
            csv.Append(",temp_").Append(Field(name)).Append("_c");
        csv.Append(",run\n");
        foreach (var (at, s) in _samples)
        {
            csv.Append(at.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(N(s.CpuAll));
            for (var c = 0; c < cores; c++)
                csv.Append(',').Append(c < s.Cpu.Length ? N(s.Cpu[c]) : "");
            for (var n = 0; n < npus; n++)
                csv.Append(',').Append(n < s.Npus.Length ? N(s.Npus[n].Busy) : "");
            csv.Append(',').Append(N(s.MemoryUsedPercent)).Append(',').Append(N(s.Memory.Available / 1048576.0))
               .Append(',').Append(N(s.NpuMemory.UsedPercent)).Append(',').Append(N(s.NpuMemory.Free / 1048576.0));
            foreach (var name in sensors)
                csv.Append(',').Append(s.Temperatures.FirstOrDefault(t => t.Name == name) is { } t ? N(t.Celsius) : "");
            csv.Append(',').Append(Field(SpanAt(at)?.Label ?? "")).Append('\n');
        }
        return csv.ToString();
    }

    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Field(string text) =>
        text.IndexOfAny([',', '"', '\n', '\r']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
}
