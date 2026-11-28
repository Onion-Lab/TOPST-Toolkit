using System.Globalization;

namespace TopstToolkit.Engine.Commands;

/// <summary>A tool run, ready for <see cref="Runner.ToolRunner"/>: the script and its argument list (no quoting needed).</summary>
public sealed record ToolInvocation(ToolKind Tool, string ScriptPath, IReadOnlyList<string> Arguments, string Title)
{
    /// <summary>For <see cref="ToolKind.Helper"/> runs: the helper's name.</summary>
    public string? HelperName { get; init; }

    public override string ToString() => $"{Path.GetFileName(ScriptPath)} {string.Join(' ', Arguments.Select(Quote))}";

    private static string Quote(string a) => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a;
}

/// <summary>
/// Settings → argument lists. Always adds <c>--model-config-root &lt;empty folder&gt;</c> (feature/09 §4 #3)
/// and absolute output locations; numbers are written with the invariant culture.
/// </summary>
public sealed class CommandBuilder
{
    private readonly EngineLayout _layout;
    private readonly string _emptyModelConfigRoot;

    public CommandBuilder(EngineLayout layout, string emptyModelConfigRoot)
    {
        _layout = layout;
        _emptyModelConfigRoot = RequireAbsolute(emptyModelConfigRoot, "model-config root");
    }

    public ToolInvocation Converter(ConverterOptions o)
    {
        RequireAscii(RequireAbsolute(o.Model, "model"), "converter model path");
        RequireAsciiFileName(RequireAbsolute(o.Output, "output"), ".enlight");
        RequireAbsolute(o.CompatibilityLogRoot, "compatibility log root");
        RequireAbsolute(o.DatasetRoot, "dataset root");
        if (o.PostProcessFile is not null && o.NumClass is null)
            throw new ArgumentException("--add-detection-post-process needs --num-class.");

        var a = new List<string> { o.Model, "--output", o.Output, "--type", TypeName(o.Type) };
        AddTriple(a, "--mean", o.Mean);
        AddTriple(a, "--std", o.Std);
        if (o.NumClass is { } numClass) a.AddRange(["--num-class", I(numClass)]);
        if (o.ClassLabels is not null) a.AddRange(["--class-labels", RequireAbsolute(o.ClassLabels, "class labels")]);
        if (o.PostProcessFile is not null) a.AddRange(["--add-detection-post-process", RequireAbsolute(o.PostProcessFile, "post-process file")]);
        if (o.YoloVersion is not null) a.AddRange(["--yolo-version", o.YoloVersion]);
        if (o.DflRegMax is { } dfl) a.AddRange(["--dfl-reg-max", I(dfl)]);
        if (o.OutputOrder is not null) a.AddRange(["--output-order", o.OutputOrder]);
        if (o.Variance is not null)
        {
            if (o.Variance.Length != 2) throw new ArgumentException("--variance takes two values.");
            a.Add("--variance");
            a.AddRange(o.Variance.Select(F));
        }
        if (o.NoBackground) a.Add("--no-background");
        if (o.Logistic is not null) a.AddRange(["--logistic", o.Logistic]);
        if (o.OmitPostProcess) a.Add("--omit-post-process");
        if (o.ForceOutput is { Count: > 0 })
        {
            a.Add("--force-output");
            a.AddRange(o.ForceOutput);
        }
        if (o.EnableYoloOutputDecomposing) a.Add("--enable-yolo-output-decomposing");
        if (o.EnableLetterbox) a.Add("--enable-letterbox");
        if (o.Weight is not null) a.AddRange(["--weight", RequireAbsolute(o.Weight, "weight")]);

        a.AddRange(["--dataset", o.Dataset, "--dataset-root", o.DatasetRoot]);
        if (o.EnableTrack)
        {
            a.Add("--enable-track");
            if (o.NumImages is { } n) a.AddRange(["--num-images", I(n)]);
            if (o.BatchSize is { } b) a.AddRange(["--batch-size", I(b)]);
        }
        a.AddRange(["--compatibility-log-root", o.CompatibilityLogRoot]);
        AddCommon(a);
        return new ToolInvocation(ToolKind.Converter, _layout.ScriptFor(ToolKind.Converter), a, "변환");
    }

    public ToolInvocation Quantizer(QuantizerOptions o)
    {
        RequireAbsolute(o.Model, "model");
        RequireAsciiFileName(RequireAbsolute(o.Output, "output"), ".enlight");
        var a = new List<string> { o.Model, "--output", o.Output };
        if (o.MStd8 is { } m8) a.AddRange(["--m-std-8", F(m8)]);
        if (o.MStdRatio is { } r) a.AddRange(["--m-std-ratio", F(r)]);
        if (o.WeightRangeAsymmetric) a.Add("--weight-range-asymmetric");
        if (o.DisableClipMinMax) a.Add("--disable-clip-min-max");
        AddCommon(a);
        return new ToolInvocation(ToolKind.Quantizer, _layout.ScriptFor(ToolKind.Quantizer), a, "양자화");
    }

    public ToolInvocation Compiler(CompilerOptions o)
    {
        RequireAsciiFileName(RequireAbsolute(o.Model, "model"), ".enlight");
        var a = new List<string> { o.Model, "--dump-root", RequireAbsolute(o.DumpRoot, "dump root") };
        if (o.ThConf is { } c) a.AddRange(["--th-conf", F(c)]);
        if (o.ThIou is { } i) a.AddRange(["--th-iou", F(i)]);
        if (o.MaxDetection is { } m) a.AddRange(["--max-detection", I(m)]);
        AddCommon(a);
        return new ToolInvocation(ToolKind.Compiler, _layout.ScriptFor(ToolKind.Compiler), a, "컴파일");
    }

    public ToolInvocation Simulator(SimulatorOptions o)
    {
        RequireAbsolute(o.Model, "model");
        var a = new List<string>
        {
            o.Model,
            "--inputs", RequireAbsolute(o.Inputs, "inputs"),
            "--result-root", RequireAbsolute(o.ResultRoot, "result root"),
        };
        if (o.ThConf is { } c) a.AddRange(["--th-conf", F(c)]);
        if (o.ThIou is { } i) a.AddRange(["--th-iou", F(i)]);
        if (o.EnableLetterbox) a.Add("--enable-letterbox");
        if (o.Dump)
        {
            a.Add("--dump");
            a.AddRange(["--dump-root", RequireAbsolute(o.DumpRoot ?? throw new ArgumentException("--dump needs a dump root."), "dump root")]);
        }
        else if (o.DumpRoot is not null)
        {
            // keeps the default ./output_dump out of the job folder even without --dump
            a.AddRange(["--dump-root", RequireAbsolute(o.DumpRoot, "dump root")]);
        }
        AddCommon(a);
        return new ToolInvocation(ToolKind.Simulator, _layout.ScriptFor(ToolKind.Simulator), a, "PC 추론");
    }

    public ToolInvocation Perf(PerfOptions o)
    {
        var output = RequireAbsolute(o.Output, "output");
        if (!output.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("perf output must end with .json (the tool appends it otherwise).");
        var a = new List<string>
        {
            RequireAbsolute(o.NpuCmdHeader, "npu_cmd.h"),
            "--npu-config", RequireAbsolute(o.NpuConfig, "npu config"),
            "--output", output,
        };
        // its developer switch: the per-op timeline the board time estimate reads (board_time.py, design §11 2026-11-28)
        if (o.Timeline is not null)
            a.Add("--debug");
        // perf_simulator has no --model-config option.
        return new ToolInvocation(ToolKind.PerfSimulator, _layout.ScriptFor(ToolKind.PerfSimulator), a, "성능 추정");
    }

    public ToolInvocation Helper(string name, string title, params string[] arguments) =>
        new(ToolKind.Helper, _layout.HelperScript(name), arguments, title) { HelperName = name };

    private void AddCommon(List<string> a) => a.AddRange(["--model-config-root", _emptyModelConfigRoot]);

    public static string TypeName(NetworkType type) => type switch
    {
        NetworkType.Detection => "obj",
        NetworkType.Classification => "class",
        _ => "unknown",
    };

    private static void AddTriple(List<string> a, string option, double[]? values)
    {
        if (values is null) return;
        if (values.Length != 3) throw new ArgumentException($"{option} takes three values.");
        a.Add(option);
        a.AddRange(values.Select(F));
    }

    public static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string RequireAbsolute(string path, string what)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"The {what} path must be absolute: {path}");
        return path;
    }

    private static void RequireAscii(string value, string what)
    {
        if (!IsAscii(value))
            throw new ArgumentException($"The {what} must be ASCII (the toolkit stores it in the .enlight file): {value}");
    }

    private static void RequireAsciiFileName(string path, string extension)
    {
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Expected a {extension} file: {path}");
        RequireAscii(Path.GetFileName(path), "network file name");
    }

    public static bool IsAscii(string value) => value.All(c => c < 128);
}
