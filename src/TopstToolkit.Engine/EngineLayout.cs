namespace TopstToolkit.Engine;

/// <summary>
/// Where the bundled engine pieces live.
/// <c>runtime\</c> is produced by <c>build\build-runtime.ps1</c>: <c>python38\</c>, <c>toolkit\</c>, <c>samples\</c>.
/// <c>engine\helpers\</c> holds our own Python scripts.
/// </summary>
public sealed class EngineLayout
{
    public EngineLayout(string runtimeRoot, string helpersDir)
    {
        RuntimeRoot = Path.GetFullPath(runtimeRoot);
        HelpersDir = Path.GetFullPath(helpersDir);
    }

    public string RuntimeRoot { get; }
    public string HelpersDir { get; }

    public string PythonExe => Path.Combine(RuntimeRoot, "python38", "python.exe");

    /// <summary>
    /// <c>onnx</c> for our helpers, deliberately off the default <c>sys.path</c>: the SDK bundles its own <c>onnx_pb2</c>
    /// and protobuf refuses both in one process. Optional — only ONNX inspection needs it.
    /// </summary>
    public string OnnxExtrasDir => Path.Combine(RuntimeRoot, "extras", "onnx");
    public string ToolkitRoot => Path.Combine(RuntimeRoot, "toolkit");
    public string EnlightSdkDir => Path.Combine(ToolkitRoot, "EnlightSDK");
    public string LabelsDir => Path.Combine(ToolkitRoot, "labels");
    public string SampleImage => Path.Combine(ToolkitRoot, "sample", "dog.png");
    public string SamplesDir => Path.Combine(RuntimeRoot, "samples");
    public string DefaultCalibrationDir => Path.Combine(SamplesDir, "calib-imagenet100");

    /// <summary>The C runtime sources <c>net.so</c> is built from (<c>toolkituild_network</c>, feature/08).</summary>
    public string BoardRuntimeSourceDir => Path.Combine(ToolkitRoot, "build_network");

    /// <summary>
    /// Our own C sources for the board (<c>engine\board\</c>, next to the helper scripts): the fast post-processing
    /// that takes the place of the toolkit's when <c>net.so</c> is built (design §5.8).
    /// </summary>
    public string BoardSourceDir => Path.Combine(Path.GetDirectoryName(HelpersDir) ?? HelpersDir, "board");

    /// <summary>Optional component "board library build tools": holds the Arm GNU-A 9.2-2019.12 toolchain (design §11 D5).</summary>
    public string ToolchainRoot => Path.Combine(Path.GetDirectoryName(RuntimeRoot) ?? RuntimeRoot, "toolchain");

    public const string CrossGccFileName = "aarch64-none-linux-gnu-gcc.exe";

    /// <summary>The cross compiler, or null when the toolchain is not installed.</summary>
    public string? FindCrossGcc()
    {
        if (!Directory.Exists(ToolchainRoot))
            return null;
        var direct = Path.Combine(ToolchainRoot, "bin", CrossGccFileName);
        if (File.Exists(direct))
            return direct;
        return Directory.EnumerateDirectories(ToolchainRoot)
            .Select(d => Path.Combine(d, "bin", CrossGccFileName))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>The toolkit.s example clock file (<c>cclk1_aclk.6_pclk.1.json</c>). Not the board's real clocks (D4).</summary>
    public string ExampleNpuConfig => Path.Combine(ToolkitRoot, "tools_sdk", "perf_sim", "cclk1_aclk.6_pclk.1.json");

    /// <summary>
    /// The TOPST AI-G board's own clocks for the estimate (<c>engine\board\topst-ai-g-npu.json</c>, next to the helper
    /// scripts): NPU core 1000, AXI 800, NPU CPU 800, APB 200 MHz as the board reports them (design §11, 2026-11-28).
    /// </summary>
    public string BoardNpuConfig => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(HelpersDir))!, "board", "topst-ai-g-npu.json");

    /// <summary>
    /// What the board does differently from the simulator, fitted to its measured times (<c>engine\board\topst-ai-g-time.json</c>,
    /// read by <c>engine\helpers\board_time.py</c>; design §11, 2026-11-28).
    /// </summary>
    public string BoardTimeConfig => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(HelpersDir))!, "board", "topst-ai-g-time.json");

    public string ScriptFor(ToolKind tool) => tool switch
    {
        ToolKind.Converter => Path.Combine(EnlightSdkDir, "converter.py"),
        ToolKind.Quantizer => Path.Combine(EnlightSdkDir, "quantizer.py"),
        ToolKind.Compiler => Path.Combine(EnlightSdkDir, "compiler.py"),
        ToolKind.Simulator => Path.Combine(EnlightSdkDir, "enlight_sim.py"),
        ToolKind.Evaluator => Path.Combine(EnlightSdkDir, "evaluator.py"),
        ToolKind.PerfSimulator => Path.Combine(ToolkitRoot, "tools_sdk", "perf_sim", "perf_simulator.py"),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Helpers are addressed with HelperScript(name)."),
    };

    public string HelperScript(string name) =>
        Path.Combine(HelpersDir, name.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? name : name + ".py");

    /// <summary>Files the engine cannot run without. Empty when the layout is complete.</summary>
    public IReadOnlyList<string> FindMissing()
    {
        var required = new List<string> { PythonExe, ExampleNpuConfig, DefaultCalibrationDir, HelpersDir };
        foreach (var tool in new[] { ToolKind.Converter, ToolKind.Quantizer, ToolKind.Compiler, ToolKind.Simulator, ToolKind.PerfSimulator })
            required.Add(ScriptFor(tool));
        return required.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
    }

    /// <summary>
    /// The folder of our own scripts that comes with the app (or the repository): the first <c>engine\helpers</c>
    /// above the start directory. Null when there is none.
    /// </summary>
    public static string? FindHelpersDir(string? startDirectory = null)
    {
        for (var d = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var helpers = Path.Combine(d.FullName, "engine", "helpers");
            if (Directory.Exists(helpers))
                return helpers;
        }
        return null;
    }

    /// <summary>
    /// The engine the app installed into a components folder (<c>&lt;root&gt;\runtime</c>, design D12) together with the
    /// app's own scripts, or null when it is not installed there.
    /// </summary>
    public static EngineLayout? InComponents(string? componentsRoot, string? startDirectory = null)
    {
        if (componentsRoot is null || FindHelpersDir(startDirectory) is not { } helpers)
            return null;
        var layout = new EngineLayout(Path.Combine(componentsRoot, "runtime"), helpers);
        return File.Exists(layout.PythonExe) ? layout : null;
    }

    /// <summary>
    /// Finds the engine next to the app (installed: <c>&lt;app&gt;\runtime</c> + <c>&lt;app&gt;\engine\helpers</c>)
    /// or, during development, in the repository (<c>artifacts\runtime</c> + <c>engine\helpers</c>).
    /// An engine the app installed itself is found with <see cref="InComponents"/>.
    /// </summary>
    public static EngineLayout? Discover(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            var installed = new EngineLayout(Path.Combine(d.FullName, "runtime"), Path.Combine(d.FullName, "engine", "helpers"));
            if (File.Exists(installed.PythonExe) && Directory.Exists(installed.HelpersDir))
                return installed;

            var dev = new EngineLayout(Path.Combine(d.FullName, "artifacts", "runtime"), Path.Combine(d.FullName, "engine", "helpers"));
            if (File.Exists(dev.PythonExe) && Directory.Exists(dev.HelpersDir))
                return dev;
        }
        return null;
    }
}

public enum ToolKind
{
    Converter,
    Quantizer,
    Compiler,
    Simulator,
    Evaluator,
    PerfSimulator,
    /// <summary>Our own scripts in <c>engine\helpers\</c>.</summary>
    Helper,
    /// <summary>A native program run directly, not through Python (the Arm cross compiler).</summary>
    Native,
}
