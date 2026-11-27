using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TopstToolkit.Core.Components;

namespace TopstToolkit.Core.Updates;

/// <summary>What a check found: a newer version (<see cref="Manifest"/>), none, or a problem.</summary>
public sealed record UpdateCheckResult(UpdateManifest? Manifest, bool IsNewer, UpdateProblem? Problem, string? Detail = null)
{
    public static UpdateCheckResult Failed(UpdateProblem problem, string detail) => new(null, false, problem, detail);
}

/// <summary>
/// Asks the update address whether a newer version is out (design §7.5): reads <c>update.json</c> and its signature, and
/// believes it only when one of the app's keys signed it. Downloads the installer it names, over several connections and
/// resumable (<see cref="ParallelDownload"/>), and keeps it only when its size and SHA-256 are the ones signed.
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _http;
    private readonly UpdateSource _source;
    private readonly Func<int, TimeSpan>? _retryDelay;

    /// <param name="retryDelay">The pause before another try of a dropped download (tests: none).</param>
    public UpdateChecker(UpdateSource source, HttpClient? http = null, Func<int, TimeSpan>? retryDelay = null)
    {
        _source = source;
        _http = http ?? CreateHttpClient();
        _retryDelay = retryDelay;
    }

    public UpdateSource Source => _source;

    /// <summary>How long the two small files may take together before the check gives up.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        byte[] manifestBytes;
        string signature;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(CheckTimeout);
            try
            {
                manifestBytes = await GetSmallAsync(_source.ManifestUrl, UpdateManifest.MaxBytes, limit.Token).ConfigureAwait(false);
                signature = Encoding.ASCII.GetString(await GetSmallAsync(_source.SignatureUrl, 4096, limit.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return UpdateCheckResult.Failed(UpdateProblem.Offline, "no answer within " + CheckTimeout.TotalSeconds + " s");
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                return UpdateCheckResult.Failed(UpdateProblem.Offline, e.Message);
            }
        }
        if (!UpdateSignature.Verify(manifestBytes, signature, _source.Keys))
            return UpdateCheckResult.Failed(UpdateProblem.BadSignature, "update.json is not signed by a key this app trusts");
        UpdateManifest manifest;
        try
        {
            manifest = UpdateManifest.Parse(manifestBytes, allowLoopbackHttp: _source.IsLocalTest);
        }
        catch (UpdateException e)
        {
            return UpdateCheckResult.Failed(e.Problem, e.Message);
        }
        return new UpdateCheckResult(manifest, UpdateVersion.IsNewer(manifest.Version, current), null);
    }

    private async Task<byte[]> GetSmallAsync(Uri url, int maxBytes, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"{url} answered {(int)response.StatusCode}", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new HttpRequestException($"{url} is larger than {maxBytes} bytes");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new HttpRequestException($"{url} is larger than {maxBytes} bytes");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The installer of <paramref name="manifest"/> in <paramref name="folder"/>, downloaded unless it is there already.
    /// <paramref name="report"/> gets (bytes done, total), from several threads. Throws <see cref="UpdateException"/>:
    /// <see cref="UpdateProblem.Mismatch"/> (the file is deleted) or <see cref="UpdateProblem.Interrupted"/> (kept, resumes).
    /// </summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, string folder, Action<long, long> report, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var target = InstallerPath(folder, manifest);
        if (IsDownloaded(target, manifest))
        {
            report(manifest.Installer.Size, manifest.Installer.Size);
            return target;
        }
        var part = target + ".part";
        try
        {
            await ParallelDownload.DownloadAsync(_http, manifest.Installer.Url, part, manifest.Installer.Size, report, ct, _retryDelay).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateProblem.Interrupted, e.Message);
        }
        var (size, sha) = Measure(part);
        if (size != manifest.Installer.Size || !string.Equals(sha, manifest.Installer.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            ParallelDownload.DeleteState(part);
            throw new UpdateException(UpdateProblem.Mismatch, $"the installer has {size} bytes, SHA-256 {sha}; update.json says {manifest.Installer.Size}, {manifest.Installer.Sha256}");
        }
        File.Move(part, target, overwrite: true);
        // older installers are of no further use
        foreach (var old in Directory.EnumerateFiles(folder, InstallerPrefix + "*"))
            if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase) && !old.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                TryDelete(old);
        return target;
    }

    public const string InstallerPrefix = "TOPST-Toolkit-Setup-";

    public static string InstallerPath(string folder, UpdateManifest manifest) =>
        Path.Combine(folder, InstallerPrefix + UpdateVersion.Show(manifest.ParsedVersion) + ".exe");

    /// <summary>Already downloaded and still the signed file.</summary>
    public static bool IsDownloaded(string path, UpdateManifest manifest)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != manifest.Installer.Size)
            return false;
        return string.Equals(Measure(path).Sha256, manifest.Installer.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static (long Size, string Sha256) Measure(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return (file.Length, Convert.ToHexStringLower(SHA256.HashData(file)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 16 })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TOPST-Toolkit");
        return http;
    }
}

/// <summary>
/// The note the app leaves before it hands over to Setup (<c>pending.json</c> in the updates folder), read when it starts
/// again: the new version means the update went through, the old one that it did not (design §7.5).
/// </summary>
public sealed record PendingUpdate
{
    public const string FileName = "pending.json";

    [JsonPropertyName("from")] public string From { get; init; } = "";
    [JsonPropertyName("to")] public string To { get; init; } = "";
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = [];
    [JsonPropertyName("started")] public DateTimeOffset Started { get; init; }
    [JsonPropertyName("installer")] public string Installer { get; init; } = "";
    [JsonPropertyName("log")] public string? Log { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
    }

    /// <summary>
    /// The outcome of the last update, once: the note is removed as it is read. Null when there was none, or when it is more
    /// than a week old (nobody remembers it by then).
    /// </summary>
    public static UpdateOutcome? Take(string folder, Version current, DateTimeOffset? now = null)
    {
        var path = Path.Combine(folder, FileName);
        PendingUpdate? pending = null;
        try
        {
            if (File.Exists(path))
                pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(path, Encoding.UTF8), Json);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (pending is null || !UpdateVersion.TryParse(pending.To, out var to))
            return null;
        var age = (now ?? DateTimeOffset.Now) - pending.Started;
        if (age > TimeSpan.FromDays(7) || age < TimeSpan.FromMinutes(-5))
            return null;
        var succeeded = new Version(current.Major, current.Minor, Math.Max(0, current.Build)) >= to;
        if (succeeded)
        {
            // the installer did its work; the next update downloads its own
            try
            {
                if (pending.Installer.Length > 0 && File.Exists(pending.Installer))
                    File.Delete(pending.Installer);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new UpdateOutcome(succeeded, pending, age);
    }
}

/// <summary>How the last update ended, for the first start after it.</summary>
public sealed record UpdateOutcome(bool Succeeded, PendingUpdate Pending, TimeSpan Took);
