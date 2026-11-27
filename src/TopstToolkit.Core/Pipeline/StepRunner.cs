using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine;
using TopstToolkit.Engine.Board;
using TopstToolkit.Engine.Commands;
using TopstToolkit.Engine.Errors;
using TopstToolkit.Engine.Helpers;
using TopstToolkit.Engine.Parsing;
using TopstToolkit.Engine.Runner;

namespace TopstToolkit.Core.Pipeline;

/// <summary>One simulator result file with the image it belongs to.</summary>
public sealed record SimImageResult(string ResultDir, string? TextFile, string? ImageFile, SimResult? Parsed);

/// <summary>Outcome of one pipeline step: the run, the verdict and whatever was parsed from the outputs.</summary>
public sealed class StepResult
{
    public required string Step { get; init; }
    public required ToolRunResult Run { get; init; }
    public ToolError? Error { get; init; }
    public bool Success => Error is null;

    /// <summary>converter / quantizer: the .enlight written; compiler: the output folder; perf: the JSON.</summary>
    public string? Output { get; init; }
    public CompatibilityReport? Compatibility { get; init; }
    public IReadOnlyList<SimImageResult> SimResults { get; init; } = [];
    public PerfResult? Perf { get; init; }
    public NetworkBuffers? Buffers { get; init; }
    public double? WorkBufferMiB { get; init; }
    public IReadOnlyList<string> CreatedFiles { get; init; } = [];
    /// <summary>net.so: which post-processing went into the library (<see cref="FastPostProcess.KindOf"/>).</summary>
    public string? BoardPostProcess { get; init; }
}

/// <summary>
/// What generating the board code for the part behind a cut gave (<see cref="StepRunner.GenerateTailAsync"/>): the
/// source when there is one, otherwise a reason in a few words for the stage card.
/// </summary>
public sealed record TailGenerationResult(ToolRunResult Run, TailGeneration? Value, string? Source, string? Problem)
{
    public bool Cancelled => Run.Cancelled;
}

/// <summary>
/// Runs single pipeline steps for a project: makes a job folder, prepares output folders, runs the tool and
/// judges the result (design §4.4 success table: exit code 0 + completion marker + expected outputs + step checks).
/// </summary>
public sealed class StepRunner
{
    private readonly EngineSession _session;
    private readonly ProjectWorkspace _project;

    public StepRunner(EngineSession session, ProjectWorkspace project)
    {
        _session = session;
        _project = project.EnsureCreated();
    }

    public ProjectWorkspace Project => _project;

    public async Task<StepResult> ConvertAsync(ConverterOptions options, string step = "convert",
        Action<ToolEvent>? onEvent = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        Directory.CreateDirectory(options.CompatibilityLogRoot);
        var expectedCompatLog = Path.Combine(options.CompatibilityLogRoot, Path.GetFileNameWithoutExtension(options.Model) + "_compatibility.log");
        DeleteStale(options.Output, expectedCompatLog);
        var run = await RunAsync(_session.Commands.Converter(options), step, onEvent, ct).ConfigureAwait(false);

        var error = CommonVerdict(run);
        // The converter keeps going on incompatible layers (exit 0), so the report decides.
        var compatLog = run.ReportedPaths(ReportedPathKind.CompatibilityLog).LastOrDefault() ?? expectedCompatLog;
        var report = CompatibilityReport.ParseFile(compatLog) ?? CompatibilityReport.Parse(run.StdoutLines);
        if (error is null)
            error = ErrorClassifier.FromCompatibility(report);
        if (error is null && !File.Exists(options.Output))
            error = ErrorClassifier.MissingOutputs([options.Output]);

        return new StepResult { Step = step, Run = run, Error = error, Output = options.Output, Compatibility = report };
    }

    public async Task<StepResult> QuantizeAsync(QuantizerOptions options, string step = "quantize",
        Action<ToolEvent>? onEvent = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        DeleteStale(options.Output);
        var run = await RunAsync(_session.Commands.Quantizer(options), step, onEvent, ct).ConfigureAwait(false);
        var error = CommonVerdict(run);
        if (error is null && !File.Exists(options.Output))
            error = ErrorClassifier.MissingOutputs([options.Output]);
        return new StepResult { Step = step, Run = run, Error = error, Output = options.Output };
    }

    public async Task<StepResult> CompileAsync(CompilerOptions options, string step = "compile",
        Action<ToolEvent>? onEvent = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.DumpRoot);
        DeleteStaleDirectory(CompilerOutputs.OutputDirFor(options.DumpRoot, options.Model));
        var run = await RunAsync(_session.Commands.Compiler(options), step, onEvent, ct).ConfigureAwait(false);
        var outputDir = run.ReportedPaths(ReportedPathKind.CompilerOutputDir).LastOrDefault()
                        ?? CompilerOutputs.OutputDirFor(options.DumpRoot, options.Model);

        var error = CommonVerdict(run);
        if (error is null)
        {
            var missing = CompilerOutputs.FindMissing(outputDir);
            if (missing.Count > 0)
                error = ErrorClassifier.MissingOutputs(missing);
            else if (!run.HasMarker(ToolMarker.CompilerSucceeded) || !CompilerOutputs.DebugLogReportsSuccess(outputDir))
                error = ErrorClassifier.MissingDoneMarker(run);
        }

        return new StepResult
        {
            Step = step,
            Run = run,
            Error = error,
            Output = outputDir,
            Buffers = NetworkBuffers.ParseFile(Path.Combine(outputDir, "network_buf.txt")),
            WorkBufferMiB = run.Events.OfType<WorkBufferEvent>().LastOrDefault()?.MiB,
            CreatedFiles = run.Events.OfType<FileCreatedEvent>().Select(e => e.Path).ToList(),
        };
    }

    public async Task<StepResult> SimulateAsync(SimulatorOptions options, string step = "simulate",
        Action<ToolEvent>? onEvent = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.ResultRoot);
        var modelName = Path.GetFileNameWithoutExtension(options.Model);
        DeleteStaleDirectory(Path.Combine(options.ResultRoot, modelName));
        if (options.DumpRoot is not null)
        {
            Directory.CreateDirectory(options.DumpRoot);
            if (options.Dump)
                DeleteStaleDirectory(Path.Combine(options.DumpRoot, modelName));
        }
        var run = await RunAsync(_session.Commands.Simulator(options), step, onEvent, ct).ConfigureAwait(false);

        // One "Save results (result) path" per image, then its text/image files.
        var results = new List<SimImageResult>();
        string? currentDir = null;
        string? text = null, image = null;
        void Flush()
        {
            if (currentDir is null) return;
            results.Add(new SimImageResult(currentDir, text, image, text is not null && File.Exists(text) ? SimResult.ParseFile(text) : null));
            text = image = null;
        }
        foreach (var e in run.Events.OfType<PathReportedEvent>())
        {
            switch (e.Kind)
            {
                case ReportedPathKind.ResultDir:
                    Flush();
                    currentDir = e.Path;
                    break;
                case ReportedPathKind.ResultText:
                    text = e.Path;
                    break;
                case ReportedPathKind.ResultImage:
                    image = e.Path;
                    break;
            }
        }
        Flush();

        var error = CommonVerdict(run);
        if (error is null && (results.Count == 0 || results.Any(r => r.Parsed is null)))
            error = ErrorClassifier.MissingOutputs(results.Count == 0 ? ["_result.txt"] : results.Where(r => r.Parsed is null).Select(r => r.TextFile ?? r.ResultDir));

        return new StepResult { Step = step, Run = run, Error = error, Output = options.ResultRoot, SimResults = results };
    }

    public async Task<StepResult> PerfAsync(PerfOptions options, string step = "perf",
        Action<ToolEvent>? onEvent = null, CancellationToken ct = default)
    {
        // perf_simulator does not create its output folder (feature/09 §6-1).
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        DeleteStale(options.Output);
        var run = await RunAsync(_session.Commands.Perf(options), step, onEvent, ct).ConfigureAwait(false);
        var error = CommonVerdict(run);
        var perf = PerfResult.ParseFile(options.Output);
        if (error is null && perf is null)
            error = ErrorClassifier.MissingOutputs([options.Output]);
        return new StepResult { Step = step, Run = run, Error = error, Output = options.Output, Perf = perf };
    }

    /// <summary>
    /// Builds <c>net.so</c> for a compiled network with the Arm cross compiler and puts it into the compiler output
    /// folder, as the official procedure does (<c>cp net.so &lt;name&gt;_quantized/</c>, design §11 D5).
    /// </summary>
    /// <param name="fastPostProcess">
    /// Put the app's fast post-processing into the library when one fits the network (design §5.8). The toolkit's own
    /// code stays in the library next to it. False = the official build, unchanged.
    /// </param>
    /// <param name="tailSource">
    /// Generated code for the part of the model behind its cut (<see cref="GenerateTailAsync"/>, design §5.8 stage 나),
    /// built in the place of a fast source. Like one, it falls back to the official library when it does not compile.
    /// </param>
    public async Task<StepResult> BuildBoardLibraryAsync(string compilerOutputDir, string step = "netso",
        Action<ToolEvent>? onEvent = null, CancellationToken ct = default, bool fastPostProcess = true, string? tailSource = null)
    {
        var target = Path.Combine(compilerOutputDir, NetSoBuild.OutputName);
        var compileRequest = Request(step);
        var buildDir = compileRequest.WorkingDirectory;
        // The mingw-hosted gcc writes its intermediate files to TEMP; keep them on an ASCII path inside the job folder.
        var temp = Directory.CreateDirectory(Path.Combine(buildDir, "tmp")).FullName;
        var environment = new Dictionary<string, string> { ["TMP"] = temp, ["TEMP"] = temp, ["TMPDIR"] = temp };
        ToolRunRequest WithEnvironment(ToolRunRequest r, string logSuffix) => new()
        {
            WorkingDirectory = buildDir,
            PycacheDirectory = r.PycacheDirectory,
            LogDirectory = r.LogDirectory,
            LogName = r.LogName + logSuffix,
            Environment = environment,
        };

        var gcc = _session.Layout.FindCrossGcc();
        if (gcc is null)
            return new StepResult { Step = step, Run = NotRun(step, buildDir), Error = ErrorClassifier.ToolchainMissing(), Output = target };

        DeleteStale(target);
        var fastPath = tailSource ?? (fastPostProcess ? FastPostProcess.Find(_session.Layout, compilerOutputDir) : null);
        var fastSource = fastPath is null ? null : Path.GetFileName(fastPath);
        NetSoBuild.PrepareSources(_session.Layout.BoardRuntimeSourceDir, compilerOutputDir, buildDir, fastPath);

        StepResult Failed(ToolRunResult run) => new()
        {
            Step = step, Run = run, Output = target,
            Error = run.Cancelled ? ErrorClassifier.Classify(run) : ErrorClassifier.BoardBuildFailed(NetSoBuild.FirstError(run.AllLines)),
        };

        var compile = await _session.Runner.RunAsync(NetSoBuild.Compile(gcc, buildDir, fastSource), WithEnvironment(compileRequest, "-compile"), onEvent, ct).ConfigureAwait(false);
        if (compile.Cancelled || compile.ExitCode != 0)
            return Failed(compile);

        if (fastSource is not null)
        {
            var fast = await _session.Runner.RunAsync(NetSoBuild.CompileFast(gcc, buildDir, fastSource), WithEnvironment(compileRequest, "-compile-fast"), onEvent, ct).ConfigureAwait(false);
            if (fast.Cancelled)
                return Failed(fast);
            if (fast.ExitCode != 0)
            {
                // Our own source does not compile against this runtime. The toolkit's code alone still makes the
                // official library, so the build goes on with that instead of failing.
                fastSource = null;
                var generated = await _session.Runner.RunAsync(NetSoBuild.CompileGenerated(gcc, buildDir), WithEnvironment(compileRequest, "-compile-generated"), onEvent, ct).ConfigureAwait(false);
                if (generated.Cancelled || generated.ExitCode != 0)
                    return Failed(generated);
            }
        }

        var link = await _session.Runner.RunAsync(NetSoBuild.Link(gcc, fastSource), WithEnvironment(compileRequest, "-link"), onEvent, ct).ConfigureAwait(false);
        var built = Path.Combine(buildDir, NetSoBuild.OutputName);
        ToolError? error = null;
        if (link.Cancelled)
            error = ErrorClassifier.Classify(link);
        else if (link.ExitCode != 0)
            error = ErrorClassifier.BoardBuildFailed(NetSoBuild.FirstError(link.AllLines));
        else if (ElfHeader.TryRead(built) is not { IsAArch64SharedObject: true })
            error = ErrorClassifier.BoardBuildFailed("net.so is missing or is not an AArch64 shared object");

        if (error is null)
            File.Copy(built, target, overwrite: true);
        return new StepResult { Step = step, Run = link, Error = error, Output = target, BoardPostProcess = FastPostProcess.KindOf(fastSource) };
    }

    private static ToolRunResult NotRun(string step, string workingDirectory) => new()
    {
        Invocation = new ToolInvocation(ToolKind.Native, EngineLayout.CrossGccFileName, [], step),
        WorkingDirectory = workingDirectory,
        ExitCode = -1,
        StdoutLines = [],
        StderrLines = [],
        Events = [],
    };

    public Task<HelperResult<EnlightInfo>> ReadEnlightAsync(string enlightPath, CancellationToken ct = default) =>
        _session.Helpers.ReadEnlightAsync(enlightPath, Request("read-enlight"), ct);

    /// <summary>
    /// Writes the board code for the part of a model behind its cut (design §5.8, stage 나) to
    /// <c>&lt;tailDir&gt;\topst_tail.c</c>: reads the layer names of the quantized network (they connect the cut operators
    /// of the original ONNX file to the outputs in <c>network.h</c>), then runs the generator. Both answers are kept in
    /// <paramref name="tailDir"/>; earlier results there are removed first, so nothing stale is left when it fails.
    /// </summary>
    public async Task<TailGenerationResult> GenerateTailAsync(string onnxPath, IReadOnlyList<string> nodes, string int8Network,
        string compilerOutputDir, string tailDir, string title, CancellationToken ct = default)
    {
        Directory.CreateDirectory(tailDir);
        var source = Path.Combine(tailDir, TailCode.SourceName);
        var layersJson = Path.Combine(tailDir, TailFiles.Layers);
        var answer = Path.Combine(tailDir, TailFiles.Generation);
        DeleteStale(source, layersJson, answer, Path.Combine(tailDir, TailFiles.Evaluation));
        DeleteStaleDirectory(Path.Combine(tailDir, TailFiles.PcDir));

        var layers = await ReadEnlightAsync(int8Network, ct).ConfigureAwait(false);
        var layersAnswer = Path.Combine(layers.Run.WorkingDirectory, "read_enlight.json");
        if (layers.Run.Cancelled || layers.Value is not { Ok: true, Layers.Count: > 0 } || !File.Exists(layersAnswer))
            return new TailGenerationResult(layers.Run, null, null, "레이어 이름을 읽지 못함");
        File.Copy(layersAnswer, layersJson, overwrite: true);

        var generated = await _session.Helpers.GenerateTailAsync(onnxPath, nodes, Path.Combine(compilerOutputDir, "network.h"), layersJson,
            source, title, Request("gen-tail"), ct).ConfigureAwait(false);
        var generatedAnswer = Path.Combine(generated.Run.WorkingDirectory, "gen_tail.json");
        if (File.Exists(generatedAnswer))
            File.Copy(generatedAnswer, answer, overwrite: true);
        if (generated.Value is { Ok: true } value && File.Exists(source))
            return new TailGenerationResult(generated.Run, value, source, null);
        var problem = generated.Value?.Unsupported.FirstOrDefault() is { } op ? $"{op.Op} 연산은 아직 옮기지 못함" : "구간을 코드로 옮기지 못함";
        return new TailGenerationResult(generated.Run, generated.Value, null, problem);
    }

    /// <summary>
    /// The part behind the cut computed on the PC for every dump folder (one per check picture): the values the board code
    /// has to give for the same NPU outputs. Written to <c>&lt;tailDir&gt;\pc\&lt;picture&gt;\</c>, the answer to
    /// <c>&lt;tailDir&gt;\eval_tail.json</c> when it succeeded.
    /// </summary>
    public async Task<HelperResult<TailEvaluation>> EvaluateTailAsync(string onnxPath, IReadOnlyList<string> nodes, string compilerOutputDir,
        IReadOnlyList<string> dumpDirs, string tailDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(tailDir);
        var answer = Path.Combine(tailDir, TailFiles.Evaluation);
        DeleteStale(answer);
        DeleteStaleDirectory(Path.Combine(tailDir, TailFiles.PcDir));
        var outDirs = dumpDirs.Select(d => TailFiles.PcFolder(tailDir, Path.GetFileName(Path.TrimEndingDirectorySeparator(d)))).ToList();
        var result = await _session.Helpers.EvaluateTailAsync(onnxPath, nodes, Path.Combine(compilerOutputDir, "network.h"), dumpDirs, outDirs,
            Request("eval-tail"), ct).ConfigureAwait(false);
        var produced = Path.Combine(result.Run.WorkingDirectory, "eval_tail.json");
        if (result.Value is { Ok: true } && File.Exists(produced))
            File.Copy(produced, answer, overwrite: true);
        return result;
    }

    public Task<HelperResult<EngineCheckReport>> EngineCheckAsync(CancellationToken ct = default) =>
        _session.Helpers.EngineCheckAsync(Request("engine-check"), ct);

    public Task<HelperResult<ModelInspection>> InspectModelAsync(string onnxPath, CancellationToken ct = default) =>
        _session.Helpers.InspectModelAsync(onnxPath, Request("inspect-model"), ct);

    public Task<HelperResult<YoloPreparation>> PrepareYoloAsync(string onnxPath, string outputDir, string stem, CancellationToken ct = default) =>
        _session.Helpers.PrepareYoloAsync(onnxPath, outputDir, stem, Request("prepare-yolo"), ct);

    public Task<HelperResult<ModelGraphReport>> AnalyzeGraphAsync(string onnxPath, string? layersJson, int depth = 2, CancellationToken ct = default) =>
        _session.Helpers.AnalyzeGraphAsync(onnxPath, layersJson, depth, Request("graph-model"), ct);

    public Task<HelperResult<ModelCutReport>> CutModelAsync(string onnxPath, IReadOnlyList<string> nodes, string outputDir, string stem, CancellationToken ct = default) =>
        _session.Helpers.CutModelAsync(onnxPath, nodes, outputDir, stem, Request("cut-model"), ct);

    private Task<ToolRunResult> RunAsync(ToolInvocation invocation, string step, Action<ToolEvent>? onEvent, CancellationToken ct) =>
        _session.Runner.RunAsync(invocation, Request(step), onEvent, ct);

    private ToolRunRequest Request(string step) => new()
    {
        WorkingDirectory = _project.NewJobDirectory(step),
        PycacheDirectory = _session.Workspace.PycacheDir,
        LogDirectory = _project.LogsDir,
        LogName = _project.NewLogName(step),
    };

    /// <summary>
    /// Outputs of an earlier run must not stand in for this run's (a failed compile would otherwise "have" all 13 files).
    /// Only paths inside the project are touched.
    /// </summary>
    private void DeleteStale(params string[] files)
    {
        foreach (var file in files)
            if (IsInsideProject(file) && File.Exists(file))
                File.Delete(file);
    }

    private void DeleteStaleDirectory(string dir)
    {
        if (IsInsideProject(dir) && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private bool IsInsideProject(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_project.Root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cancel → exit code → completion marker, the checks every tool shares.</summary>
    private static ToolError? CommonVerdict(ToolRunResult run)
    {
        if (run.Cancelled)
            return ErrorClassifier.Classify(run);
        if (run.ExitCode != 0)
            return ErrorClassifier.Classify(run);
        if (!run.HasMarker(ToolMarker.Done))
            return ErrorClassifier.Classify(run) ?? ErrorClassifier.MissingDoneMarker(run);
        return null;
    }
}
