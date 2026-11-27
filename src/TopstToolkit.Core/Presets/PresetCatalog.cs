using TopstToolkit.Core.Projects;

namespace TopstToolkit.Core.Presets;

/// <summary>
/// Converter and compiler values for one model family. The numbers are the toolkit's example values, each run
/// end to end on Windows on 2026-11-20 (docs/design.md §8.1, feature/02 recipes A–E).
/// </summary>
public sealed record ModelPreset
{
    public required string Id { get; init; }
    /// <summary>Short family name shown next to the task ("YOLOv5", "SSD").</summary>
    public required string Family { get; init; }
    public required string DisplayName { get; init; }
    /// <summary><c>obj</c>, <c>class</c> or <c>unknown</c>.</summary>
    public required string Task { get; init; }
    public double[] Mean { get; init; } = [0, 0, 0];
    public double[] Std { get; init; } = [1, 1, 1];
    public int? NumClass { get; init; }
    public string? LabelSet { get; init; }
    public string? YoloVersion { get; init; }
    public int? DflRegMax { get; init; }
    public string? OutputOrder { get; init; }
    public double[]? Variance { get; init; }
    public string? Logistic { get; init; }
    public bool Letterbox { get; init; }
    public double ThConf { get; init; } = 0.5;
    public double ThIou { get; init; } = 0.5;
    /// <summary>What the family needs besides the model: <c>anchor</c> (SSD), <c>grid</c> (YOLO <c>.bin</c>), <c>weights</c> (Darknet) or null.</summary>
    public string? Companion { get; init; }

    /// <summary>Writes the preset's values into a project's options. Files the user picked are left alone.</summary>
    public void ApplyTo(ProjectFile project)
    {
        project.Preset = Id;
        project.Task = Task;
        var c = project.Options.Convert;
        c.Mean = [.. Mean];
        c.Std = [.. Std];
        c.NumClass = NumClass;
        c.YoloVersion = YoloVersion;
        c.DflRegMax = DflRegMax;
        c.OutputOrder = OutputOrder;
        c.Variance = Variance is null ? null : [.. Variance];
        c.Logistic = Logistic;
        c.Letterbox = Letterbox;
        project.Options.Labels.Set = LabelSet;
        project.Options.Compile.ThConf = ThConf;
        project.Options.Compile.ThIou = ThIou;
    }
}

public static class PresetCatalog
{
    private static ModelPreset Yolo(string version, string family, int? dfl = null, string? order = null, double[]? std = null) => new()
    {
        Id = "pytorch-yolo-" + version,
        Family = family,
        DisplayName = "PyTorch " + family,
        Task = "obj",
        Std = std ?? [1, 1, 1],
        NumClass = 80,
        LabelSet = "coco80",
        YoloVersion = version,
        DflRegMax = dfl,
        OutputOrder = order,
        Letterbox = true,
        Companion = "grid",
    };

    public static readonly IReadOnlyList<ModelPreset> All =
    [
        new()
        {
            Id = "classification-imagenet", Family = "ImageNet", DisplayName = "분류 (ImageNet 전처리)", Task = "class",
            Mean = [0.485, 0.456, 0.406], Std = [0.229, 0.224, 0.225], NumClass = 1000, LabelSet = "imagenet",
        },
        Yolo("v5", "YOLOv5"),
        Yolo("v8", "YOLOv8", dfl: 16, order: "cl"),
        Yolo("v7", "YOLOv7"),
        Yolo("v6", "YOLOv6", dfl: 17, order: "cl"),
        Yolo("v3", "YOLOv3"),
        Yolo("x", "YOLOX", order: "clo", std: [0.00392156, 0.00392156, 0.00392156]),
        new()
        {
            Id = "ssd-anchor", Family = "SSD", DisplayName = "SSD (anchor 파일)", Task = "obj",
            Mean = [0.498, 0.498, 0.498], Std = [0.502, 0.502, 0.502], NumClass = 20, LabelSet = "voc20",
            Variance = [0.1, 0.2], Logistic = "softmax", ThConf = 0.4, ThIou = 0.45, Companion = "anchor",
        },
        new()
        {
            Id = "tflite-ssd", Family = "TFLite SSD", DisplayName = "TFLite SSD (후처리 내장)", Task = "obj",
            Mean = [0.5, 0.5, 0.5], Std = [0.5, 0.5, 0.5], NumClass = 90, LabelSet = "coco91", ThConf = 0.3, ThIou = 0.6,
        },
        new()
        {
            Id = "darknet-yolo", Family = "Darknet YOLO", DisplayName = "Darknet YOLO (.cfg + .weights)", Task = "obj",
            NumClass = 80, LabelSet = "coco80", ThConf = 0.7, ThIou = 0.213, Companion = "weights",
        },
        new() { Id = "generic-unknown", Family = "", DisplayName = "기타 (출력값 그대로)", Task = "unknown" },
    ];

    public static ModelPreset? Find(string? id) => id is null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>"객체 검출 · YOLOv5", "분류", "기타" — the kind column of project lists.</summary>
    public static string DescribeKind(string task, string? presetId)
    {
        var taskName = task switch { "obj" => "객체 검출", "class" => "분류", "unknown" => "기타", _ => "종류 미정" };
        var family = Find(presetId)?.Family;
        return task == "obj" && !string.IsNullOrEmpty(family) ? $"{taskName} · {family}" : taskName;
    }
}
