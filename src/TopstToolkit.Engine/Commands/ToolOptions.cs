namespace TopstToolkit.Engine.Commands;

/// <summary>converter <c>--type</c>.</summary>
public enum NetworkType
{
    /// <summary>Object detection (<c>obj</c>): needs a post-process layer.</summary>
    Detection,
    /// <summary>Classification (<c>class</c>).</summary>
    Classification,
    /// <summary>Raw tensor output (<c>unknown</c>).</summary>
    Unknown,
}

/// <summary>
/// converter.py options (feature/03 §1). Paths are absolute. The model path and the output file name must be ASCII
/// (feature/09 §4 #4, #5) — the workspace guarantees that by staging inputs.
/// </summary>
public sealed class ConverterOptions
{
    public required string Model { get; init; }
    public required string Output { get; init; }
    public required NetworkType Type { get; init; }
    public required string CompatibilityLogRoot { get; init; }

    /// <summary>
    /// Always passed: converter checks that <c>--dataset-root</c> exists even without <c>--enable-track</c>,
    /// and its default (<c>downloads</c>) does not exist in a job folder.
    /// </summary>
    public required string DatasetRoot { get; init; }

    public double[]? Mean { get; init; }
    public double[]? Std { get; init; }
    public int? NumClass { get; init; }
    public string? ClassLabels { get; init; }

    /// <summary><c>--add-detection-post-process</c>: SSD <c>.anchor</c> or YOLO grid <c>.bin</c>.</summary>
    public string? PostProcessFile { get; init; }
    public string? YoloVersion { get; init; }
    public int? DflRegMax { get; init; }
    public string? OutputOrder { get; init; }
    public double[]? Variance { get; init; }
    public bool NoBackground { get; init; }
    public string? Logistic { get; init; }
    public bool OmitPostProcess { get; init; }
    public IReadOnlyList<string>? ForceOutput { get; init; }
    public bool EnableYoloOutputDecomposing { get; init; }
    public bool EnableLetterbox { get; init; }
    /// <summary>Darknet <c>.weights</c>.</summary>
    public string? Weight { get; init; }

    /// <summary>Calibration: collect activation statistics. Without it the network cannot be quantized.</summary>
    public bool EnableTrack { get; init; }
    public string Dataset { get; init; } = "Custom";
    public int? NumImages { get; init; }
    public int? BatchSize { get; init; }
}

/// <summary>quantizer.py options (feature/03 §2).</summary>
public sealed class QuantizerOptions
{
    public required string Model { get; init; }
    public required string Output { get; init; }
    /// <summary>Default 10 when omitted.</summary>
    public double? MStd8 { get; init; }
    /// <summary>Default 1.6 when omitted.</summary>
    public double? MStdRatio { get; init; }
    public bool WeightRangeAsymmetric { get; init; }
    public bool DisableClipMinMax { get; init; }
}

/// <summary>compiler.py options (feature/03 §3). Output goes to <c>&lt;DumpRoot&gt;\&lt;model name&gt;\</c>.</summary>
public sealed class CompilerOptions
{
    public required string Model { get; init; }
    public required string DumpRoot { get; init; }
    public double? ThConf { get; init; }
    public double? ThIou { get; init; }
    public int? MaxDetection { get; init; }
}

/// <summary>enlight_sim.py options (feature/03 §4). Results go to <c>&lt;ResultRoot&gt;\&lt;model name&gt;\&lt;image name&gt;\</c>.</summary>
public sealed class SimulatorOptions
{
    public required string Model { get; init; }
    /// <summary>An image file or a folder (searched recursively).</summary>
    public required string Inputs { get; init; }
    public required string ResultRoot { get; init; }
    public double? ThConf { get; init; }
    public double? ThIou { get; init; }
    public bool EnableLetterbox { get; init; }
    /// <summary>NPU input/output activation dump (<c>input.ia.bin</c> and friends). Needs <see cref="DumpRoot"/>.</summary>
    public bool Dump { get; init; }
    public string? DumpRoot { get; init; }
}

/// <summary>perf_simulator.py options (feature/03 §6). The output folder must exist beforehand.</summary>
public sealed class PerfOptions
{
    public required string NpuCmdHeader { get; init; }
    public required string NpuConfig { get; init; }
    public required string Output { get; init; }
    /// <summary>
    /// Where to keep the simulator's timeline: with a path the tool also runs with <c>--debug</c> (one CSV row per start of
    /// the DMA engine's and the two cores' ops, written beside <c>npu_cmd.h</c>) and the CSV is moved here. Null = none.
    /// </summary>
    public string? Timeline { get; init; }
}
