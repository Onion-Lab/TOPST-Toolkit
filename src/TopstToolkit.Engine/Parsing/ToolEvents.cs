namespace TopstToolkit.Engine.Parsing;

public enum StreamKind
{
    Stdout,
    Stderr,
}

/// <summary>What the tool is doing, as far as its console output tells (feature/04 §5.3).</summary>
public enum ToolStage
{
    LoadModel,
    Optimize,
    Compatibility,
    Calibration,
    Serialize,
    CodeEmit,
    WriteWeights,
    Inference,
    Evaluate,
    Perf,
}

public enum ToolMarker
{
    /// <summary>The start banner between the 80-star lines.</summary>
    Started,
    /// <summary>The tool's own completion line (<c>Converter. done.</c> and so on).</summary>
    Done,
    /// <summary><c>NPU compiler: done sucessfully.</c> (sic) printed before <c>Compiler. done.</c></summary>
    CompilerSucceeded,
}

public enum ReportedPathKind
{
    /// <summary>converter / quantizer: <c>Writing File path :  &lt;.enlight&gt;</c></summary>
    EnlightWritten,
    CompatibilityLog,
    /// <summary>compiler: <c>output_dir : &lt;dump-root&gt;\&lt;net&gt;</c></summary>
    CompilerOutputDir,
    ResultDir,
    ResultText,
    ResultImage,
    ResultNpy,
    DumpDir,
    PerfJson,
}

public abstract record ToolEvent
{
    /// <summary>High-volume events that go to listeners but are not kept in the run result.</summary>
    public virtual bool IsTransient => false;
}

/// <summary>One cleaned console line (ANSI removed). <see cref="IsUpdate"/> marks a progress rewrite.</summary>
public sealed record OutputLineEvent(StreamKind Stream, string Text, bool IsUpdate) : ToolEvent
{
    public override bool IsTransient => true;
}

public sealed record ProgressEvent(ToolStage Stage, long Current, long Total) : ToolEvent
{
    public override bool IsTransient => true;
    /// <summary>0..1, or null when the total is unknown.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp((double)Current / Total, 0, 1) : null;
}

public sealed record StageEvent(ToolStage Stage, string? Name = null) : ToolEvent;

public sealed record MarkerEvent(ToolMarker Marker, string Text) : ToolEvent;

public sealed record FileCreatedEvent(string Path) : ToolEvent;

public sealed record PathReportedEvent(ReportedPathKind Kind, string Path) : ToolEvent;

public sealed record CompatibilitySummaryEvent(int Compatible, int Incompatible, int Total) : ToolEvent;

public sealed record WorkBufferEvent(double MiB) : ToolEvent;

public sealed record PerfTimeEvent(double Milliseconds) : ToolEvent;

/// <summary><c>[INFO] Find model_config &lt;name&gt;.json, but can't find it</c> — expected with an empty model-config root.</summary>
public sealed record ModelConfigNoticeEvent(string ConfigName) : ToolEvent;

/// <summary>The <c>name(40 columns): value</c> argument echo every tool prints after its banner.</summary>
public sealed record ArgumentEchoEvent(string Name, string Value) : ToolEvent;

/// <summary>No output for a long time (design §4.4: shown, never auto-killed).</summary>
public sealed record StalledEvent(TimeSpan Silence) : ToolEvent;

/// <summary>Output resumed after a <see cref="StalledEvent"/>.</summary>
public sealed record ResumedEvent : ToolEvent;
