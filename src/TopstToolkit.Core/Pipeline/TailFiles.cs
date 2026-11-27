using System.Text.Json;
using TopstToolkit.Engine.Board;
using TopstToolkit.Engine.Helpers;

namespace TopstToolkit.Core.Pipeline;

/// <summary>
/// What a project keeps of the part of its model behind the cut (design §5.8, stage 나), in <c>out\tail\</c>:
/// the generated source (<see cref="TailCode.SourceName"/>), the answers of the helpers and the values the PC computed
/// for every check picture (<c>pc\&lt;picture&gt;\&lt;index&gt;.bin</c>, float32).
/// </summary>
public static class TailFiles
{
    /// <summary><c>read_enlight.py</c> for the quantized network: the layer names that connect the cut to <c>network.h</c>.</summary>
    public const string Layers = "layers.json";
    /// <summary><c>gen_tail.py</c>: what was generated, or why nothing was.</summary>
    public const string Generation = "gen_tail.json";
    /// <summary><c>eval_tail.py</c>: the outputs per check picture.</summary>
    public const string Evaluation = "eval_tail.json";
    public const string PcDir = "pc";

    public static TailGeneration? LoadGeneration(string tailDir) => Load<TailGeneration>(Path.Combine(tailDir, Generation));

    public static TailEvaluation? LoadEvaluation(string tailDir) => Load<TailEvaluation>(Path.Combine(tailDir, Evaluation));

    /// <summary>The folder with the PC's values for one check picture (named like its dump folder).</summary>
    public static string PcFolder(string tailDir, string picture) => Path.Combine(tailDir, PcDir, picture);

    /// <summary>The PC's outputs for one check picture (the name of its result or dump folder), or null.</summary>
    public static TailRun? RunFor(string tailDir, string picture) =>
        LoadEvaluation(tailDir) is { Ok: true } evaluation
            ? evaluation.Runs.FirstOrDefault(r => string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(r.Dump)), picture, StringComparison.OrdinalIgnoreCase))
            : null;

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
