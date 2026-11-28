using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TopstToolkit.Core.Board;

/// <summary>A model folder on the board, as <c>topst-runner models</c> lists it.</summary>
public sealed class BoardModelInfo
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>"class", "obj", "custom" or "none" (read from its net.so).</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("input")] public int[] Input { get; init; } = [];
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("mtime")] public long? ModifiedUnix { get; init; }
    /// <summary>Put there by this app (in <see cref="BoardAgent.ModelRoot"/>); otherwise one of the board's own.</summary>
    [JsonPropertyName("app")] public bool ByApp { get; init; }
    [JsonPropertyName("manifest")] public BoardManifest? Manifest { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary><c>topst.json</c> in a model folder the app put on the board: what it is and the hashes of its files.</summary>
public sealed class BoardManifest
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("project")] public string Project { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("task")] public string Task { get; init; } = "";
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; init; } = [];
    [JsonPropertyName("deployed")] public string Deployed { get; init; } = "";

    /// <summary>
    /// The names the screen shows for the classes (the project's own, or its label set in Korean where the app knows the
    /// word), so the model shows names on any PC — also where its project is not in the workspace. Null in older manifests.
    /// </summary>
    [JsonPropertyName("labels")] public List<string>? Labels { get; init; }

    /// <summary>Pictures are padded to a square before the resize (the project's letterbox). Null in older manifests.</summary>
    [JsonPropertyName("letterbox")] public bool? Letterbox { get; init; }

    /// <summary>The same model, but the names or the letterbox are not what <paramref name="wanted"/> says: only the manifest goes again.</summary>
    public static bool DescribesDifferently(BoardManifest there, BoardManifest wanted) =>
        there.Letterbox != wanted.Letterbox || !(there.Labels ?? []).SequenceEqual(wanted.Labels ?? []);

    /// <summary><paramref name="wanted"/> with the time it first went to the board kept.</summary>
    public BoardManifest WithDeployed(string deployed) => new()
    {
        Schema = Schema, Project = Project, Name = Name, Model = Model, Task = Task, Files = Files, Deployed = deployed,
        Labels = Labels, Letterbox = Letterbox,
    };
}

/// <summary>What goes to the board for one project: the three files the board needs and the manifest.</summary>
public sealed record BoardDeployment(string Folder, IReadOnlyList<string> Files, BoardManifest Manifest)
{
    public long Bytes => Files.Sum(f => new FileInfo(f).Length);
}

public sealed record BoardDeployResult(string RemoteDir, bool Skipped, long Bytes);

public sealed class BoardBenchResult
{
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("avg")] public double Average { get; init; }
    [JsonPropertyName("min")] public double Min { get; init; }
    [JsonPropertyName("max")] public double Max { get; init; }
    [JsonPropertyName("median")] public double Median { get; init; }
    [JsonPropertyName("npu")] public int Npu { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>A check before a model goes to the board: the line the screen shows, and the numbers behind it.</summary>
public sealed record BoardCheck(string Text, bool Ok, string Detail);

/// <summary>One report of <c>topst-runner soak</c>: how long it ran, how many runs, the average of the last stretch.</summary>
public sealed record BoardSoakPoint(double Seconds, long Runs, double AverageMs, double Temperature);

/// <summary>
/// The app's side of the board (design §5.10): the runner it installs (<c>/usr/bin/topst-runner</c>, skipped when the
/// same version is there), the model folders it puts in <c>/usr/share/topst-toolkit</c> (skipped when the same files are
/// there), and the board's own models. Writes nowhere else on the board, except its working files in <c>/tmp</c>.
/// </summary>
public sealed class BoardAgent
{
    public const string RunnerPath = "/usr/bin/topst-runner";
    public const string ModelRoot = "/usr/share/topst-toolkit";
    public const string ManifestName = "topst.json";
    public static readonly string[] ModelFiles = ["net.so", "npu_cmd.bin", "quantized_network.bin"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public BoardAgent(SshClient ssh, string runnerScript)
    {
        Ssh = ssh;
        RunnerScript = runnerScript;
        RunnerVersion = ReadRunnerVersion(runnerScript);
    }

    public SshClient Ssh { get; }
    public string RunnerScript { get; }
    public string RunnerVersion { get; }

    /// <summary>The runner that comes with the app: <c>engine\board\topst_runner.py</c> next to the helper scripts.</summary>
    public static string RunnerScriptIn(string helpersDir) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(helpersDir))!, "board", "topst_runner.py");

    public static string ReadRunnerVersion(string script)
    {
        var match = Regex.Match(File.ReadAllText(script), "^VERSION\\s*=\\s*\"([^\"]+)\"", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : throw new InvalidDataException("no VERSION in " + script);
    }

    /// <summary>Puts the runner in <c>/usr/bin</c> unless the same version is already there. True when it was installed now.</summary>
    public async Task<bool> EnsureRunnerAsync(CancellationToken ct = default)
    {
        var current = await Ssh.RunAsync($"{RunnerPath} version 2>/dev/null || echo none", ct: ct).ConfigureAwait(false);
        if (current.StdOut.Trim() == RunnerVersion)
            return false;
        // through /tmp, then install: a half-copied runner never sits in /usr/bin
        var staging = Path.Combine(Path.GetTempPath(), "topst-runner");
        Directory.CreateDirectory(staging);
        var local = Path.Combine(staging, "topst-runner");
        File.WriteAllText(local, File.ReadAllText(RunnerScript).Replace("\r\n", "\n"), new UTF8Encoding(false));
        await Ssh.RunCheckedAsync("mkdir -p /tmp/topst-upload", ct: ct).ConfigureAwait(false);
        await Ssh.UploadAsync([local], "/tmp/topst-upload", ct).ConfigureAwait(false);
        await Ssh.RunCheckedAsync($"install -m 0755 /tmp/topst-upload/topst-runner {RunnerPath} && rm -rf /tmp/topst-upload", ct: ct).ConfigureAwait(false);
        var installed = (await Ssh.RunCheckedAsync($"{RunnerPath} version", ct: ct).ConfigureAwait(false)).Trim();
        if (installed != RunnerVersion)
            throw new BoardException(BoardFailure.Command, "보드에 실행기를 설치하지 못했어요", $"expected {RunnerVersion}, got '{installed}'");
        return true;
    }

    public async Task<IReadOnlyList<BoardModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var text = await Ssh.RunCheckedAsync($"{RunnerPath} models --root {ModelRoot}", ct: ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("models").Deserialize<List<BoardModelInfo>>(Json) ?? [];
    }

    /// <summary>The folder name of a project's model on the board: its id, which is ASCII and does not change.</summary>
    public static string FolderFor(string projectId)
    {
        var name = Regex.Replace(projectId, "[^A-Za-z0-9._-]", "-").Trim('-', '.');
        return name.Length == 0 ? throw new ArgumentException("empty project id", nameof(projectId)) : name;
    }

    public static string RemoteDirOf(string folder) => $"{ModelRoot}/{folder}";

    /// <summary>The manifest of a deployment: the hashes decide whether the board already has these files.</summary>
    public static BoardManifest ManifestFor(string projectId, string name, string model, string task, IReadOnlyList<string> files,
        IReadOnlyList<string>? labels = null, bool? letterbox = null) => new()
    {
        Project = projectId, Name = name, Model = model, Task = task,
        Files = files.ToDictionary(f => System.IO.Path.GetFileName(f), Sha256Of),
        Deployed = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
        Labels = labels?.ToList(), Letterbox = letterbox,
    };

    public static bool SameFiles(BoardManifest? a, BoardManifest b) =>
        a is not null && a.Files.Count == b.Files.Count && b.Files.All(kv => a.Files.TryGetValue(kv.Key, out var hash) && string.Equals(hash, kv.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Copies a project's model to the board unless the same files are already there.</summary>
    public async Task<BoardDeployResult> DeployAsync(BoardDeployment deployment, CancellationToken ct = default)
    {
        var remote = RemoteDirOf(deployment.Folder);
        var existing = await Ssh.RunAsync($"cat {SshClient.Quote(remote + "/" + ManifestName)} 2>/dev/null", ct: ct).ConfigureAwait(false);
        BoardManifest? there = null;
        if (existing.Ok && existing.StdOut.TrimStart().StartsWith('{'))
        {
            try { there = JsonSerializer.Deserialize<BoardManifest>(existing.StdOut, Json); }
            catch (JsonException) { there = null; }
        }
        var staging = Path.Combine(Path.GetTempPath(), "topst-board", deployment.Folder);
        Directory.CreateDirectory(staging);
        var manifest = Path.Combine(staging, ManifestName);
        if (SameFiles(there, deployment.Manifest))
        {
            if (!BoardManifest.DescribesDifferently(there!, deployment.Manifest))
                return new BoardDeployResult(remote, true, 0);
            // the same files, but the names or the letterbox are missing or changed (a manifest from before they were kept)
            File.WriteAllText(manifest, JsonSerializer.Serialize(deployment.Manifest.WithDeployed(there!.Deployed), Json), new UTF8Encoding(false));
            await Ssh.UploadAsync([manifest], remote, ct).ConfigureAwait(false);
            return new BoardDeployResult(remote, true, 0);
        }

        File.WriteAllText(manifest, JsonSerializer.Serialize(deployment.Manifest, Json), new UTF8Encoding(false));
        // the manifest goes last: a folder without it (an interrupted copy) is copied again next time
        await Ssh.RunCheckedAsync($"mkdir -p {SshClient.Quote(remote)} && rm -f {SshClient.Quote(remote + "/" + ManifestName)}", ct: ct).ConfigureAwait(false);
        await Ssh.UploadAsync(deployment.Files, remote, ct).ConfigureAwait(false);
        await Ssh.UploadAsync([manifest], remote, ct).ConfigureAwait(false);
        return new BoardDeployResult(remote, false, deployment.Bytes);
    }

    /// <summary>Deletes a model folder the app put on the board (never one of the board's own).</summary>
    public Task DeleteModelAsync(string folder, CancellationToken ct = default)
    {
        if (folder != FolderFor(folder))
            throw new ArgumentException("not a folder name of the app", nameof(folder));
        return Ssh.RunCheckedAsync($"rm -rf {SshClient.Quote(RemoteDirOf(folder))}", ct: ct);
    }

    /// <summary>
    /// The clocks the board runs its NPU at (debugfs, read only): core, AXI, APB and the NPU's CPU of cluster 0, in MHz —
    /// what the estimate of 6 성능 has to assume (Pipeline.PerfClocks). Null when the board does not say (no debugfs).
    /// </summary>
    public async Task<Pipeline.NpuClocks?> ReadClocksAsync(CancellationToken ct = default)
    {
        string[] names = ["npubus0_core", "npubus0_axi", "npubus_apb", "npubus0_cpu"];
        var result = await Ssh.RunAsync(string.Join(' ', names.Select(n => $"cat /sys/kernel/debug/clk/{n}/clk_rate")) + " 2>/dev/null",
            timeout: TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
        var hz = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => long.TryParse(l, out var v) ? v : 0).ToArray();
        return hz.Length == names.Length && hz.All(v => v > 0)
            ? new Pipeline.NpuClocks(hz[0] / 1e6, hz[1] / 1e6, hz[2] / 1e6, hz[3] / 1e6)
            : null;
    }

    /// <summary>Runs the network alone on one NPU <paramref name="count"/> times (no picture): the board's real time.</summary>
    public async Task<BoardBenchResult> BenchAsync(string modelDir, int npu, int count = 100, CancellationToken ct = default)
    {
        var text = await Ssh.RunCheckedAsync($"{RunnerPath} bench --model {SshClient.Quote(modelDir)} --npu {npu} --count {count}",
            TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "{}";
        return JsonSerializer.Deserialize<BoardBenchResult>(line, Json) ?? new BoardBenchResult { Error = "no result" };
    }

    /// <summary>
    /// Runs the network without a pause for <paramref name="duration"/> (the board heats up as it would in use) and
    /// reports every <paramref name="every"/>: runs so far, the average time, the hottest sensor. Stopping ends it on the board too.
    /// </summary>
    public async Task<IReadOnlyList<BoardSoakPoint>> SoakAsync(string modelDir, int npu, TimeSpan duration, TimeSpan every,
        Action<BoardSoakPoint> onPoint, CancellationToken ct = default)
    {
        var seconds = duration.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        var step = every.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        using var process = Ssh.Start($"{RunnerPath} soak --model {SshClient.Quote(modelDir)} --npu {npu} --seconds {seconds} --every {step}");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var points = new List<BoardSoakPoint>();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!line.TrimStart().StartsWith('{'))
                    continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error))
                    throw new BoardException(BoardFailure.Command, "보드에서 모델을 돌리지 못했어요", error.GetString());
                if (root.TryGetProperty("done", out _))
                    break;
                var point = new BoardSoakPoint(root.GetProperty("t").GetDouble(), root.GetProperty("runs").GetInt64(),
                    root.GetProperty("avg").GetDouble(), root.GetProperty("temp").GetDouble());
                points.Add(point);
                onPoint(point);
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (process.ExitCode == 255 && points.Count == 0 && !ct.IsCancellationRequested)
            throw SshClient.Classify(await stderr.ConfigureAwait(false));
        return points;
    }

    /// <summary>Removes the app's working files in /tmp (not the models, not the runner).</summary>
    public Task CleanTempAsync(CancellationToken ct = default) =>
        Ssh.RunCheckedAsync("rm -rf /tmp/topst-runner /tmp/topst-upload", ct: ct);

    public Task RebootAsync(CancellationToken ct = default) =>
        Ssh.RunAsync("(sleep 1; reboot) >/dev/null 2>&1 &", ct: ct);

    /// <summary>
    /// Before a model goes to the board: the NPU's reserved memory (the network's buffers, from the compiler's
    /// network_buf.txt), the board's storage and its NPU library.
    /// </summary>
    public static IReadOnlyList<BoardCheck> Checks(BoardStatus status, long npuBytesNeeded, long filesBytes, bool alreadyThere)
    {
        var memoryOk = npuBytesNeeded <= 0 || npuBytesNeeded <= status.NpuMemory.Free;
        var diskOk = alreadyThere || filesBytes <= status.Disk.Free;
        var libraryOk = status.HasNpuLibrary != false;
        var devicesOk = status.NpuDevices.Length == 0 || status.NpuDevices.All(d => d);
        return
        [
            new(memoryOk ? "NPU용 메모리 충분" : "NPU용 메모리 부족", memoryOk,
                npuBytesNeeded > 0 ? $"{Mb(npuBytesNeeded)} 필요 · {Mb(status.NpuMemory.Free)} 남음" : $"{Mb(status.NpuMemory.Free)} 남음"),
            new(diskOk ? "저장 공간 충분" : "저장 공간 부족", diskOk,
                alreadyThere ? "같은 모델이 이미 있어요" : $"{Mb(filesBytes)} 필요 · {Mb(status.Disk.Free)} 남음"),
            new(libraryOk && devicesOk ? "보드 소프트웨어 호환" : !libraryOk ? "NPU 라이브러리가 없어요" : "NPU 장치가 없어요", libraryOk && devicesOk,
                "libtcndnpu.so.1 · /dev/npu0 · /dev/npu1"),
        ];
    }

    public static string Mb(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString(bytes < 10L << 20 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
