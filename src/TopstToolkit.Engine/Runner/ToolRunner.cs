using System.Diagnostics;
using System.Text;
using TopstToolkit.Engine.Commands;
using TopstToolkit.Engine.Parsing;

namespace TopstToolkit.Engine.Runner;

/// <summary>Where and how to run one invocation.</summary>
public sealed class ToolRunRequest
{
    /// <summary>The job folder. For the compiler it must contain <c>EnlightSDK\enlight\code_emission\cfg\</c> (feature/09 §5.2).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Bytecode cache outside the install folder (<c>-X pycache_prefix</c>).</summary>
    public required string PycacheDirectory { get; init; }

    /// <summary>Raw streams are saved as <c>&lt;LogDirectory&gt;\&lt;LogName&gt;.out.log</c> / <c>.err.log</c>.</summary>
    public string? LogDirectory { get; init; }
    public string? LogName { get; init; }

    /// <summary>"No response" notice after this much silence. The run is never killed for it.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Extra environment variables for this run (for example an ASCII TEMP folder for the cross compiler).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public sealed class ToolRunResult
{
    public required ToolInvocation Invocation { get; init; }
    public required string WorkingDirectory { get; init; }
    public int ExitCode { get; init; }
    public bool Cancelled { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>Peak working set, sampled while the process ran.</summary>
    public long PeakWorkingSetBytes { get; init; }
    /// <summary>Peak committed memory from the job object (0 when unavailable).</summary>
    public long PeakCommitBytes { get; init; }
    public required IReadOnlyList<string> StdoutLines { get; init; }
    public required IReadOnlyList<string> StderrLines { get; init; }
    /// <summary>Non-transient events in arrival order.</summary>
    public required IReadOnlyList<ToolEvent> Events { get; init; }
    public string? StdoutLogPath { get; init; }
    public string? StderrLogPath { get; init; }
    public string? SdkVersion { get; init; }

    public bool HasMarker(ToolMarker marker) => Events.OfType<MarkerEvent>().Any(e => e.Marker == marker);

    public IEnumerable<string> ReportedPaths(ReportedPathKind kind) =>
        Events.OfType<PathReportedEvent>().Where(e => e.Kind == kind).Select(e => e.Path);

    public IEnumerable<string> AllLines => StdoutLines.Concat(StderrLines);
}

/// <summary>
/// Runs toolkit scripts with the bundled Python (design §4.4):
/// <c>python.exe -X utf8 -u -s -X pycache_prefix=&lt;cache&gt; &lt;script&gt; &lt;args&gt;</c>, PYTHON* variables removed,
/// one run at a time, process tree in a Job Object, raw logs kept, output parsed into events.
/// </summary>
public sealed class ToolRunner
{
    private readonly EngineLayout _layout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolRunner(EngineLayout layout) => _layout = layout;

    public EngineLayout Layout => _layout;

    public ProcessStartInfo CreateStartInfo(ToolInvocation invocation, ToolRunRequest request)
    {
        // ToolKind.Native: ScriptPath is the program itself (the Arm cross compiler); everything else runs through Python.
        var native = invocation.Tool == ToolKind.Native;
        var psi = new ProcessStartInfo(native ? invocation.ScriptPath : _layout.PythonExe)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        if (!native)
            foreach (var arg in new[] { "-X", "utf8", "-u", "-s", "-X", "pycache_prefix=" + request.PycacheDirectory, invocation.ScriptPath })
                psi.ArgumentList.Add(arg);
        foreach (var arg in invocation.Arguments)
            psi.ArgumentList.Add(arg);

        // The user's Python settings must not leak into the engine (feature/09 §6-1).
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        foreach (var (name, value) in request.Environment ?? new Dictionary<string, string>())
            psi.Environment[name] = value;
        return psi;
    }

    /// <summary>Runs one invocation after any earlier run on this runner has finished.</summary>
    /// <param name="onEvent">
    /// Called on a background thread while the runner holds its output lock, in output order.
    /// Keep it short (a UI should post to its dispatcher, not wait on it).
    /// </param>
    /// <param name="cancellationToken">Cancels a queued run (throws) or kills a running one (returns with <see cref="ToolRunResult.Cancelled"/>).</param>
    public async Task<ToolRunResult> RunAsync(ToolInvocation invocation, ToolRunRequest request,
        Action<ToolEvent>? onEvent = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(invocation, request, onEvent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ToolRunResult> RunCoreAsync(ToolInvocation invocation, ToolRunRequest request,
        Action<ToolEvent>? onEvent, CancellationToken cancellationToken)
    {
        if (invocation.Tool != ToolKind.Native && !File.Exists(_layout.PythonExe))
            throw new FileNotFoundException("The engine runtime is missing.", _layout.PythonExe);
        if (!File.Exists(invocation.ScriptPath))
            throw new FileNotFoundException("The tool script is missing.", invocation.ScriptPath);

        Directory.CreateDirectory(request.WorkingDirectory);
        Directory.CreateDirectory(request.PycacheDirectory);

        string? outLogPath = null, errLogPath = null;
        if (request.LogDirectory is not null)
        {
            Directory.CreateDirectory(request.LogDirectory);
            var name = request.LogName ?? invocation.Tool.ToString().ToLowerInvariant();
            outLogPath = Path.Combine(request.LogDirectory, name + ".out.log");
            errLogPath = Path.Combine(request.LogDirectory, name + ".err.log");
        }

        var parser = new ToolOutputParser(invocation.Tool, request.WorkingDirectory);
        var sync = new object();
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var kept = new List<ToolEvent>();
        var scratch = new List<ToolEvent>();
        long lastOutputTicks = Environment.TickCount64;
        var stalled = false;

        void Deliver(StreamKind stream, List<OutputToken> tokens)
        {
            if (tokens.Count == 0) return;
            lock (sync)
            {
                lastOutputTicks = Environment.TickCount64;
                if (stalled)
                {
                    stalled = false;
                    Publish(new ResumedEvent());
                }
                foreach (var token in tokens)
                {
                    if (token.Kind == TokenKind.Line)
                        (stream == StreamKind.Stdout ? stdoutLines : stderrLines).Add(token.Text);
                    scratch.Clear();
                    parser.Parse(stream, token, scratch);
                    foreach (var e in scratch)
                        Publish(e);
                }
            }
        }

        void Publish(ToolEvent e)
        {
            if (!e.IsTransient)
                kept.Add(e);
            try { onEvent?.Invoke(e); }
            catch { /* a listener must not break the run */ }
        }

        using var process = new Process { StartInfo = CreateStartInfo(invocation, request) };
        await using var outLog = outLogPath is null ? null : new FileStream(outLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var errLog = errLogPath is null ? null : new FileStream(errLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        // No idle sleep in the middle of a build (design §4.4). Released when the run ends.
        using var keepAwake = OperatingSystem.IsWindows() ? new PowerRequest("TOPST Toolkit: " + invocation.Title) : null;
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        process.Start();

        JobObject? job = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                job = new JobObject();
                job.Assign(process);
            }
            catch (Exception)
            {
                // Without a job the run still works; cancellation falls back to Process.Kill(entireProcessTree).
                job?.Dispose();
                job = null;
            }
        }

        try { process.StandardInput.Close(); } catch (IOException) { }

        var stdoutPump = PumpAsync(process.StandardOutput.BaseStream, outLog, StreamKind.Stdout, Deliver);
        var stderrPump = PumpAsync(process.StandardError.BaseStream, errLog, StreamKind.Stderr, Deliver);

        long peakWorkingSet = 0;
        using var monitorStop = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!monitorStop.IsCancellationRequested)
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                        peakWorkingSet = Math.Max(peakWorkingSet, process.PeakWorkingSet64);
                }
                catch (InvalidOperationException) { }

                lock (sync)
                {
                    var silence = TimeSpan.FromMilliseconds(Environment.TickCount64 - lastOutputTicks);
                    if (!stalled && silence >= request.StallTimeout)
                    {
                        stalled = true;
                        Publish(new StalledEvent(silence));
                    }
                }
                try { await Task.Delay(500, monitorStop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        });

        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (job is null || !job.Terminate(1))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Pipes close when the process (and anything that inherited them) is gone.
        var pumps = Task.WhenAll(stdoutPump, stderrPump);
        if (await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) != pumps)
            job?.Terminate(1);
        try { await pumps.ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A broken pipe loses only the tail of the output; the exit code and the raw log so far still count.
        }

        stopwatch.Stop();
        monitorStop.Cancel();
        await monitor.ConfigureAwait(false);

        var peakCommit = job?.QueryPeakProcessMemory() ?? 0;
        job?.Dispose();

        lock (sync)
        {
            return new ToolRunResult
            {
                Invocation = invocation,
                WorkingDirectory = request.WorkingDirectory,
                ExitCode = process.ExitCode,
                Cancelled = cancelled,
                StartedAt = startedAt,
                Elapsed = stopwatch.Elapsed,
                PeakWorkingSetBytes = peakWorkingSet,
                PeakCommitBytes = peakCommit,
                StdoutLines = stdoutLines.ToArray(),
                StderrLines = stderrLines.ToArray(),
                Events = kept.ToArray(),
                StdoutLogPath = outLogPath,
                StderrLogPath = errLogPath,
                SdkVersion = parser.SdkVersion,
            };
        }
    }

    /// <summary>Copies raw bytes to the log and feeds UTF-8-decoded text to the tokenizer.</summary>
    private static async Task PumpAsync(Stream source, FileStream? rawLog, StreamKind stream, Action<StreamKind, List<OutputToken>> deliver)
    {
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var tokenizer = new OutputTokenizer();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length) + 4];
        var tokens = new List<OutputToken>();

        while (true)
        {
            var read = await source.ReadAsync(bytes.AsMemory(0, bytes.Length)).ConfigureAwait(false);
            if (read == 0)
                break;
            if (rawLog is not null)
            {
                await rawLog.WriteAsync(bytes.AsMemory(0, read)).ConfigureAwait(false);
                await rawLog.FlushAsync().ConfigureAwait(false);
            }
            var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            tokens.Clear();
            tokenizer.Feed(chars.AsSpan(0, count), tokens);
            deliver(stream, tokens);
        }

        var tail = decoder.GetChars([], 0, 0, chars, 0, flush: true);
        tokens.Clear();
        tokenizer.Feed(chars.AsSpan(0, tail), tokens);
        tokenizer.Complete(tokens);
        deliver(stream, tokens);
    }
}
