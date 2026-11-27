namespace TopstToolkit.Engine.Errors;

public enum ToolErrorCode
{
    Cancelled,
    IncompatibleLayers,
    CompatibilityUnknown,
    NotCalibrated,
    CalibrationImagesNotFound,
    NonAsciiPath,
    NonAsciiLabel,
    ModelConfigConflict,
    SdkVersionMismatch,
    ModelLoadFailed,
    InputMissing,
    OutOfMemory,
    NoDetectionLayer,
    InvalidOptions,
    DatasetNotFound,
    CompilerFailed,
    EngineSetup,
    MissingDoneMarker,
    MissingOutputs,
    /// <summary>The optional Arm cross toolchain is not installed.</summary>
    ToolchainMissing,
    /// <summary>gcc failed while building <c>net.so</c>.</summary>
    BoardBuildFailed,
    /// <summary>The converter or the compiler names something the NPU does not do (an operator, an activation, a padding).</summary>
    UnsupportedByNpu,
    /// <summary>A layer does not fit the NPU's buffers at this input size (the compiler cannot split it).</summary>
    NpuMemoryLimit,
    Unexpected,
}

/// <summary>What the screen offers next to the message (design §4.8).</summary>
public enum SuggestedAction
{
    None,
    ShowCompatibility,
    GoToConvert,
    GoToData,
    CheckWorkspace,
    EditLabels,
    ReconvertFromSource,
    CloseOtherApps,
    MakeDiagnostics,
    InstallBoardTools,
}

/// <param name="Title">One short sentence in plain Korean (design §5.3).</param>
/// <param name="Detail">What to do about it.</param>
/// <param name="Evidence">The log line that matched, for the details pane and diagnostics.</param>
public sealed record ToolError(ToolErrorCode Code, string Title, string Detail, SuggestedAction Action, string? Evidence = null);
