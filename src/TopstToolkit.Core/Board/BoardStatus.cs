using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopstToolkit.Core.Board;

/// <summary>One status line of <c>topst-runner monitor</c> (engine/board/topst_runner.py, <c>status_line</c>).</summary>
public sealed class BoardStatus
{
    [JsonPropertyName("t")] public double Time { get; init; }
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    /// <summary>The board's PRETTY_NAME, e.g. "Telechips Baseline (Poky/meta-telechips/meta-core) 4.0.17".</summary>
    [JsonPropertyName("os")] public string Os { get; init; } = "";
    [JsonPropertyName("uptime")] public long UptimeSeconds { get; init; }
    /// <summary>Use of each CPU core over the last interval, percent.</summary>
    [JsonPropertyName("cpu")] public double[] Cpu { get; init; } = [];
    [JsonPropertyName("cpuAll")] public double CpuAll { get; init; }
    [JsonPropertyName("mem")] public BoardMemory Memory { get; init; } = new();
    /// <summary>The reserved memory the NPU's buffers come from (CMA).</summary>
    [JsonPropertyName("npuMem")] public BoardNpuMemory NpuMemory { get; init; } = new();
    [JsonPropertyName("temps")] public BoardTemperature[] Temperatures { get; init; } = [];
    [JsonPropertyName("disk")] public BoardSpace Disk { get; init; } = new();
    [JsonPropertyName("tmp")] public BoardSpace Temp { get; init; } = new();
    [JsonPropertyName("net")] public BoardNetwork Network { get; init; } = new();
    [JsonPropertyName("npu")] public BoardNpu[] Npus { get; init; } = [];
    /// <summary>The NPU library (libtcndnpu.so.1) is on the board; null from a runner older than version 2.</summary>
    [JsonPropertyName("lib")] public bool? HasNpuLibrary { get; init; }
    /// <summary>/dev/npu0 and /dev/npu1 are there.</summary>
    [JsonPropertyName("dev")] public bool[] NpuDevices { get; init; } = [];
    [JsonPropertyName("runner")] public string Runner { get; init; } = "";

    /// <summary>"Telechips Baseline 4.0.17": the name without the build layers in brackets.</summary>
    public string OsShort => System.Text.RegularExpressions.Regex.Replace(Os, @"\s*\([^)]*\)", "").Trim();

    public double MaxTemperature => Temperatures.Length == 0 ? 0 : Temperatures.Max(t => t.Celsius);

    public double MemoryUsedPercent => Memory.Total <= 0 ? 0 : 100.0 * (Memory.Total - Memory.Available) / Memory.Total;

    /// <summary>The busier of the two NPUs, percent of the last interval.</summary>
    public double NpuBusy => Npus.Length == 0 ? 0 : Npus.Max(n => n.Busy);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static BoardStatus? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
            return null;
        try
        {
            return JsonSerializer.Deserialize<BoardStatus>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class BoardMemory
{
    [JsonPropertyName("total")] public long Total { get; init; }
    [JsonPropertyName("avail")] public long Available { get; init; }
}

public sealed class BoardNpuMemory
{
    [JsonPropertyName("total")] public long Total { get; init; }
    [JsonPropertyName("free")] public long Free { get; init; }
    public double UsedPercent => Total <= 0 ? 0 : 100.0 * (Total - Free) / Total;
}

public sealed class BoardTemperature
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("c")] public double Celsius { get; init; }
}

public sealed class BoardSpace
{
    [JsonPropertyName("total")] public long Total { get; init; }
    [JsonPropertyName("free")] public long Free { get; init; }
}

public sealed class BoardNetwork
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("mbps")] public int Mbps { get; init; }
}

public sealed class BoardNpu
{
    /// <summary>The cluster's clock is on (the board turns an idle cluster's clock off).</summary>
    [JsonPropertyName("on")] public bool ClockOn { get; init; }
    /// <summary>Share of the last interval the cluster spent on the app's inferences, percent.</summary>
    [JsonPropertyName("busy")] public double Busy { get; init; }
}
