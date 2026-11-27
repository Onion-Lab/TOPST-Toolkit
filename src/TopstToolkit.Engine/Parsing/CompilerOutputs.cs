using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TopstToolkit.Engine.Parsing;

/// <summary>The compiler output folder <c>&lt;dump-root&gt;\&lt;net&gt;\</c> (feature/04 §4.3).</summary>
public static class CompilerOutputs
{
    /// <summary>The 13 files every verified compile produced.</summary>
    public static readonly IReadOnlyList<string> ExpectedFiles =
    [
        "network.h", "post_process.c", "npu_cmd.h", "npu_cmd.bin", "npu_cmd.txt", "quantized_network.bin",
        "oe_network.bin", "oe_network.h", "network_buf.txt", "network_group_info.csv",
        "oact_entry_table.py", "trap_point.py", "compile_debug.log",
    ];

    /// <summary>The compiler writes to <c>&lt;dump-root&gt;\&lt;model file name without extension&gt;</c>.</summary>
    public static string OutputDirFor(string dumpRoot, string quantizedModelPath) =>
        Path.Combine(dumpRoot, Path.GetFileNameWithoutExtension(quantizedModelPath));

    public static IReadOnlyList<string> FindMissing(string outputDir) =>
        ExpectedFiles.Where(f => !File.Exists(Path.Combine(outputDir, f))).ToList();

    /// <summary><c>compile_debug.log</c> holds <c>NPU compiler: done sucessfully.</c> (sic) on success, a call stack on failure.</summary>
    public static bool DebugLogReportsSuccess(string outputDir)
    {
        var log = Path.Combine(outputDir, "compile_debug.log");
        return File.Exists(log) && File.ReadAllText(log, Encoding.UTF8).Contains("done sucessfully", StringComparison.Ordinal);
    }
}

/// <summary><c>network_buf.txt</c>: five hexadecimal buffer sizes the board runtime allocates.</summary>
public sealed partial record NetworkBuffers(long Code, long Weight, long Work, long InputActivation, long OutputActivation)
{
    public long Total => Code + Weight + Work + InputActivation + OutputActivation;

    public static NetworkBuffers? Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var m = Row().Match(line);
            if (m.Success)
                values[m.Groups[1].Value] = long.Parse(m.Groups[2].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        string[] keys = ["CODE", "WGHT", "WORK", "IACT", "OACT"];
        if (!keys.All(values.ContainsKey))
            return null;
        return new NetworkBuffers(values["CODE"], values["WGHT"], values["WORK"], values["IACT"], values["OACT"]);
    }

    public static NetworkBuffers? ParseFile(string path) => File.Exists(path) ? Parse(File.ReadLines(path)) : null;

    [GeneratedRegex(@"^\s*(CODE|WGHT|WORK|IACT|OACT)\s+([0-9A-Fa-f]+)\s*$")]
    private static partial Regex Row();
}

/// <summary>An activation tensor declared in <c>network.h</c> (<c>static enlight_act_tensor_t &lt;name&gt; = {...}</c>).</summary>
public sealed record NetworkTensor(string Name, string DataType, IReadOnlyList<int> Shape, double Scale, long Size);

/// <summary>Summary of <c>network.h</c>: the tensors the board code exchanges with the NPU.</summary>
public sealed partial record NetworkHeaderSummary(IReadOnlyList<NetworkTensor> Tensors)
{
    public NetworkTensor? Input => Tensors.FirstOrDefault(t => t.Name.StartsWith("Input", StringComparison.Ordinal));

    public static NetworkHeaderSummary Parse(string text)
    {
        var tensors = new List<NetworkTensor>();
        foreach (Match m in TensorBlock().Matches(text))
        {
            var fields = SplitFields(m.Groups[2].Value);
            // { DTYPE, (void*)0x0, offset, size, ?, ?, {dims}, scale, ?, ?, "name" }
            if (fields.Count < 8)
                continue;
            var shape = fields[6].Trim('{', '}', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList();
            double.TryParse(fields[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale);
            long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            tensors.Add(new NetworkTensor(m.Groups[1].Value, fields[0], shape, scale, size));
        }
        return new NetworkHeaderSummary(tensors);
    }

    /// <summary>The tensor table sits at the top; large models have a multi-megabyte header, so only the head is read.</summary>
    public static NetworkHeaderSummary? ParseFile(string path, int maxChars = 2_000_000)
    {
        if (!File.Exists(path))
            return null;
        using var reader = new StreamReader(path, Encoding.UTF8);
        var buffer = new char[maxChars];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return Parse(new string(buffer, 0, read));
    }

    private static List<string> SplitFields(string body)
    {
        var fields = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in body)
        {
            if (c == '{') depth++;
            if (c == '}') depth--;
            if (c == ',' && depth == 0)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.ToString().Trim().Length > 0)
            fields.Add(current.ToString().Trim());
        return fields;
    }

    [GeneratedRegex(@"static\s+enlight_act_tensor_t\s+(\w+)\s*=\s*\{((?:[^{}]|\{[^{}]*\})*)\}\s*;", RegexOptions.Singleline)]
    private static partial Regex TensorBlock();
}

/// <summary>perf_simulator's JSON (feature/04 §4.6): one estimated time plus a copy of the clock settings.</summary>
public sealed record PerfResult(double InferenceTimeMs, double? Cclk, double? Aclk, double? Pclk, double? CpuClk, double? DramUtilization)
{
    public static PerfResult? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("Inference_Time(msec)", out var time) || time.ValueKind != JsonValueKind.Number)
            return null;
        return new PerfResult(time.GetDouble(), Num(root, "CCLK"), Num(root, "ACLK"), Num(root, "PCLK"), Num(root, "CPU_CLK"), Num(root, "DRAM_UTILIZATION"));
    }

    public static PerfResult? ParseFile(string path) => File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : null;

    private static double? Num(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>One group of layers the NPU runs as a unit, with its convolution work (multiply-accumulate operations).</summary>
public sealed record NetworkGroup(string Name, long ConvOps);

/// <summary>
/// <c>network_group_info.csv</c>: the total convolution work of the network on the first line, then one row per group.
/// Used for the "연산량" numbers of the performance screen.
/// </summary>
public sealed record NetworkGroupInfo(long TotalConvOps, IReadOnlyList<NetworkGroup> Groups)
{
    public static NetworkGroupInfo? Parse(IEnumerable<string> lines)
    {
        long? total = null;
        var groups = new List<NetworkGroup>();
        var inTable = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (total is null && long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            {
                total = t;
                continue;
            }
            if (line.StartsWith("group_layer,", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }
            if (!inTable)
                continue;
            var cells = line.Split(',');
            if (cells.Length >= 2 && long.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ops))
                groups.Add(new NetworkGroup(cells[0], ops));
        }
        if (total is null && groups.Count == 0)
            return null;
        return new NetworkGroupInfo(total ?? groups.Sum(g => g.ConvOps), groups);
    }

    public static NetworkGroupInfo? ParseFile(string path) => File.Exists(path) ? Parse(File.ReadLines(path)) : null;
}
