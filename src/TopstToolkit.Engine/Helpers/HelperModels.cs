using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopstToolkit.Engine.Helpers;

/// <summary>
/// Output of <c>engine\helpers\board_time.py</c> (design §11, 2026-11-28): the estimated NPU time on the TOPST AI-G board,
/// the simulator's value plus what the board was measured to do differently. Kept next to the simulator's JSON.
/// </summary>
public sealed class BoardTimeEstimate
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    /// <summary>perf_simulator's value with the board's clocks.</summary>
    [JsonPropertyName("simulatorMs")] public double SimulatorMs { get; init; }
    /// <summary>The estimate: what the screens show.</summary>
    [JsonPropertyName("estimateMs")] public double EstimateMs { get; init; }
    [JsonPropertyName("terms")] public BoardTimeTerms? Terms { get; init; }
    /// <summary>False when the timeline or the model could not be read and that part was left out.</summary>
    [JsonPropertyName("complete")] public bool Complete { get; init; }
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];

    public double CorrectionMs => EstimateMs - SimulatorMs;

    public static BoardTimeEstimate? ParseFile(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<BoardTimeEstimate>(File.ReadAllText(path));
            return value is { Ok: true, EstimateMs: > 0 } ? value : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>The parts of <see cref="BoardTimeEstimate.CorrectionMs"/>, in ms.</summary>
public sealed class BoardTimeTerms
{
    /// <summary>Every run: starting the NPU and waiting for it.</summary>
    [JsonPropertyName("fixedMs")] public double FixedMs { get; init; }
    /// <summary>Average pooling, which the simulator gives no time.</summary>
    [JsonPropertyName("avgPoolMs")] public double AvgPoolMs { get; init; }
    /// <summary>Stride-2 max pooling, faster on the board than the simulator thinks (negative).</summary>
    [JsonPropertyName("maxPoolMs")] public double MaxPoolMs { get; init; }
    /// <summary>A first convolution with a kernel larger than 3x3.</summary>
    [JsonPropertyName("bigStemMs")] public double BigStemMs { get; init; }
    /// <summary>Depthwise convolutions, slower on the board than the simulator thinks.</summary>
    [JsonPropertyName("depthwiseMs")] public double DepthwiseMs { get; init; }
}

/// <summary>Output of <c>engine\helpers\engine_check.py</c>.</summary>
public sealed class EngineCheckReport
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("python")] public string? Python { get; init; }
    [JsonPropertyName("sdkVersion")] public string? SdkVersion { get; init; }
    [JsonPropertyName("enlightFile")] public string? EnlightFile { get; init; }
    [JsonPropertyName("packages")] public Dictionary<string, string>? Packages { get; init; }
    [JsonPropertyName("license")] public Dictionary<string, JsonElement>? License { get; init; }
    [JsonPropertyName("cudaAvailable")] public bool? CudaAvailable { get; init; }
    [JsonPropertyName("importSeconds")] public double? ImportSeconds { get; init; }
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];
}

/// <summary>Output of <c>engine\helpers\read_enlight.py</c>: the network's metadata (feature/03 §8).</summary>
public sealed class EnlightInfo
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("isQuantized")] public bool IsQuantized { get; init; }
    [JsonPropertyName("isTracked")] public bool IsTracked { get; init; }
    [JsonPropertyName("hasPostProcess")] public bool? HasPostProcess { get; init; }
    [JsonPropertyName("inputSize")] public List<int>? InputSize { get; init; }
    [JsonPropertyName("sourceModel")] public string? SourceModel { get; init; }
    [JsonPropertyName("info")] public Dictionary<string, JsonElement> Info { get; init; } = [];
    [JsonPropertyName("layers")] public List<EnlightLayer> Layers { get; init; } = [];
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];

    public string? InfoString(string key) =>
        Info.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public int? NumClass =>
        Info.TryGetValue("num_class", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

public sealed class EnlightLayer
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
}

/// <summary>Output of <c>engine\helpers\inspect_model.py</c>: an ONNX model's interface, read with <c>onnx</c> (no SDK).</summary>
public sealed class ModelInspection
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("irVersion")] public int? IrVersion { get; init; }
    [JsonPropertyName("producer")] public string? Producer { get; init; }
    [JsonPropertyName("opsets")] public List<OnnxOpset> Opsets { get; init; } = [];
    [JsonPropertyName("inputs")] public List<OnnxTensor> Inputs { get; init; } = [];
    [JsonPropertyName("outputs")] public List<OnnxTensor> Outputs { get; init; } = [];
    [JsonPropertyName("operators")] public Dictionary<string, int> Operators { get; init; } = [];
    [JsonPropertyName("nodeCount")] public int NodeCount { get; init; }
    [JsonPropertyName("parameterCount")] public long ParameterCount { get; init; }
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];
}

public sealed class OnnxOpset
{
    [JsonPropertyName("domain")] public string Domain { get; init; } = "";
    [JsonPropertyName("version")] public long Version { get; init; }
}

/// <summary>A graph input or output. Dimensions are numbers, symbolic names (<c>batch_size</c>) or null.</summary>
public sealed class OnnxTensor
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("shape")] public List<JsonElement> Shape { get; init; } = [];
    [JsonPropertyName("dtype")] public string? DataType { get; init; }

    public string ShapeText => "[" + string.Join(", ", Shape.Select(d => d.ValueKind switch
    {
        JsonValueKind.Number => d.GetRawText(),
        JsonValueKind.String => d.GetString(),
        _ => "?",
    })) + "]";
}

/// <summary>
/// Output of <c>engine\helpers\prepare_yolo.py</c>: an exported YOLOv8 model cut at its head convolutions plus the
/// grid file the toolkit's box maths needs (the toolkit's "사전 준비" for PyTorch YOLO, feature/02 recipe E).
/// </summary>
public sealed class YoloPreparation
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    /// <summary><c>v8</c> when prepared.</summary>
    [JsonPropertyName("family")] public string? Family { get; init; }
    [JsonPropertyName("extracted")] public string? Extracted { get; init; }
    [JsonPropertyName("grid")] public string? Grid { get; init; }
    /// <summary>Grid height and width per scale, largest first.</summary>
    [JsonPropertyName("grids")] public List<List<int>> Grids { get; init; } = [];
    [JsonPropertyName("gridShape")] public List<int> GridShape { get; init; } = [];
    [JsonPropertyName("outputs")] public List<OnnxTensor> Outputs { get; init; } = [];
    [JsonPropertyName("numClass")] public int? NumClass { get; init; }
    /// <summary>Box distribution bins per side (converter <c>--dfl-reg-max</c>).</summary>
    [JsonPropertyName("regMax")] public int? RegMax { get; init; }
    [JsonPropertyName("outputOrder")] public string? OutputOrder { get; init; }
    [JsonPropertyName("opset")] public long? Opset { get; init; }
    /// <summary>How many nodes of the box maths were cut off.</summary>
    [JsonPropertyName("removedNodes")] public int RemovedNodes { get; init; }
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];
}

/// <summary>
/// Output of <c>engine\helpers\graph_model.py</c>: what the NPU cannot run in an ONNX model, what is stuck behind it,
/// where the model can be cut and the part of the graph worth drawing (design §5.7).
/// </summary>
public sealed class ModelGraphReport
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    /// <summary>Operators of the model file (constants not counted).</summary>
    [JsonPropertyName("ops")] public int Ops { get; init; }
    [JsonPropertyName("weightValues")] public long WeightValues { get; init; }
    /// <summary><c>clean</c>, <c>tail</c> (cut at the frontier and everything bad is gone) or <c>blocked</c>.</summary>
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    /// <summary>Why blocked: <c>learned</c> (learned layers behind the cut) or <c>front</c> (nothing runnable in front).</summary>
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("bad")] public List<GraphBadOperator> Bad { get; init; } = [];
    /// <summary>Incompatible layers that could not be matched to an operator of the file.</summary>
    [JsonPropertyName("unmapped")] public List<string> Unmapped { get; init; } = [];
    [JsonPropertyName("blockedOps")] public int BlockedOps { get; init; }
    /// <summary>The last supported points, by operator name.</summary>
    [JsonPropertyName("frontier")] public List<string> Frontier { get; init; } = [];
    [JsonPropertyName("frontierAway")] public GraphAway? FrontierAway { get; init; }
    [JsonPropertyName("proposal")] public GraphProposal? Proposal { get; init; }
    [JsonPropertyName("graph")] public GraphDrawing Graph { get; init; } = new();
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];
}

public sealed class GraphBadOperator
{
    [JsonPropertyName("node")] public string Node { get; init; } = "";
    [JsonPropertyName("op")] public string Op { get; init; } = "";
    [JsonPropertyName("layer")] public string? Layer { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("id")] public int? Id { get; init; }
}

/// <summary>What leaves the NPU part with a cut.</summary>
public sealed class GraphAway
{
    [JsonPropertyName("ops")] public int Ops { get; init; }
    [JsonPropertyName("weightValues")] public long WeightValues { get; init; }
    [JsonPropertyName("weightLayers")] public int WeightLayers { get; init; }
}

/// <summary>A cut the app can suggest: a known head (<c>yolo-v8</c>, <c>ssd</c>) or the last supported points (<c>frontier</c>).</summary>
public sealed class GraphProposal
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    /// <summary>The board-side box maths that fits the cut outputs, when the toolkit has one.</summary>
    [JsonPropertyName("decoder")] public string? Decoder { get; init; }
    [JsonPropertyName("nodes")] public List<string> Nodes { get; init; } = [];
    [JsonPropertyName("numClass")] public int? NumClass { get; init; }
    [JsonPropertyName("regMax")] public int? RegMax { get; init; }
    [JsonPropertyName("away")] public GraphAway? Away { get; init; }
    [JsonPropertyName("kept")] public int Kept { get; init; }
}

public sealed class GraphDrawing
{
    /// <summary>Operators in front of the drawn part, shown as one folded box.</summary>
    [JsonPropertyName("foldOps")] public int FoldOps { get; init; }
    /// <summary>Operators left out because the drawing would be too large.</summary>
    [JsonPropertyName("farOps")] public int FarOps { get; init; }
    [JsonPropertyName("nodes")] public List<GraphNode> Nodes { get; init; } = [];
    /// <summary>Pairs of node ids: from, to.</summary>
    [JsonPropertyName("edges")] public List<int[]> Edges { get; init; } = [];
    [JsonPropertyName("outputs")] public List<GraphOutput> Outputs { get; init; } = [];
}

public sealed class GraphNode
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("op")] public string Op { get; init; } = "";
    [JsonPropertyName("shape")] public List<int?>? Shape { get; init; }
    /// <summary><c>ok</c>, <c>bad</c> (the NPU cannot run it) or <c>blocked</c> (runnable, but behind a bad one).</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "ok";
    [JsonPropertyName("layer")] public string? Layer { get; init; }
    [JsonPropertyName("layerType")] public string? LayerType { get; init; }
    /// <summary>The reason of the converter, as written in its compatibility table.</summary>
    [JsonPropertyName("why")] public string? Why { get; init; }
    /// <summary><c>class</c> or <c>box</c> for the heads of a known detector.</summary>
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("proposed")] public bool Proposed { get; init; }
    [JsonPropertyName("frontier")] public bool Frontier { get; init; }
    [JsonPropertyName("weights")] public long Weights { get; init; }
    [JsonPropertyName("fromFold")] public bool FromFold { get; init; }
    [JsonPropertyName("toFold")] public bool ToFold { get; init; }
}

public sealed class GraphOutput
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("shape")] public List<int?>? Shape { get; init; }
    [JsonPropertyName("node")] public int? Node { get; init; }
}

/// <summary>
/// Output of <c>engine\helpers\gen_tail.py</c>: the C code for the part of a model behind a cut (design §5.8, stage 나),
/// or which operator could not be turned into code and why.
/// </summary>
public sealed class TailGeneration
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];
    /// <summary>Operators that cannot be turned into board code yet; the first one stopped the generator.</summary>
    [JsonPropertyName("unsupported")] public List<TailUnsupported> Unsupported { get; init; } = [];
    /// <summary>Operators behind the cut.</summary>
    [JsonPropertyName("ops")] public int Ops { get; init; }
    [JsonPropertyName("opTypes")] public List<string> OpTypes { get; init; } = [];
    /// <summary>The NPU outputs the code reads, one per cut operator.</summary>
    [JsonPropertyName("inputs")] public List<TailInput> Inputs { get; init; } = [];
    [JsonPropertyName("cFile")] public string? CFile { get; init; }
    [JsonPropertyName("sourceBytes")] public long SourceBytes { get; init; }
    /// <summary>The model's own outputs the code computes, in the order of the model file.</summary>
    [JsonPropertyName("outputs")] public List<TailOutput> Outputs { get; init; } = [];
}

public sealed class TailUnsupported
{
    [JsonPropertyName("node")] public string Node { get; init; } = "";
    [JsonPropertyName("op")] public string Op { get; init; } = "";
    [JsonPropertyName("why")] public string Why { get; init; } = "";
}

public sealed class TailInput
{
    [JsonPropertyName("cut")] public string Cut { get; init; } = "";
    [JsonPropertyName("tensor")] public string Tensor { get; init; } = "";
    [JsonPropertyName("shape")] public List<int> Shape { get; init; } = [];
    [JsonPropertyName("npu")] public TailNpuTensor? Npu { get; init; }
}

/// <summary>An output tensor of the compiled network (<c>network.h</c>).</summary>
public sealed class TailNpuTensor
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("size")] public int Size { get; init; }
    [JsonPropertyName("dims")] public List<int> Dims { get; init; } = [];
    [JsonPropertyName("scale")] public double Scale { get; init; }
}

/// <summary>One output of the part behind the cut; the values are filled by <c>eval_tail.py</c>.</summary>
public sealed class TailOutput
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("shape")] public List<int> Shape { get; init; } = [];
    [JsonPropertyName("file")] public string? File { get; init; }
    [JsonPropertyName("min")] public double? Min { get; init; }
    [JsonPropertyName("max")] public double? Max { get; init; }
    [JsonPropertyName("first")] public List<double> First { get; init; } = [];

    public string ShapeText => "[" + string.Join(", ", Shape) + "]";
}

/// <summary>Output of <c>engine\helpers\eval_tail.py</c>: the part behind the cut computed on the PC, per picture.</summary>
public sealed class TailEvaluation
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];
    [JsonPropertyName("runs")] public List<TailRun> Runs { get; init; } = [];
}

public sealed class TailRun
{
    /// <summary>The dump folder of the picture (<c>out\dump\&lt;network&gt;\&lt;picture&gt;</c>).</summary>
    [JsonPropertyName("dump")] public string Dump { get; init; } = "";
    [JsonPropertyName("outDir")] public string OutDir { get; init; } = "";
    [JsonPropertyName("outputs")] public List<TailOutput> Outputs { get; init; } = [];
}

/// <summary>
/// Output of <c>engine\helpers\cut_model.py</c>: the model cut at the chosen operators. For the head of an exported
/// YOLOv8 the grid file is written too.
/// </summary>
public sealed class ModelCutReport
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    /// <summary><c>v8</c> when the cut is the head of an exported YOLOv8, otherwise null.</summary>
    [JsonPropertyName("family")] public string? Family { get; init; }
    [JsonPropertyName("extracted")] public string? Extracted { get; init; }
    [JsonPropertyName("outputs")] public List<OnnxTensor> Outputs { get; init; } = [];
    /// <summary>The operators cut at, in the order of the outputs.</summary>
    [JsonPropertyName("nodes")] public List<string> Nodes { get; init; } = [];
    [JsonPropertyName("removedNodes")] public int RemovedNodes { get; init; }
    [JsonPropertyName("keptNodes")] public int KeptNodes { get; init; }
    [JsonPropertyName("grid")] public string? Grid { get; init; }
    [JsonPropertyName("grids")] public List<List<int>> Grids { get; init; } = [];
    [JsonPropertyName("numClass")] public int? NumClass { get; init; }
    [JsonPropertyName("regMax")] public int? RegMax { get; init; }
    [JsonPropertyName("outputOrder")] public string? OutputOrder { get; init; }
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];
}

/// <summary>
/// Output of <c>engine\helpers\zoo_export.py</c>: one model of the model zoo exported to ONNX the way the review did it
/// (design §5.9).
/// </summary>
public sealed class ZooExportReport
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("onnx")] public string? Onnx { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("params")] public long Params { get; init; }
    /// <summary>The fixes the export made on the way (<c>meanToPool</c>, <c>hardSigmoidToRelu6</c> ...: counts or true).</summary>
    [JsonPropertyName("fixes")] public Dictionary<string, JsonElement> Fixes { get; init; } = [];
    [JsonPropertyName("plain")] public bool Plain { get; init; }
    [JsonPropertyName("relu6")] public bool Relu6 { get; init; }
    [JsonPropertyName("size")] public int Size { get; init; }
    /// <summary>False when the model was exported with random weights (structure only).</summary>
    [JsonPropertyName("weights")] public bool Weights { get; init; }
    [JsonPropertyName("seconds")] public double Seconds { get; init; }
    [JsonPropertyName("versions")] public Dictionary<string, string> Versions { get; init; } = [];
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];
}

/// <summary>How one model of the model zoo is exported (the catalog's <c>export</c> entry plus who it is).</summary>
/// <param name="Plain">The review's first round: exported as is, none of the fixes.</param>
/// <param name="Relu6">Hard-sigmoid as clip(x + 3, 0, 6) / 6.</param>
/// <param name="WeightsTag">torchvision only: the weights entry (<c>IMAGENET1K_V1</c>).</param>
public sealed record ZooExportRecipe(string Library, string Name, string Task, int Size, bool Plain, bool Relu6, string? WeightsTag = null);
