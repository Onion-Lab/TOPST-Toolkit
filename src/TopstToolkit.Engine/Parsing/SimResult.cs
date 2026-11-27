using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TopstToolkit.Engine.Parsing;

public enum SimResultKind
{
    Detection,
    Classification,
    Tensor,
    Unrecognized,
}

public readonly record struct ImageSize(int Width, int Height);

/// <param name="ScorePercent">Score in percent (the file prints score × 100).</param>
/// <param name="Label">Class label, or the class number when the model has no labels.</param>
public sealed record Detection(int Index, double X1, double Y1, double X2, double Y2, double ScorePercent, int ClassNumber, string Label);

/// <summary>
/// One <c>&lt;image&gt;_result.txt</c> written by enlight_sim (feature/04 §4.4).
/// Detection files have a block in original-image pixels and, when anything was found, a block in network-input pixels.
/// </summary>
public sealed partial class SimResult
{
    public SimResultKind Kind { get; private init; } = SimResultKind.Unrecognized;
    public ImageSize? OriginalSize { get; private init; }
    public ImageSize? NetworkSize { get; private init; }
    /// <summary>Boxes in original image pixels (letterbox padding already removed by the toolkit).</summary>
    public IReadOnlyList<Detection> Detections { get; private init; } = [];
    /// <summary>The same boxes in network input pixels.</summary>
    public IReadOnlyList<Detection> NetworkDetections { get; private init; } = [];
    public int? ClassNumber { get; private init; }
    public string? ClassLabel { get; private init; }
    public string? TensorShape { get; private init; }

    public static SimResult Parse(IEnumerable<string> lines)
    {
        var original = new List<Detection>();
        var network = new List<Detection>();
        List<Detection>? target = null;
        ImageSize? originalSize = null, networkSize = null;
        bool classificationHeader = false, sawDetectionBlock = false;
        int? classNumber = null;
        string? classLabel = null, tensorShape = null;

        foreach (var line in lines)
        {
            Match m;
            if ((m = Resolution().Match(line)).Success)
            {
                var size = new ImageSize(int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture));
                sawDetectionBlock = true;
                if (m.Groups[3].Value.StartsWith("original", StringComparison.Ordinal))
                {
                    originalSize = size;
                    target = original;
                }
                else
                {
                    networkSize = size;
                    target = network;
                }
                continue;
            }

            if (target is not null && (m = DetectionRow().Match(line)).Success)
            {
                target.Add(new Detection(
                    int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                    D(m.Groups[2]), D(m.Groups[3]), D(m.Groups[4]), D(m.Groups[5]), D(m.Groups[6]),
                    int.Parse(m.Groups[7].ValueSpan, CultureInfo.InvariantCulture),
                    m.Groups[8].Value.Length > 0 ? m.Groups[8].Value : m.Groups[7].Value));
                continue;
            }

            if (!sawDetectionBlock && line.Contains("class_number", StringComparison.Ordinal) && !line.Contains("idx", StringComparison.Ordinal))
            {
                classificationHeader = true;
                continue;
            }

            if (classificationHeader && classNumber is null && (m = ClassificationRow().Match(line)).Success)
            {
                classNumber = int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                classLabel = m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value;
                continue;
            }

            if (tensorShape is null && (m = ShapeLine().Match(line)).Success)
                tensorShape = m.Groups[1].Value.Trim();
        }

        if (sawDetectionBlock)
            return new SimResult
            {
                Kind = SimResultKind.Detection,
                OriginalSize = originalSize,
                NetworkSize = networkSize,
                Detections = original,
                NetworkDetections = network,
            };
        if (classNumber is not null)
            return new SimResult { Kind = SimResultKind.Classification, ClassNumber = classNumber, ClassLabel = classLabel };
        if (tensorShape is not null)
            return new SimResult { Kind = SimResultKind.Tensor, TensorShape = tensorShape };
        return new SimResult();
    }

    /// <summary>Reads only the head of the file for tensor results, which can be very large.</summary>
    public static SimResult ParseFile(string path) => Parse(File.ReadLines(path, Encoding.UTF8).Take(5000));

    private static double D(Group g) => double.Parse(g.ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^Image resolution: (\d+)x(\d+) \((original|network)[^)]*\)")]
    private static partial Regex Resolution();

    //       0   126.1945   125.5361   573.7945   422.0061    57.9289        1         bicycle
    [GeneratedRegex(@"^\s*(\d+)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(\d+)\s*(.*?)\s*$")]
    private static partial Regex DetectionRow();

    //       249       malamute, malemute, Alaskan malamute
    [GeneratedRegex(@"^\s*(\d+)\s*(.*?)\s*$")]
    private static partial Regex ClassificationRow();

    [GeneratedRegex(@"^\s*shape\s*:\s*(.+)$")]
    private static partial Regex ShapeLine();
}
