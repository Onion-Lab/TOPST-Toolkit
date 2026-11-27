using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopstToolkit.Core.Components;

/// <summary>
/// One file over several connections at once (HTTP Range requests), in chunks that the connections take in turn, so one
/// slow connection does not hold the others up. The chunks that are complete are noted next to the file
/// (<c>&lt;file&gt;.json</c>): a download stopped by a cancel, a lost connection or a closed app goes on where it was the next
/// time. A server that does not answer range requests gets one plain download, as before (design D12, 2026-11-25).
/// </summary>
internal static class ParallelDownload
{
    /// <summary>What one request asks for; also what a resumed download may fetch twice per connection.</summary>
    public const long ChunkBytes = 8L << 20;

    /// <summary>Connections per file. Six is what browsers open per server, and what the official servers expect.</summary>
    public const int Connections = 6;

    /// <summary>Smaller files come in one request: more connections would not make them noticeably faster.</summary>
    public const long MinParallelBytes = 32L << 20;

    /// <summary>Tries per chunk; a dropped connection or a server hiccup is tried again after a pause.</summary>
    private const int Attempts = 6;

    /// <summary>The pause before another try: 2 s, 4 s, ... up to 20 s.</summary>
    internal static TimeSpan DefaultDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(20, 2 * attempt));

    /// <summary>Downloads <paramref name="url"/> into <paramref name="part"/>; <paramref name="report"/> gets (bytes done, total), from several threads.</summary>
    /// <param name="expectedBytes">The size the catalog gives; 0 when unknown.</param>
    /// <param name="retryDelay">The pause before try n+1 (tests: none).</param>
    public static async Task DownloadAsync(HttpClient http, string url, string part, long expectedBytes, Action<long, long> report, CancellationToken ct,
        Func<int, TimeSpan>? retryDelay = null)
    {
        retryDelay ??= DefaultDelay;
        if (expectedBytes >= MinParallelBytes && await ProbeAsync(http, url, retryDelay, ct).ConfigureAwait(false) is { } total)
        {
            try
            {
                await ChunkedAsync(http, url, part, total, report, retryDelay, ct).ConfigureAwait(false);
                return;
            }
            catch (RangeNotHonoredException)
            {
                // a server (or a proxy on the way) that stopped answering ranges halfway: start over in one piece
                DeleteState(part);
            }
        }
        await SingleAsync(http, url, part, expectedBytes, report, retryDelay, ct).ConfigureAwait(false);
    }

    /// <summary>The file's length when the server answers a range request with exactly that range; null otherwise.</summary>
    internal static async Task<long?> ProbeAsync(HttpClient http, string url, Func<int, TimeSpan> retryDelay, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { From: 0, Length: > 0 and var length })
                    return length;
                if (!Transient(response.StatusCode) || attempt >= 3)
                    return null; // the plain download says what is wrong, if anything is
            }
            catch (HttpRequestException) when (attempt < 3)
            {
            }
            catch (HttpRequestException)
            {
                return null;
            }
            await Task.Delay(retryDelay(attempt), ct).ConfigureAwait(false);
        }
    }

    private static async Task SingleAsync(HttpClient http, string url, string part, long expectedBytes, Action<long, long> report,
        Func<int, TimeSpan> retryDelay, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? expectedBytes;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                report(0, total);
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    report(done, total);
                }
                return;
            }
            catch (Exception e) when (IsTransient(e, ct) && attempt < Attempts)
            {
                await Task.Delay(retryDelay(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Worth another try: the connection dropped, the server was busy or failed for a moment (5xx, 408, 429).</summary>
    private static bool IsTransient(Exception e, CancellationToken ct) =>
        !ct.IsCancellationRequested && e is not RangeNotHonoredException
        && (e is IOException || (e is HttpRequestException http && (http.StatusCode is null || Transient(http.StatusCode.Value))));

    private static bool Transient(HttpStatusCode status) =>
        status is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static async Task ChunkedAsync(HttpClient http, string url, string part, long total, Action<long, long> report,
        Func<int, TimeSpan> retryDelay, CancellationToken ct)
    {
        var count = (int)((total + ChunkBytes - 1) / ChunkBytes);
        var state = ChunkState.Load(StatePath(part), url, total, count);
        if (state is null || !File.Exists(part) || new FileInfo(part).Length != total)
            state = new ChunkState { Url = url, Total = total, Done = new string('0', count) };
        var done = new bool[count];
        for (var i = 0; i < count; i++)
            done[i] = state.Done[i] == '1';
        var pending = Enumerable.Range(0, count).Where(i => !done[i]).ToArray();
        long bytes = Enumerable.Range(0, count).Where(i => done[i]).Sum(i => LengthOf(i, total));
        report(bytes, total);

        using var handle = File.OpenHandle(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        if (RandomAccess.GetLength(handle) != total)
            RandomAccess.SetLength(handle, total);
        var gate = new object();
        var saved = Environment.TickCount64;
        void Save(bool now)
        {
            lock (gate)
            {
                if (!now && Environment.TickCount64 - saved < 1000)
                    return;
                saved = Environment.TickCount64;
                state.Done = new string([.. done.Select(d => d ? '1' : '0')]);
                state.Save(StatePath(part));
            }
        }

        var cursor = -1;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Worker()
        {
            var buffer = new byte[1 << 20];
            while (Interlocked.Increment(ref cursor) is var k && k < pending.Length)
            {
                var index = pending[k];
                await ChunkAsync(http, url, handle, index * ChunkBytes, index * ChunkBytes + LengthOf(index, total) - 1, buffer,
                    n => report(Interlocked.Add(ref bytes, n), total), retryDelay, stop.Token).ConfigureAwait(false);
                lock (gate)
                    done[index] = true;
                Save(now: false);
            }
        }

        var workers = Enumerable.Range(0, Math.Min(Connections, Math.Max(1, pending.Length))).Select(_ => Task.Run(Worker, stop.Token)).ToList();
        try
        {
            // the first failure stops the other connections too
            while (workers.Count > 0)
            {
                var finished = await Task.WhenAny(workers).ConfigureAwait(false);
                workers.Remove(finished);
                if (finished.IsFaulted || finished.IsCanceled)
                {
                    stop.Cancel();
                    await Task.WhenAll(workers.Select(w => w.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
                    await finished.ConfigureAwait(false); // rethrows
                }
            }
        }
        finally
        {
            Save(now: true);
        }
        DeleteState(part);
    }

    private static long LengthOf(int index, long total) => Math.Min(ChunkBytes, total - index * ChunkBytes);

    /// <summary>Bytes <paramref name="first"/> to <paramref name="last"/> written at their place in the file, tried again where a connection dropped.</summary>
    private static async Task ChunkAsync(HttpClient http, string url, Microsoft.Win32.SafeHandles.SafeFileHandle handle, long first, long last, byte[] buffer,
        Action<long> wrote, Func<int, TimeSpan> retryDelay, CancellationToken ct)
    {
        var at = first;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(at, last);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                    throw new RangeNotHonoredException();
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != at)
                    throw new RangeNotHonoredException();
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int read;
                while (at <= last && (read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    var take = (int)Math.Min(read, last + 1 - at); // never past the chunk, whatever the server sends
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, take), at, ct).ConfigureAwait(false);
                    at += take;
                    wrote(take);
                }
                if (at <= last)
                    throw new IOException($"the connection closed {last + 1 - at} bytes before the end of the chunk");
                return;
            }
            catch (Exception e) when (IsTransient(e, ct) && attempt < Attempts)
            {
                await Task.Delay(retryDelay(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    internal static string StatePath(string part) => part + ".json";

    internal static void DeleteState(string part)
    {
        try
        {
            File.Delete(StatePath(part));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class RangeNotHonoredException() : IOException("the server stopped answering range requests");

    /// <summary><c>&lt;file&gt;.part.json</c>: which chunks of which download are in the file already.</summary>
    internal sealed class ChunkState
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("total")] public long Total { get; set; }
        [JsonPropertyName("chunk")] public long Chunk { get; set; } = ChunkBytes;
        /// <summary>One character per chunk: '1' when it is complete.</summary>
        [JsonPropertyName("done")] public string Done { get; set; } = "";

        public static ChunkState? Load(string path, string url, long total, int count)
        {
            try
            {
                var state = File.Exists(path) ? JsonSerializer.Deserialize<ChunkState>(File.ReadAllText(path)) : null;
                return state is not null && state.Url == url && state.Total == total && state.Chunk == ChunkBytes && state.Done.Length == count ? state : null;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Save(string path)
        {
            try
            {
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // only the resume is lost
            }
        }
    }
}
