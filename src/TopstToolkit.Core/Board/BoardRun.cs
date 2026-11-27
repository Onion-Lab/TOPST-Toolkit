using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TopstToolkit.Engine;

namespace TopstToolkit.Core.Board;

/// <summary>A box the board found, in the pixels of the network input (as the runner reports it).</summary>
/// <param name="ClassNumber">The network's class number (YOLO and SSD count from 1: the background's place).</param>
/// <param name="Score">0..1.</param>
public sealed record BoardBox(double X1, double Y1, double X2, double Y2, int ClassNumber, double Score);

/// <summary>The board's answer for one frame.</summary>
public sealed record BoardFrameResult(int Seq, double NpuMs, double PostMs, double WallMs, IReadOnlyList<BoardBox> Boxes, int? ClassId, string? Error)
{
    public static BoardFrameResult? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("seq", out var seqProp))
            return null;
        var boxes = new List<BoardBox>();
        if (root.TryGetProperty("objs", out var objs))
            foreach (var o in objs.EnumerateArray())
                boxes.Add(new BoardBox(o[0].GetDouble(), o[1].GetDouble(), o[2].GetDouble(), o[3].GetDouble(), o[4].GetInt32(), o[5].GetDouble()));
        double Get(string name) => root.TryGetProperty(name, out var p) ? p.GetDouble() : 0;
        return new BoardFrameResult(seqProp.GetInt32(), Get("ms"), Get("post"), Get("wall"), boxes,
            root.TryGetProperty("cls", out var cls) ? cls.GetInt32() : null,
            root.TryGetProperty("error", out var error) ? error.GetString() : null);
    }
}

/// <summary>Facts of one frame from <c>board_frames.py</c>: the original's size and the padding added before the resize.</summary>
public sealed class FrameInfo
{
    [JsonPropertyName("i")] public int Index { get; init; }
    [JsonPropertyName("src")] public string? Source { get; init; }
    [JsonPropertyName("t")] public double Seconds { get; init; }
    [JsonPropertyName("W")] public int OriginalWidth { get; init; }
    [JsonPropertyName("H")] public int OriginalHeight { get; init; }
    /// <summary>Left, top, right, bottom (letterbox).</summary>
    [JsonPropertyName("pad")] public int[] Pad { get; init; } = [0, 0, 0, 0];
    [JsonPropertyName("w")] public int Width { get; init; }
    [JsonPropertyName("h")] public int Height { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("end")] public bool End { get; init; }
    [JsonPropertyName("frames")] public int Frames { get; init; }
    [JsonPropertyName("video")] public VideoInfo? Video { get; init; }

    /// <summary>A box of the network input in the original picture's pixels (letterbox undone), clamped to the picture.</summary>
    public BoardBox ToOriginal(BoardBox box)
    {
        double padL = Pad.ElementAtOrDefault(0), padT = Pad.ElementAtOrDefault(1), padR = Pad.ElementAtOrDefault(2), padB = Pad.ElementAtOrDefault(3);
        var sx = (OriginalWidth + padL + padR) / Math.Max(1, Width);
        var sy = (OriginalHeight + padT + padB) / Math.Max(1, Height);
        double X(double v) => Math.Clamp(v * sx - padL, 0, OriginalWidth);
        double Y(double v) => Math.Clamp(v * sy - padT, 0, OriginalHeight);
        return box with { X1 = X(box.X1), Y1 = Y(box.Y1), X2 = X(box.X2), Y2 = Y(box.Y2) };
    }
}

public sealed class VideoInfo
{
    [JsonPropertyName("src")] public string Source { get; init; } = "";
    [JsonPropertyName("W")] public int Width { get; init; }
    [JsonPropertyName("H")] public int Height { get; init; }
    [JsonPropertyName("fps")] public double Fps { get; init; }
    [JsonPropertyName("frames")] public int Frames { get; init; }
    [JsonPropertyName("every")] public int Every { get; init; } = 1;
}

public sealed record FrameRecord(FrameInfo Info, byte[] Rgb, byte[] Preview);

/// <summary>
/// Pictures or a video's frames prepared for the network on the PC (<c>engine\helpers\board_frames.py</c>, the
/// toolkit's own letterbox and resize), read from the helper's binary output.
/// </summary>
public sealed class FrameSource : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _out;
    private readonly Task<string> _stderr;

    private FrameSource(EngineLayout layout, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(layout.PythonExe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var arg in new[] { "-X", "utf8", "-u", "-I", layout.HelperScript("board_frames.py") }.Concat(arguments))
            psi.ArgumentList.Add(arg);
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        _process = Process.Start(psi)!;
        _process.StandardInput.Close();
        _out = _process.StandardOutput.BaseStream;
        _stderr = _process.StandardError.ReadToEndAsync();
    }

    public static FrameSource Images(EngineLayout layout, IReadOnlyList<string> files, int width, int height, bool letterbox) =>
        new(layout, Common("images", width, height, letterbox).Concat(files));

    public static FrameSource Video(EngineLayout layout, string video, int width, int height, bool letterbox, int every, int previewSize) =>
        new(layout, Common("video", width, height, letterbox).Concat(["--video", video, "--every", Inv(every), "--preview", Inv(previewSize)]));

    /// <summary>A video's size, frame rate and length, read before it is run; throws when it cannot be read.</summary>
    public static async Task<VideoInfo> ProbeAsync(EngineLayout layout, string video, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(layout.PythonExe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-X", "utf8", "-I", layout.HelperScript("board_frames.py"), "probe", video })
            psi.ArgumentList.Add(arg);
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var text = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (process.ExitCode != 0 || !line.TrimStart().StartsWith('{') || line.Contains("\"error\"", StringComparison.Ordinal))
            throw new InvalidDataException("영상을 열지 못했어요: " + (line.Length > 0 ? line : (await stderr.ConfigureAwait(false)).Trim()));
        return JsonSerializer.Deserialize<VideoInfo>(line) ?? throw new InvalidDataException("영상을 열지 못했어요");
    }

    private static IEnumerable<string> Common(string mode, int width, int height, bool letterbox) =>
        new[] { mode, "--w", Inv(width), "--h", Inv(height) }.Concat(letterbox ? ["--letterbox"] : []);

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The next record, or null when the helper ended.</summary>
    public async Task<FrameRecord?> NextAsync(CancellationToken ct = default)
    {
        var head = new byte[8];
        if (!await ReadExactAsync(head, ct).ConfigureAwait(false))
            return null;
        if (head[0] != (byte)'T' || head[1] != (byte)'B' || head[2] != (byte)'F' || head[3] != (byte)'1')
            throw new InvalidDataException("board_frames.py: bad record");
        var json = new byte[BitConverter.ToInt32(head, 4)];
        await ReadExactAsync(json, ct).ConfigureAwait(false);
        var info = JsonSerializer.Deserialize<FrameInfo>(json) ?? new FrameInfo();
        var data = await ReadBlockAsync(ct).ConfigureAwait(false);
        var preview = await ReadBlockAsync(ct).ConfigureAwait(false);
        return new FrameRecord(info, data, preview);
    }

    public Task<string> ErrorsAsync() => _stderr;

    private async Task<byte[]> ReadBlockAsync(CancellationToken ct)
    {
        var size = new byte[4];
        await ReadExactAsync(size, ct).ConfigureAwait(false);
        var data = new byte[BitConverter.ToInt32(size, 0)];
        if (data.Length > 0)
            await ReadExactAsync(data, ct).ConfigureAwait(false);
        return data;
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _out.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                    return false;
                throw new EndOfStreamException("board_frames.py stopped in the middle of a record");
            }
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }
}

/// <summary>
/// One network loaded on one NPU of the board (<c>topst-runner serve</c>) for as long as the session lasts: frames go
/// in over the SSH connection, one result line comes back per frame.
/// </summary>
public sealed class BoardRunSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _in;
    private readonly Task<string> _stderr;

    private BoardRunSession(Process process)
    {
        _process = process;
        _in = process.StandardInput.BaseStream;
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>"obj", "class", "custom" or "none".</summary>
    public string Type { get; private set; } = "";
    public int Npu { get; private set; }

    public static async Task<BoardRunSession> StartAsync(SshClient ssh, string modelDir, int npu, CancellationToken ct = default)
    {
        var process = ssh.Start($"{BoardAgent.RunnerPath} serve --model {SshClient.Quote(modelDir)} --npu {npu}");
        var session = new BoardRunSession(process);
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                var text = await session._stderr.ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                throw process.ExitCode == 255 ? SshClient.Classify(text)
                    : new BoardException(BoardFailure.Command, "보드에서 모델을 올리지 못했어요", text.Trim());
            }
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new BoardException(BoardFailure.Command, ReadyError(root.TryGetProperty("stage", out var s) ? s.GetString() : null), error.GetString());
            session.Width = root.GetProperty("w").GetInt32();
            session.Height = root.GetProperty("h").GetInt32();
            session.Type = root.GetProperty("type").GetString() ?? "";
            session.Npu = root.GetProperty("npu").GetInt32();
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string ReadyError(string? stage) => stage switch
    {
        "files" => "보드에 모델 파일이 없어요",
        "npu" => "보드의 NPU를 열지 못했어요. 다른 프로그램이 쓰고 있을 수 있어요",
        "load" => "보드에서 모델을 올리지 못했어요",
        "buffer" => "보드의 NPU용 메모리가 모자라요",
        _ => "보드에서 실행하지 못했어요",
    };

    /// <summary>Sends one frame: packed RGB of exactly <see cref="Width"/> x <see cref="Height"/>.</summary>
    public async Task SendAsync(int seq, byte[] rgb, CancellationToken ct = default)
    {
        var header = new byte[16];
        "TPF1"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header.AsSpan(4), (uint)seq);
        BitConverter.TryWriteBytes(header.AsSpan(8), (uint)rgb.Length);
        await _in.WriteAsync(header, ct).ConfigureAwait(false);
        await _in.WriteAsync(rgb, ct).ConfigureAwait(false);
        await _in.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The next result line, or null when the runner ended.</summary>
    public async Task<BoardFrameResult?> ReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                return null;
            if (line.Contains("\"done\"", StringComparison.Ordinal))
                return null;
            if (BoardFrameResult.Parse(line) is { } result)
                return result;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                var stop = new byte[16];
                "TPF1"u8.CopyTo(stop);
                BitConverter.TryWriteBytes(stop.AsSpan(12), 1u);
                try
                {
                    await _in.WriteAsync(stop).ConfigureAwait(false);
                    await _in.FlushAsync().ConfigureAwait(false);
                    _in.Close();
                }
                catch (IOException)
                {
                }
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }
}

/// <summary>One frame done: where it came from, what the board found (in the original's pixels) and how long it took.</summary>
public sealed record BoardRunFrame(FrameInfo Info, byte[] Preview, BoardFrameResult Result, IReadOnlyList<BoardBox> Boxes);

/// <summary>Numbers of a whole run.</summary>
public sealed record BoardRunSummary(int Frames, double AverageNpuMs, double FramesPerSecond, TimeSpan Elapsed, int Width, int Height, string Type);

/// <summary>Runs pictures or a video through a model on the board: prepare on the PC, two frames in flight, results back.</summary>
public sealed class BoardRunner(SshClient ssh, EngineLayout layout)
{
    /// <param name="video">A video file; null when <paramref name="images"/> are given.</param>
    /// <param name="every">Video: send every n-th frame.</param>
    public async Task<BoardRunSummary> RunAsync(string modelDir, int npu, bool letterbox, IReadOnlyList<string>? images, string? video, int every,
        Action<BoardRunFrame> onFrame, Action<VideoInfo>? onVideo = null, CancellationToken ct = default)
    {
        await using var session = await BoardRunSession.StartAsync(ssh, modelDir, npu, ct).ConfigureAwait(false);
        await using var source = video is null
            ? FrameSource.Images(layout, images ?? [], session.Width, session.Height, letterbox)
            : FrameSource.Video(layout, video, session.Width, session.Height, letterbox, every, previewSize: 960);
        var watch = Stopwatch.StartNew();
        var pending = Channel.CreateBounded<FrameRecord>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait });
        var frames = 0;
        var npuMs = 0.0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = Task.Run(async () =>
        {
            var seq = 0;
            try
            {
                while (await source.NextAsync(linked.Token).ConfigureAwait(false) is { } record)
                {
                    if (record.Info.Video is { } info)
                    {
                        onVideo?.Invoke(info);
                        continue;
                    }
                    if (record.Info.End)
                        break;
                    if (record.Info.Error is not null || record.Rgb.Length == 0)
                        continue;
                    await pending.Writer.WriteAsync(record, linked.Token).ConfigureAwait(false);   // waits while two are on the way
                    await session.SendAsync(seq++, record.Rgb, linked.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                pending.Writer.TryComplete();
            }
        }, linked.Token);

        try
        {
            await foreach (var record in pending.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                var result = await session.ReadAsync(linked.Token).ConfigureAwait(false)
                    ?? throw new BoardException(BoardFailure.Command, "보드의 실행기가 도중에 멈췄어요");
                frames++;
                npuMs += result.NpuMs;
                onFrame(new BoardRunFrame(record.Info, record.Preview, result, result.Boxes.Select(record.Info.ToOriginal).ToList()));
            }
            await send.ConfigureAwait(false);
        }
        catch
        {
            linked.Cancel();
            try { await send.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (BoardException) { }
            throw;
        }
        watch.Stop();
        return new BoardRunSummary(frames, frames == 0 ? 0 : npuMs / frames, frames / Math.Max(watch.Elapsed.TotalSeconds, 1e-6),
            watch.Elapsed, session.Width, session.Height, session.Type);
    }
}

/// <summary>Writes the video with the board's boxes drawn on it (<c>engine\helpers\board_video.py</c>) and the CSV table.</summary>
public static class BoardVideoOutput
{
    /// <summary>The results of a run as the helper reads them: one JSON line per frame, boxes in the video's pixels.</summary>
    public static void WriteResults(string path, IEnumerable<BoardRunFrame> frames)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var f in frames)
        {
            var objs = f.Boxes.Select(b => new object[] { Math.Round(b.X1, 1), Math.Round(b.Y1, 1), Math.Round(b.X2, 1), Math.Round(b.Y2, 1), b.ClassNumber, Math.Round(b.Score, 4) });
            writer.WriteLine(JsonSerializer.Serialize(new { i = f.Info.Index, objs }));
        }
    }

    public static void WriteCsv(string path, IEnumerable<BoardRunFrame> frames, Func<int, string> nameOf)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("frame,seconds,class,name,score,x1,y1,x2,y2,npu_ms");
        foreach (var f in frames)
        {
            var at = f.Info.Seconds.ToString("0.000", CultureInfo.InvariantCulture);
            var ms = f.Result.NpuMs.ToString("0.000", CultureInfo.InvariantCulture);
            if (f.Boxes.Count == 0)
                writer.WriteLine($"{f.Info.Index},{at},,,,,,,,{ms}");
            foreach (var b in f.Boxes)
                writer.WriteLine(string.Join(',', f.Info.Index, at, b.ClassNumber, Csv(nameOf(b.ClassNumber)),
                    b.Score.ToString("0.0000", CultureInfo.InvariantCulture), F(b.X1), F(b.Y1), F(b.X2), F(b.Y2), ms));
        }
    }

    private static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Csv(string text) => text.Contains(',') || text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    /// <summary>Runs the helper; progress gets (frames done, frames in all).</summary>
    public static async Task WriteVideoAsync(EngineLayout layout, string video, string resultsJsonl, IReadOnlyDictionary<int, string> labels,
        string font, string output, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var labelsPath = Path.ChangeExtension(resultsJsonl, ".labels.json");
        File.WriteAllText(labelsPath, JsonSerializer.Serialize(labels.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value)), new UTF8Encoding(false));
        var psi = new ProcessStartInfo(layout.PythonExe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-X", "utf8", "-u", "-I", layout.HelperScript("board_video.py"), "--video", video, "--results", resultsJsonl,
                     "--labels", labelsPath, "--font", font, "--out", output })
            psi.ArgumentList.Add(arg);
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(ct);
        string? error = null;
        while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("error", out var e))
                error = e.GetString();
            else if (doc.RootElement.TryGetProperty("done", out var done))
                progress?.Invoke(done.GetInt32(), doc.RootElement.TryGetProperty("total", out var total) ? total.GetInt32() : 0);
        }
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0 || error is not null)
            throw new IOException("결과 영상을 만들지 못했어요: " + (error ?? (await stderr.ConfigureAwait(false)).Trim()));
    }

    /// <summary>A font with Korean letters for the labels: Malgun Gothic, which Windows has.</summary>
    public static string? KoreanFont()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return new[] { "malgunbd.ttf", "malgun.ttf", "gulim.ttc" }.Select(f => Path.Combine(fonts, f)).FirstOrDefault(File.Exists);
    }
}
