using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TopstToolkit.Engine.Parsing;

/// <summary>A layer the NPU cannot run, from the compatibility table (feature/06 §3).</summary>
/// <param name="Index">Layer index in the converted network.</param>
/// <param name="Name">ENLIGHT layer name <c>&lt;Type&gt;_&lt;n&gt;___&lt;original name&gt;</c>; usable with <c>--force-output</c>.</param>
public sealed record IncompatibleLayer(int Index, string Name, string Type, string Description);

/// <summary>
/// Converter compatibility result, read from <c>&lt;net&gt;_compatibility.log</c> or the same table on stdout.
/// The detail table lists only the failing layers; a fully compatible model has the summary only.
/// </summary>
public sealed partial record CompatibilityReport(int Compatible, int Incompatible, int Total, IReadOnlyList<IncompatibleLayer> Layers)
{
    public bool IsFullyCompatible => Incompatible == 0;

    /// <summary>Null when no summary table is present (for example the cp949 logging failure, feature/09 §4 #2).</summary>
    public static CompatibilityReport? Parse(IEnumerable<string> lines)
    {
        int? compatible = null, incompatible = null, total = null;
        var layers = new List<IncompatibleLayer>();
        IncompatibleLayer? open = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            Match m;
            if ((m = SummaryRow().Match(line)).Success)
            {
                var value = int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
                switch (m.Groups[1].Value)
                {
                    case "Number of compatible layers": compatible = value; break;
                    case "Number of incompatible layers": incompatible = value; break;
                    default: total = value; break;
                }
                continue;
            }

            if ((m = LayerRow().Match(line)).Success)
            {
                var pass = m.Groups[4].Value;
                if (m.Groups[1].Value.Length > 0)
                {
                    open = null;
                    if (!string.Equals(pass, "X", StringComparison.OrdinalIgnoreCase))
                        continue; // header row ("Pass") or a passing layer
                    open = new IncompatibleLayer(
                        int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                        m.Groups[2].Value, m.Groups[3].Value, m.Groups[5].Value);
                    layers.Add(open);
                }
                else if (open is not null)
                {
                    // tabulate wraps long cells onto continuation rows with an empty index cell
                    var merged = open with
                    {
                        Name = Join(open.Name, m.Groups[2].Value, ""),
                        Type = Join(open.Type, m.Groups[3].Value, ""),
                        Description = Join(open.Description, m.Groups[5].Value, " "),
                    };
                    layers[^1] = merged;
                    open = merged;
                }
                continue;
            }

            if (line.Length > 0 && line[0] is not ('│' or '├' or '╞'))
                open = null;
        }

        if (total is null && compatible is null && incompatible is null)
            return null;

        var inc = incompatible ?? layers.Count;
        var tot = total ?? (compatible ?? 0) + inc;
        return new CompatibilityReport(compatible ?? tot - inc, inc, tot, layers);
    }

    public static CompatibilityReport? ParseFile(string path) =>
        File.Exists(path) ? Parse(File.ReadLines(path, Encoding.UTF8)) : null;

    private static string Join(string a, string b, string separator) =>
        b.Length == 0 ? a : a.Length == 0 ? b : a + separator + b;

    [GeneratedRegex(@"^│\s*(Number of compatible layers|Number of incompatible layers|Total number of layers)\s*│\s*(\d+)\s*│$")]
    private static partial Regex SummaryRow();

    // │ 131 │ Softmax_0___Softmax │ Softmax │   X    │ Softmax not supported in ENLIGHT NPU │
    [GeneratedRegex(@"^│\s*(\d*)\s*│\s*(.*?)\s*│\s*(.*?)\s*│\s*(.*?)\s*│\s*(.*?)\s*│$")]
    private static partial Regex LayerRow();
}
