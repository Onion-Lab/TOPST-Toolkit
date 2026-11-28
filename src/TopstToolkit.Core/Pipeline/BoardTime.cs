using TopstToolkit.Engine;

namespace TopstToolkit.Core.Pipeline;

/// <summary>
/// The estimated NPU time on the TOPST AI-G board (design §11, 2026-11-28): the simulator's value with the board's clocks
/// (<see cref="PerfClocks"/>) corrected for what the models measured on the board showed (<c>engine\board\topst-ai-g-time.json</c>,
/// computed by <c>engine\helpers\board_time.py</c>). Where its files go beside the simulator's JSON, and its part of the
/// perf stage's build key.
/// </summary>
public static class BoardTime
{
    /// <summary>Changes when the way of computing changes, so that built projects compute their estimate again.</summary>
    public const string Version = "1";

    /// <summary>The coefficients the engine carries, or null for an engine from before them (the simulator's value alone).</summary>
    public static string? ConfigFor(EngineLayout layout) => File.Exists(layout.BoardTimeConfig) ? layout.BoardTimeConfig : null;

    /// <summary>For the perf key: the version and the coefficients ("none" without them).</summary>
    public static string KeyOf(string? config) => config is null ? "none" : $"bt{Version}-{PerfClocks.HashOf(config)}";

    /// <summary><c>&lt;stem&gt;_quantized_board_time.json</c> beside <c>&lt;stem&gt;_quantized_performance.json</c>.</summary>
    public static string JsonFor(string perfJson) => Beside(perfJson, "_board_time.json");

    /// <summary>The simulator's timeline (its <c>--debug</c> CSV) beside its JSON.</summary>
    public static string TimelineFor(string perfJson) => Beside(perfJson, "_timeline.csv");

    private static string Beside(string perfJson, string suffix)
    {
        var name = Path.GetFileNameWithoutExtension(perfJson);
        if (name.EndsWith("_performance", StringComparison.OrdinalIgnoreCase))
            name = name[..^"_performance".Length];
        return Path.Combine(Path.GetDirectoryName(perfJson)!, name + suffix);
    }
}
