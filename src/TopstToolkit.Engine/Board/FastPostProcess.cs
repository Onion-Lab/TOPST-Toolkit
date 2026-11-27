namespace TopstToolkit.Engine.Board;

/// <summary>
/// The app's own post-processing for the board (<c>engine\board\fast_*.c</c>, design §5.8): the same detections as
/// the toolkit's code, found much faster. A fast source takes the place of the compiler's <c>post_process.c</c> in
/// <c>net.so</c>; it includes that file, so the toolkit code stays in the library as
/// <c>run_post_process_reference</c> — the fallback for layouts the fast code does not handle, and what it is
/// compared with on a board (<c>engine\board\check_post_process.py</c>).
/// </summary>
public static class FastPostProcess
{
    /// <summary>YOLO v3/v4/v5/v6/v7/v8/X: candidates are picked from the raw int8 values with NEON.</summary>
    public const string YoloSource = "fast_yolo.c";

    /// <summary>SSD (softmax with a background class, or sigmoid): boxes are sorted out from their raw int8 values first.</summary>
    public const string SsdSource = "fast_ssd.c";

    /// <summary>
    /// What was measured on a TOPST AI-G board (one Cortex-A53 core, the dumped outputs of the toolkit's sample
    /// picture, 2026-11-22), as the package README says it. Change these together with design §5.8.
    /// </summary>
    public const string YoloMeasured =
        "TOPST AI-G(Cortex-A53)에서 yolov8m 한 프레임이 5.35 ms → 0.43 ms, yolov5m은 1.31 ms → 0.72 ms였습니다 (2026-11-22 측정, 모델과 검출 수에 따라 다릅니다). " +
        "클래스 점수만 있는 YOLOv6 · v8에서 차이가 크고, 물체 점수가 따로 있는 계열은 원래 코드도 1 ms 남짓이라 차이가 작습니다. " +
        "`tcnnapp`으로 실제 NPU 추론(yolov8m 43 ms)과 함께 돌렸을 때 앱이 찍은 후처리 시간은 5.6 ms → 0.62 ms였습니다.";
    public const string SsdMeasured =
        "TOPST AI-G(Cortex-A53)에서 mb2-ssd-lite 한 프레임이 1.22 ms → 0.16 ms, TFLite SSD MobileNet v1은 2.70 ms → 0.29 ms였습니다 (2026-11-22 측정, 모델과 검출 수에 따라 다릅니다). " +
        "`tcnnapp`으로 실제 NPU 추론(mb2-ssd-lite 2 ms)과 함께 돌렸을 때 앱이 찍은 후처리 시간은 1.5 ms → 0.26 ms였습니다.";

    /// <summary>The symbol the toolkit's generated function gets inside a library built with a fast source.</summary>
    public const string ReferenceSymbol = "run_post_process_reference";

    /// <summary>
    /// The fast source that fits a compiled network, or null when there is none: decided by which detector the
    /// compiler's <c>post_process.c</c> calls (the network header does not say whether a detector is YOLO or SSD).
    /// </summary>
    public static string? SourceNameFor(string compilerOutputDir)
    {
        var generated = Path.Combine(compilerOutputDir, "post_process.c");
        if (!File.Exists(generated))
            return null;
        var text = File.ReadAllText(generated);
        if (text.Contains("yolo_detect_object(", StringComparison.Ordinal))
            return YoloSource;
        return text.Contains("ssd_detect_object(", StringComparison.Ordinal) ? SsdSource : null;
    }

    /// <summary>The full path of the fast source for a compiled network, or null (none fits, or the file is not installed).</summary>
    public static string? Find(EngineLayout layout, string compilerOutputDir)
    {
        if (SourceNameFor(compilerOutputDir) is not { } name)
            return null;
        var path = Path.Combine(layout.BoardSourceDir, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>How a kind recorded in a project reads on the screen and in the package README.</summary>
    public static string Describe(string? kind) => kind switch
    {
        "fast-yolo" => "빠른 박스 계산 (YOLO)",
        "fast-ssd" => "빠른 박스 계산 (SSD)",
        TailCode.Kind => "잘라낸 구간 코드",
        _ => "툴킷 기본 코드",
    };

    /// <summary>The <c>--kind</c> the board check script (<c>check_post_process.py</c>) takes for a recorded kind.</summary>
    public static string? BoardCheckKind(string? kind) => kind switch
    {
        "fast-yolo" => "yolo",
        "fast-ssd" => "ssd",
        _ => null,
    };

    /// <summary>The fast source a recorded kind was built with, or null for the toolkit's own code.</summary>
    public static string? SourceOfKind(string? kind) => kind switch
    {
        "fast-yolo" => YoloSource,
        "fast-ssd" => SsdSource,
        _ => null,
    };

    /// <summary>
    /// The kind recorded for a library built with this source (null = the toolkit's own code). The generated code for
    /// the part behind a cut (<see cref="TailCode"/>) is recorded as <c>tail</c>; it has no source in <c>engine\board</c>.
    /// </summary>
    public static string KindOf(string? sourceName) => sourceName switch
    {
        YoloSource => "fast-yolo",
        SsdSource => "fast-ssd",
        TailCode.SourceName => TailCode.Kind,
        _ => "toolkit",
    };
}
