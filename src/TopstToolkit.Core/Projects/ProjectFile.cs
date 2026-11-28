using System.Text.Json.Serialization;

namespace TopstToolkit.Core.Projects;

/// <summary>
/// <c>project.json</c> (design §4.6): everything needed to rebuild a project. Paths inside the project folder are
/// stored relative with forward slashes; the display name may be Korean, the id and file names are ASCII.
/// </summary>
public sealed class ProjectFile
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ProjectVersions Versions { get; set; } = new();

    public ProjectModel? Model { get; set; }

    /// <summary><c>obj</c> (detection), <c>class</c> (classification) or <c>unknown</c>; empty until chosen.</summary>
    public string Task { get; set; } = "";

    /// <summary>Preset id (see Presets), for example <c>pytorch-yolo-v5</c>.</summary>
    public string? Preset { get; set; }

    public ProjectOptions Options { get; set; } = new();

    /// <summary>Last result of each pipeline step, keyed by step name (<c>convert</c>, <c>quantize</c>, ...).</summary>
    public Dictionary<string, StepRecord> Steps { get; set; } = new();

    /// <summary>Headline numbers for lists: filled when the matching step finishes.</summary>
    public ProjectHeadline Headline { get; set; } = new();

    /// <summary>Set when the model came from the model zoo (design §5.9): which one, and whose weights.</summary>
    public ZooOrigin? Zoo { get; set; }
}

/// <summary>A project made from the model zoo: the catalog entry and the weights it was exported with.</summary>
public sealed class ZooOrigin
{
    public string Id { get; set; } = "";
    /// <summary><c>torchvision</c>, <c>timm</c> or <c>ultralytics</c>.</summary>
    public string Library { get; set; } = "";
    public string Display { get; set; } = "";
    public string WeightsUrl { get; set; } = "";
    public string WeightsSha256 { get; set; } = "";
    /// <summary>The day of the review the catalog's numbers come from.</summary>
    public string Reviewed { get; set; } = "";
    /// <summary>The input size of the export (smaller than the trained size when the compiler needed it).</summary>
    public int Size { get; set; }
    public int NominalSize { get; set; }
}

public sealed class ProjectVersions
{
    public string App { get; set; } = "";
    public string Sdk { get; set; } = "";
}

public sealed class ProjectModel
{
    /// <summary>Where the user picked the file (may contain Korean or spaces); informational only.</summary>
    public string Source { get; set; } = "";
    /// <summary>The staged copy the toolkit reads, relative to the project folder.</summary>
    public string Staged { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary><c>onnx</c>, <c>tflite</c>, <c>darknet</c> or <c>enlight</c>.</summary>
    public string Format { get; set; } = "";
    /// <summary>Darknet <c>.weights</c>, relative.</summary>
    public string? Weights { get; set; }
    /// <summary>
    /// Set when the app made the staged model from the user's file: <c>yolo-v8</c> = an exported YOLOv8 model cut at
    /// its head (the staged file is <c>*_extracted.onnx</c>, the grid file was generated).
    /// </summary>
    public string? Prepared { get; set; }
    public ModelFacts? Facts { get; set; }

    /// <summary>
    /// The model as the user brought it in, relative. Set when the app cut it (then <see cref="Staged"/> is the cut
    /// model); kept so that the cut can be chosen again on the graph. Null when the staged file is the original.
    /// </summary>
    public string? Original { get; set; }
    /// <summary>What the check found when the model was brought in (design §5.7). Null for older projects.</summary>
    public ModelCheckRecord? Check { get; set; }
    /// <summary>Where the staged model was cut, if it was.</summary>
    public ModelCut? Cut { get; set; }

    /// <summary>The check found a tail the NPU cannot run and no cut has been chosen yet (design §5.7).</summary>
    [JsonIgnore]
    public bool NeedsCut => Check is { Outcome: "tail" } && Cut is null;

    /// <summary>The check found that no cut helps: this model cannot be built.</summary>
    [JsonIgnore]
    public bool IsBlocked => Check is { Outcome: "blocked" };

    /// <summary>Nothing about the model itself stands in the way of the next steps.</summary>
    [JsonIgnore]
    public bool IsReady => !NeedsCut && !IsBlocked;
}

/// <summary>
/// The check that runs when a model is brought in: which layers the NPU cannot run, and — for ONNX — what that means
/// on the graph of the model (design §5.7).
/// </summary>
public sealed class ModelCheckRecord
{
    public DateTimeOffset CheckedAt { get; set; }
    public double ElapsedSec { get; set; }
    /// <summary>
    /// <c>clean</c> (everything runs), <c>tail</c> (what cannot run is a tail that can be cut off), <c>blocked</c>
    /// (cutting does not help), <c>listed</c> (layers that cannot run are known, but not where they are: no graph for
    /// this format, or the names did not match) or <c>failed</c> (the check itself did not finish).
    /// </summary>
    public string Outcome { get; set; } = "";
    /// <summary>For <c>blocked</c>: <c>learned</c> (learned layers behind the cut) or <c>front</c> (nothing runnable in front).</summary>
    public string? BlockedReason { get; set; }
    public string? ErrorTitle { get; set; }
    public string? ErrorDetail { get; set; }
    /// <summary>Layers of the converted network, as the compatibility table counts them.</summary>
    public int TotalLayers { get; set; }
    public List<CheckedLayer> Layers { get; set; } = [];
    /// <summary>Operators of the model file (ONNX), 0 when there is no graph.</summary>
    public int Ops { get; set; }
    /// <summary>Operators that could run but sit behind one that cannot.</summary>
    public int BlockedOps { get; set; }
    public ProposedCut? Proposal { get; set; }
    /// <summary>The graph analysis (<c>graph_model.py</c>), relative. Made again when missing.</summary>
    public string? Graph { get; set; }
}

/// <summary>A layer the NPU cannot run.</summary>
public sealed class CheckedLayer
{
    public int Index { get; set; }
    /// <summary>ENLIGHT layer name, <c>&lt;Type&gt;_&lt;n&gt;___&lt;original name&gt;</c>.</summary>
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>The reason as the converter wrote it.</summary>
    public string Description { get; set; } = "";
    /// <summary>The operator of the model file this layer comes from, when it could be matched (ONNX).</summary>
    public string? Node { get; set; }
}

/// <summary>A cut the app suggests: the head of a known detector, or the last supported points.</summary>
public sealed class ProposedCut
{
    /// <summary><c>yolo-v8</c>, <c>ssd</c> or <c>frontier</c>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The board-side box maths that fits (<c>yolo-v8</c>, <c>ssd</c>), null when the toolkit has none.</summary>
    public string? Decoder { get; set; }
    public List<string> Nodes { get; set; } = [];
    public int KeptOps { get; set; }
    public int AwayOps { get; set; }
}

/// <summary>Where the staged model was cut: the result of each named operator is an output, everything behind is gone.</summary>
public sealed class ModelCut
{
    public List<string> Nodes { get; set; } = [];
    /// <summary><c>app</c> (the suggestion, applied when the model came in) or <c>user</c> (chosen on the graph).</summary>
    public string By { get; set; } = "app";
    public string? Decoder { get; set; }
    public int KeptOps { get; set; }
    public int AwayOps { get; set; }
}

/// <summary>What model inspection found; shown on the model step and used to guess a preset.</summary>
public sealed class ModelFacts
{
    public List<TensorFact> Inputs { get; set; } = [];
    public List<TensorFact> Outputs { get; set; } = [];
    public int NodeCount { get; set; }
    public long ParameterCount { get; set; }
    public long? Opset { get; set; }
    public Dictionary<string, int> Operators { get; set; } = [];
}

public sealed class TensorFact
{
    public string Name { get; set; } = "";
    /// <summary>Dimensions as text: numbers or symbolic names (<c>batch_size</c>).</summary>
    public List<string> Shape { get; set; } = [];
}

public sealed class ProjectOptions
{
    public ConvertSettings Convert { get; set; } = new();
    public LabelSettings Labels { get; set; } = new();
    public CalibrationSettings Calibration { get; set; } = new();
    public QuantizeSettings Quantize { get; set; } = new();
    public CompileSettings Compile { get; set; } = new();
    public BoardSettings Board { get; set; } = new();
    public PerfSettings Perf { get; set; } = new();
}

public sealed class BoardSettings
{
    /// <summary>
    /// Build <c>net.so</c> with the app's fast post-processing when one fits the network (design §5.8). The toolkit's
    /// own code stays in the library either way; false builds the official library unchanged.
    /// </summary>
    public bool FastPostProcess { get; set; } = true;
}

public sealed class ConvertSettings
{
    public double[] Mean { get; set; } = [0, 0, 0];
    public double[] Std { get; set; } = [1, 1, 1];
    public int? NumClass { get; set; }
    public string? YoloVersion { get; set; }
    public int? DflRegMax { get; set; }
    public string? OutputOrder { get; set; }
    public double[]? Variance { get; set; }
    public string? Logistic { get; set; }
    public bool NoBackground { get; set; }
    public bool Letterbox { get; set; }
    /// <summary>SSD <c>.anchor</c> or YOLO grid <c>.bin</c>, relative.</summary>
    public string? PostProcess { get; set; }
    /// <summary>Layers to cut the network at (two-step conversion, design §5.4).</summary>
    public List<string> ForceOutput { get; set; } = [];
}

public sealed class LabelSettings
{
    /// <summary>ASCII label file given to the toolkit, relative.</summary>
    public string? Toolkit { get; set; }
    /// <summary>Label set id: <c>coco80</c>, <c>voc20</c>, <c>imagenet</c>, <c>custom</c>.</summary>
    public string? Set { get; set; }
    /// <summary>Display names (may be Korean), one per class; drawn by the app, never sent to the toolkit.</summary>
    public string? Display { get; set; }
}

public sealed class CalibrationSettings
{
    /// <summary><c>samples</c> (bundled 100 images), <c>folder</c> or <c>npy</c>.</summary>
    public string Source { get; set; } = "samples";
    public string? SourcePath { get; set; }
    public int Count { get; set; } = 100;
    public string? ListSha256 { get; set; }
}

public sealed class QuantizeSettings
{
    public double? MStd8 { get; set; }
    public double? MStdRatio { get; set; }
}

public sealed class CompileSettings
{
    public double ThConf { get; set; } = 0.5;
    public double ThIou { get; set; } = 0.5;
    public int? MaxDetection { get; set; }
}

public sealed class PerfSettings
{
    /// <summary>The clocks of the estimate (<see cref="Pipeline.PerfClocks"/>): the TOPST AI-G board's since 2026-11-28.</summary>
    public string ClockPreset { get; set; } = Pipeline.PerfClocks.BoardPreset;
}

public sealed class StepRecord
{
    /// <summary>Hash of everything the step depends on (design §4.5). A different key means "needs to run again".</summary>
    public string Key { get; set; } = "";
    /// <summary><c>done</c>, <c>failed</c> or <c>cancelled</c>.</summary>
    public string Status { get; set; } = "";
    public double ElapsedSec { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<string> Outputs { get; set; } = [];
    /// <summary>Short facts for the step card (for example <c>compatible = 273</c>).</summary>
    public Dictionary<string, string> Facts { get; set; } = [];
    public string? ErrorCode { get; set; }
    public string? ErrorTitle { get; set; }
}

public sealed class ProjectHeadline
{
    public double? InferenceTimeMs { get; set; }
    public int? CompatibleLayers { get; set; }
    public int? TotalLayers { get; set; }
    public long? WeightBytes { get; set; }
    public DateTimeOffset? BuiltAt { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }
    /// <summary>Settings changed after the last build: some steps have to run again.</summary>
    public bool NeedsRebuild { get; set; }

    [JsonIgnore]
    public bool IsBuilt => BuiltAt is not null;
}
