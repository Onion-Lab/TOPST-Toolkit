using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine;

namespace TopstToolkit.Core.Pipeline;

/// <summary>The NPU clocks of a board in MHz, as the estimate takes them (perf_simulator's CCLK, ACLK, PCLK, CPU_CLK).</summary>
public sealed record NpuClocks(double Core, double Bus, double Peripheral, double Cpu)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"NPU {Core:0} · AXI {Bus:0} · APB {Peripheral:0} · CPU {Cpu:0} MHz");
}

/// <summary>
/// Which clock settings the estimated board time is computed with (design §11 2026-11-28, decision D4 revisited).
/// Until then every build used the toolkit's example file; the TOPST AI-G board runs its NPU AXI, CPU and APB faster
/// (800 · 800 · 200 MHz against 666 · 666 · 100), and with its own clocks the estimate meets the measured time.
/// <list type="bullet">
/// <item><see cref="BoardPreset"/> (new projects, and older ones on their next build): the board's file in the engine,
/// or the clocks read from the board the app was last connected to when they differ (<see cref="OverrideFile"/>).</item>
/// <item><see cref="ExamplePreset"/>: the toolkit's example, kept for comparison.</item>
/// </list>
/// </summary>
public static class PerfClocks
{
    public const string BoardPreset = "topst-ai-g";
    public const string ExamplePreset = "toolkit-example";

    /// <summary>The connected board's clocks when they are not the ones in the engine's file: <c>&lt;workspace&gt;\config\board-npu.json</c>.</summary>
    public static string OverrideFile(WorkspaceRoot workspace) => Path.Combine(workspace.Root, "config", "board-npu.json");

    /// <summary>
    /// The preset a project's estimate uses. Projects made before 2026-11-28 carry "toolkit-example", which was never
    /// chosen by anyone (it was the only file there was): they move to the board's clocks.
    /// </summary>
    public static string PresetOf(string? stored) => stored is null or "" or ExamplePreset ? BoardPreset : stored;

    /// <summary>The clock file the perf simulator gets for <paramref name="preset"/>.</summary>
    public static string ConfigFor(string preset, EngineLayout layout, WorkspaceRoot workspace)
    {
        if (preset == ExamplePreset || !File.Exists(layout.BoardNpuConfig))
            return layout.ExampleNpuConfig;
        var board = OverrideFile(workspace);
        return File.Exists(board) ? board : layout.BoardNpuConfig;
    }

    /// <summary>What a clock file says, for the build's key (a changed file computes the estimate again).</summary>
    public static string HashOf(string config) =>
        File.Exists(config) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(config)))[..16] : "";

    public static NpuClocks? Read(string config)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(config));
            var r = json.RootElement;
            return new NpuClocks(r.GetProperty("CCLK").GetDouble(), r.GetProperty("ACLK").GetDouble(), r.GetProperty("PCLK").GetDouble(), r.GetProperty("CPU_CLK").GetDouble());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The board the app connected to says its clocks are <paramref name="clocks"/>: when they differ from the engine's
    /// file, the next estimates use them (the file's memory settings stay); when they match, the override goes.
    /// Returns true when the override was written or removed.
    /// </summary>
    public static bool UseBoardClocks(NpuClocks clocks, EngineLayout layout, WorkspaceRoot workspace)
    {
        var file = OverrideFile(workspace);
        if (Read(layout.BoardNpuConfig) is not { } shipped || JsonNode.Parse(File.ReadAllText(layout.BoardNpuConfig)) is not JsonObject config)
            return false;
        if (clocks == shipped)
        {
            if (!File.Exists(file))
                return false;
            File.Delete(file);
            return true;
        }
        config["CCLK"] = clocks.Core;
        config["ACLK"] = clocks.Bus;
        config["PCLK"] = clocks.Peripheral;
        config["CPU_CLK"] = clocks.Cpu;
        var text = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (File.Exists(file) && File.ReadAllText(file) == text)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return true;
    }
}
