using System.Globalization;
using System.Text.RegularExpressions;
using TopstToolkit.Engine.Runner;

namespace TopstToolkit.Engine.Parsing;

/// <summary>
/// Turns the cleaned console tokens of one tool run into events. Patterns come from the real Windows logs
/// (feature/04 §5, win-verify\logs). One instance per run; not thread-safe.
/// </summary>
public sealed partial class ToolOutputParser
{
    private readonly ToolKind _tool;
    private readonly string? _workingDirectory;
    private int? _compatible;
    private int? _incompatible;

    public ToolOutputParser(ToolKind tool, string? workingDirectory = null)
    {
        _tool = tool;
        _workingDirectory = workingDirectory;
    }

    /// <summary>The SDK version from the converter banner (<c>[v0.9.9]</c>), when seen.</summary>
    public string? SdkVersion { get; private set; }

    public static string? DoneMarkerFor(ToolKind tool) => tool switch
    {
        ToolKind.Converter => "Converter. done.",
        ToolKind.Quantizer => "Quantizer done.",
        ToolKind.Compiler => "Compiler. done.",
        ToolKind.Simulator => "Inference. done.",
        ToolKind.Evaluator => "Evaluator done.",
        ToolKind.PerfSimulator => "Performance Simulator. done.",
        ToolKind.Helper => "TOPST helper. done.",
        _ => null,
    };

    public void Parse(StreamKind stream, OutputToken token, List<ToolEvent> sink)
    {
        sink.Add(new OutputLineEvent(stream, token.Text, token.Kind == TokenKind.Update));

        // stderr carries warnings and errors only; the error classifier reads it from the run result.
        if (stream == StreamKind.Stderr)
            return;

        var text = token.Text.Trim();
        if (text.Length == 0)
            return;

        if (TryProgress(text, sink))
            return;

        if (token.Kind == TokenKind.Line)
            ParseLine(token.Text, text, sink);
    }

    private bool TryProgress(string text, List<ToolEvent> sink)
    {
        Match m;
        if ((m = Optimizing().Match(text)).Success)
            sink.Add(Progress(ToolStage.Optimize, m));
        else if ((m = Batch().Match(text)).Success)
            sink.Add(Progress(_tool switch
            {
                ToolKind.Simulator => ToolStage.Inference,
                ToolKind.Evaluator => ToolStage.Evaluate,
                _ => ToolStage.Calibration,
            }, m));
        else if ((m = Serializing().Match(text)).Success)
            sink.Add(Progress(ToolStage.Serialize, m));
        else if ((m = CodeEmitting().Match(text)).Success)
            sink.Add(Progress(ToolStage.CodeEmit, m));
        else if ((m = WritingWeights().Match(text)).Success)
            sink.Add(Progress(ToolStage.WriteWeights, m));
        else if ((m = ParsingLayer().Match(text)).Success)
            sink.Add(new ProgressEvent(ToolStage.LoadModel, long.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture) + 1, 0));
        else
            return false;
        return true;
    }

    private static ProgressEvent Progress(ToolStage stage, Match m) =>
        new(stage, long.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture), long.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture));

    private void ParseLine(string raw, string text, List<ToolEvent> sink)
    {
        Match m;

        // --- banners and completion markers
        if ((m = ConverterBanner().Match(text)).Success)
        {
            SdkVersion = m.Groups[1].Value;
            sink.Add(new MarkerEvent(ToolMarker.Started, text));
            return;
        }
        if (StartBanners.Contains(text))
        {
            sink.Add(new MarkerEvent(ToolMarker.Started, text));
            return;
        }
        if (text == "NPU compiler: done sucessfully.")
        {
            sink.Add(new MarkerEvent(ToolMarker.CompilerSucceeded, text));
            return;
        }
        if (text == DoneMarkerFor(_tool))
        {
            sink.Add(new MarkerEvent(ToolMarker.Done, text));
            return;
        }

        // --- stages
        if ((m = OptimizerStart().Match(text)).Success)
        {
            sink.Add(new StageEvent(ToolStage.Optimize, m.Groups[1].Value));
            return;
        }
        if ((m = CodeEmitStart().Match(text)).Success)
        {
            sink.Add(new StageEvent(ToolStage.CodeEmit, m.Groups[1].Value));
            return;
        }
        if (text == "Checking compatibility with ENLIGHT NPU")
        {
            sink.Add(new StageEvent(ToolStage.Compatibility));
            return;
        }
        if (text == "Initializing Network done.")
        {
            sink.Add(new StageEvent(ToolStage.LoadModel, "done"));
            return;
        }

        // --- compatibility summary table (stdout copy of the log's table)
        if ((m = CompatRow().Match(text)).Success)
        {
            var value = int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
            switch (m.Groups[1].Value)
            {
                case "Number of compatible layers":
                    _compatible = value;
                    break;
                case "Number of incompatible layers":
                    _incompatible = value;
                    break;
                case "Total number of layers":
                    sink.Add(new CompatibilitySummaryEvent(_compatible ?? value - (_incompatible ?? 0), _incompatible ?? 0, value));
                    break;
            }
            return;
        }

        // --- paths the tools report
        if ((m = Created().Match(text)).Success && LooksLikePath(m.Groups[1].Value))
        {
            sink.Add(new FileCreatedEvent(NormalizePath(m.Groups[1].Value)));
            return;
        }
        if ((m = SaveResults().Match(text)).Success)
        {
            var kind = m.Groups[1].Value switch
            {
                "result" => ReportedPathKind.ResultDir,
                "dump" => ReportedPathKind.DumpDir,
                "text" => ReportedPathKind.ResultText,
                "image" => ReportedPathKind.ResultImage,
                _ => ReportedPathKind.ResultNpy,
            };
            sink.Add(new PathReportedEvent(kind, NormalizePath(m.Groups[2].Value)));
            return;
        }
        if ((m = PerfWritten().Match(text)).Success)
        {
            sink.Add(new PathReportedEvent(ReportedPathKind.PerfJson, NormalizePath(m.Groups[1].Value)));
            return;
        }
        if ((m = CompatWritten().Match(text)).Success)
        {
            sink.Add(new PathReportedEvent(ReportedPathKind.CompatibilityLog, NormalizePath(m.Groups[1].Value)));
            return;
        }
        if ((m = EnlightWritten().Match(text)).Success)
        {
            sink.Add(new PathReportedEvent(ReportedPathKind.EnlightWritten, NormalizePath(m.Groups[1].Value)));
            return;
        }

        // --- summaries
        if ((m = WorkBuffer().Match(text)).Success)
        {
            sink.Add(new WorkBufferEvent(double.Parse(m.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture)));
            return;
        }
        if ((m = PerfTime().Match(text)).Success)
        {
            sink.Add(new PerfTimeEvent(double.Parse(m.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture)));
            return;
        }
        if ((m = ModelConfigNotFound().Match(text)).Success)
        {
            sink.Add(new ModelConfigNoticeEvent(m.Groups[1].Value));
            return;
        }

        // --- argument echo: name padded to 40 columns, then ": value" (raw line, the name starts at column 0)
        if ((m = ArgumentEcho().Match(raw)).Success)
        {
            var name = m.Groups[1].Value;
            var value = m.Groups[2].Value.TrimEnd();
            sink.Add(new ArgumentEchoEvent(name, value));
            if (name == "output_dir" && _tool == ToolKind.Compiler)
                sink.Add(new PathReportedEvent(ReportedPathKind.CompilerOutputDir, NormalizePath(value)));
        }
    }

    private static bool LooksLikePath(string value) => value.Length > 2 && (Path.IsPathRooted(value) || value.Contains('/') || value.Contains('\\'));

    /// <summary>The tools print mixed separators (<c>E:/out/code\net\npu_cmd.h</c>); returns a Windows full path when possible.</summary>
    public string NormalizePath(string value)
    {
        var p = value.Trim().Replace('/', Path.DirectorySeparatorChar);
        try
        {
            if (Path.IsPathRooted(p))
                return Path.GetFullPath(p);
            return _workingDirectory is null ? p : Path.GetFullPath(Path.Combine(_workingDirectory, p));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return p;
        }
    }

    private static readonly HashSet<string> StartBanners = new(StringComparer.Ordinal)
    {
        "Quantization. start",
        "Compiling network. start.",
        "Inference image. start.",
        "Evaluate network. start.",
        "Performance Simulator. start.",
        "Generate detection network grid info . start.",
        "TOPST helper. start.",
    };

    [GeneratedRegex(@"^optimizing (\d+) / (\d+)$")]
    private static partial Regex Optimizing();

    [GeneratedRegex(@"^\[(\d+) / (\d+)\]$")]
    private static partial Regex Batch();

    [GeneratedRegex(@"^serializing (\d+) / (\d+)$")]
    private static partial Regex Serializing();

    [GeneratedRegex(@"^code-emitting (\d+) / (\d+)$")]
    private static partial Regex CodeEmitting();

    [GeneratedRegex(@"^Writing \[(\d+)\]/\[(\d+)\]$")]
    private static partial Regex WritingWeights();

    [GeneratedRegex(@"^parsing (\d+)th layer$")]
    private static partial Regex ParsingLayer();

    [GeneratedRegex(@"^\[v([0-9][0-9.]*)\] Converting to enlight format\. start\.$")]
    private static partial Regex ConverterBanner();

    [GeneratedRegex(@"^Start (\w+) optimizer$")]
    private static partial Regex OptimizerStart();

    [GeneratedRegex(@"^Start (\w+)\(version=\w+\) code-emitting$")]
    private static partial Regex CodeEmitStart();

    [GeneratedRegex(@"^│\s*(Number of compatible layers|Number of incompatible layers|Total number of layers)\s*│\s*(\d+)\s*│$")]
    private static partial Regex CompatRow();

    [GeneratedRegex(@"^(.+) created$")]
    private static partial Regex Created();

    [GeneratedRegex(@"^Save results \((result|dump|text|image|npy)\)(?: path ?:)? (.+)$")]
    private static partial Regex SaveResults();

    [GeneratedRegex(@"^Writing result to \.\.\. (.+)$")]
    private static partial Regex PerfWritten();

    [GeneratedRegex(@"^Writing compatibility result to \.\.\. (.+)$")]
    private static partial Regex CompatWritten();

    [GeneratedRegex(@"^Writing File path :\s*(.+)$")]
    private static partial Regex EnlightWritten();

    [GeneratedRegex(@"work-buffer size : ([0-9.eE+-]+) \(MiB\)")]
    private static partial Regex WorkBuffer();

    [GeneratedRegex(@"^\*\s*Inference Time\(msec\)\s*\|\s*([0-9.eE+-]+)\s*\*$")]
    private static partial Regex PerfTime();

    [GeneratedRegex(@"^\[INFO\] Find model_config (.+?), but can't find it$")]
    private static partial Regex ModelConfigNotFound();

    [GeneratedRegex(@"^([a-z][a-z0-9_]*) +: ?(.*)$")]
    private static partial Regex ArgumentEcho();
}
