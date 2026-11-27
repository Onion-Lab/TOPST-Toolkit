using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TopstToolkit.Engine;
using TopstToolkit.Engine.Board;
using TopstToolkit.Engine.Commands;
using TopstToolkit.Engine.Runner;

namespace TopstToolkit.Core.Components;

/// <summary>
/// Where an install is. <see cref="Key"/> names the stage (<c>download:&lt;id&gt;</c>, <c>python</c>, <c>packages</c>, <c>extract</c>,
/// <c>smoke</c> ... see <see cref="ComponentSteps"/>), <see cref="Step"/> says it in words. <see cref="Done"/> and
/// <see cref="Total"/> are bytes while a file is being downloaded.
/// </summary>
public sealed record ComponentProgress(string Component, string Key, string Step, long? Done = null, long? Total = null)
{
    public double? Fraction => Total is > 0 && Done is { } done ? Math.Clamp((double)done / Total.Value, 0, 1) : null;

    /// <summary>A download of a file of the catalog: it may run ahead of the install, side by side with other stages.</summary>
    public bool IsDownload => Key.StartsWith("download:", StringComparison.Ordinal);

    /// <summary>
    /// Told by a download that runs ahead of the install (<see cref="ComponentInstaller.PrefetchAsync"/>): it moves its
    /// own line, not the place where the install is.
    /// </summary>
    public bool Ahead { get; init; }
}

/// <summary>A downloaded file whose SHA-256 is not the one of the catalog: it is never used (the official file may have changed).</summary>
public sealed class ComponentDownloadMismatchException(DownloadSpec download, string actualSha256)
    : IOException($"{download.FileName} does not have the SHA-256 the catalog gives (got {actualSha256}): the official file may have changed")
{
    public DownloadSpec Download { get; } = download;
    public string ActualSha256 { get; } = actualSha256;
}

public sealed class ComponentInstallResult
{
    public required string Component { get; init; }
    public bool Success => Error is null;
    public bool Cancelled { get; init; }
    /// <summary>What went wrong, in English (for the log and the details line); null on success.</summary>
    public string? Error { get; init; }
    public string? Folder { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Smoke { get; init; }
    public string LogDirectory { get; init; } = "";
    /// <summary>The stage the install was in when it stopped (<see cref="ComponentProgress.Key"/>).</summary>
    public string? FailedKey { get; init; }
    /// <summary>Set when a downloaded file did not have the catalog's SHA-256.</summary>
    public DownloadSpec? Mismatch { get; init; }
    public string? ActualSha256 { get; init; }
}

/// <summary>A line of a pip lock file: <c>name==version --hash=sha256:…</c>.</summary>
public sealed record LockedPackage(string Name, string Version, string Sha256);

/// <summary>
/// Installs the components of the catalog into a components root (design D12), the way <c>build\build-runtime.ps1</c> and
/// <c>build\build-toolchain.ps1</c> built them on the development PC: every file from its official source, checked
/// against the catalog's SHA-256 before it is used, assembled in <c>&lt;folder&gt;.partial</c>, checked with a smoke
/// test, and only then put in place — an install that fails or is cancelled leaves nothing half-done behind.
/// Downloads can start before the installs need them (<see cref="PrefetchAsync"/>): a big file over several connections,
/// the Python packages four at a time, and an install that gets to a file joins its download (2026-11-25).
/// </summary>
public sealed class ComponentInstaller
{
    private readonly ComponentCatalog _catalog;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _lookup;
    private readonly string _appVersion;
    private readonly object _jobsGate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Downloads a prefetch tried and that failed: the install that needs the file says so, instead of downloading it again.</summary>
    private readonly ConcurrentDictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files hashed in this process (path → length, time written, SHA-256, MD5): a file checked once is not read again.</summary>
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Sha256, string? Md5)> _verified = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="root">The components root (<see cref="ComponentsRoot"/>).</param>
    /// <param name="lookupDirectories">Folders searched for a file of the right name and hash before downloading it
    /// (development: <c>artifacts\cache</c>; offline installs).</param>
    /// <param name="pipCacheDirectory">pip's own cache; <c>&lt;root&gt;\cache\pip</c> when null.</param>
    public ComponentInstaller(ComponentCatalog catalog, string root, IEnumerable<string>? lookupDirectories = null,
        string? pipCacheDirectory = null, HttpClient? http = null, string appVersion = "")
    {
        _catalog = catalog;
        Root = Path.GetFullPath(root);
        _lookup = [.. (lookupDirectories ?? []).Select(Path.GetFullPath)];
        PipCacheDirectory = pipCacheDirectory ?? Path.Combine(Root, "cache", "pip");
        _http = http ?? CreateHttpClient();
        _appVersion = appVersion;
    }

    public ComponentCatalog Catalog => _catalog;
    public string Root { get; }
    /// <summary>Downloaded files are kept here, so that installing again does not download them again.</summary>
    public string DownloadsDirectory => Path.Combine(Root, "downloads");
    /// <summary>The Python packages of the lock files, fetched before pip runs (<see cref="FetchWheelsAsync"/>).</summary>
    public string WheelsDirectory => Path.Combine(DownloadsDirectory, "wheels");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string PipCacheDirectory { get; }
    private string CacheDirectory => Path.Combine(Root, "cache");

    /// <summary>PyPI's JSON API (<c>&lt;base&gt;&lt;name&gt;/&lt;version&gt;/json</c>); tests point it elsewhere.</summary>
    internal string PypiJsonBase { get; init; } = "https://pypi.org/pypi/";

    /// <summary>The pause before a download is tried again (tests: none).</summary>
    internal Func<int, TimeSpan>? RetryDelay { get; init; }

    public string FolderOf(string id) => Path.Combine(Root, _catalog.Get(id).Folder);

    public ComponentState StateOf(string id)
    {
        var spec = _catalog.Get(id);
        var folder = FolderOf(id);
        if (!Directory.Exists(folder))
            return ComponentState.Missing;
        return spec.RequiredFiles.All(f => Exists(Path.Combine(folder, f))) ? ComponentState.Installed : ComponentState.Broken;
    }

    /// <summary>
    /// Installed from the files the catalog names now. An update of the app can bring another toolkit or other packages;
    /// Setup installs a component again only then. A component installed some other way (no record) counts as current.
    /// </summary>
    public bool IsCurrent(string id)
    {
        if (StateOf(id) != ComponentState.Installed)
            return false;
        if (InstalledComponent.Read(FolderOf(id)) is not { } record)
            return true;
        var spec = _catalog.Get(id);
        if (!spec.Downloads.All(d => record.Downloads.TryGetValue(d.Id, out var sha) && Same(sha, d.Sha256)))
            return false;
        return record.Locks is null || LockHashes(spec).All(l => record.Locks.TryGetValue(l.Key, out var sha) && Same(sha, l.Value));
    }

    /// <summary>
    /// Whether this process can write into <see cref="Root"/>. Not when the app was installed for all users under
    /// Program Files and runs without administrator rights: installs then go through an elevated helper (design §7.2).
    /// </summary>
    public bool CanWrite()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var probe = Path.Combine(Root, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes an installed component. Fails (IOException) while one of its programs is running.</summary>
    public void Remove(string id)
    {
        var folder = FolderOf(id);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>Frees the space the downloaded files take (they are only needed to install again).</summary>
    public void DeleteDownloads()
    {
        if (Directory.Exists(DownloadsDirectory))
            Directory.Delete(DownloadsDirectory, recursive: true);
        _verified.Clear();
    }

    /// <summary>What the downloads and the caches (pip's, the smoke tests') take; only needed to install again.</summary>
    public long KeptBytes() => SizeOf(DownloadsDirectory) + SizeOf(CacheDirectory);

    /// <summary>Removes the downloads and the caches. The installed components stay.</summary>
    public void DeleteKeptFiles()
    {
        DeleteDownloads();
        if (Directory.Exists(CacheDirectory))
            Directory.Delete(CacheDirectory, recursive: true);
    }

    private static long SizeOf(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    // ------------------------------------------------------------------------------------------------ another place

    /// <summary>The folders this installer keeps under <see cref="Root"/>: one per component, the downloads, the caches, the logs.</summary>
    private IEnumerable<string> StoredFolders() =>
        _catalog.Components.Select(c => c.Folder).Concat(["downloads", "cache", "logs"]).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything a move takes along: installed components, downloads, caches and logs.</summary>
    public long StoredBytes() => StoredFolders().Sum(f => SizeOf(Path.Combine(Root, f)));

    /// <summary>What a folder the user picked as the new location holds.</summary>
    public static LocationContents Inspect(string folder, ComponentCatalog catalog)
    {
        if (!Directory.Exists(folder))
            return LocationContents.Empty;
        var entries = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).ToList();
        if (entries.Count == 0)
            return LocationContents.Empty;
        var known = catalog.Components.Select(c => c.Folder).Concat(["downloads", "cache", "logs"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = catalog.Components.Any(c => File.Exists(Path.Combine(folder, c.Folder, InstalledComponent.FileName)));
        return installed && entries.All(e => e is not null && (known.Contains(e) || e.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)))
            ? LocationContents.Components
            : LocationContents.Other;
    }

    /// <summary>
    /// Moves everything kept under <see cref="Root"/> into <paramref name="destination"/>, which has to be empty. On the same
    /// drive the folders are renamed; across drives they are copied first (into <c>&lt;name&gt;.partial</c>) and the originals
    /// are deleted only when every copy is complete, so a failure leaves the components where they were.
    /// </summary>
    /// <param name="forceCopy">Copy even on the same drive (tests).</param>
    public async Task MoveToAsync(string destination, IProgress<ComponentProgress>? progress = null, CancellationToken ct = default, bool forceCopy = false)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (ComponentsRoot.IsSameOrUnder(target, Root) || ComponentsRoot.IsSameOrUnder(Root, target))
            throw new IOException("지금 위치와 겹치는 폴더로는 옮길 수 없어요.");
        if (Inspect(target, _catalog) != LocationContents.Empty)
            throw new IOException($"{target} 이(가) 비어 있지 않아요.");
        Directory.CreateDirectory(target);

        var items = StoredFolders().Where(f => Directory.Exists(Path.Combine(Root, f))).ToList();
        var sameDrive = string.Equals(Path.GetPathRoot(Root), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        if (sameDrive && !forceCopy)
        {
            var moved = new List<string>();
            try
            {
                foreach (var item in items)
                {
                    Report(progress, "", "move", item + " 옮기는 중");
                    await MoveAsync(Path.Combine(Root, item), Path.Combine(target, item), ct).ConfigureAwait(false);
                    moved.Add(item);
                }
            }
            catch
            {
                // all or nothing: what was moved already goes back
                foreach (var item in moved)
                {
                    try { Directory.Move(Path.Combine(target, item), Path.Combine(Root, item)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
        }
        else
        {
            var total = items.Sum(f => SizeOf(Path.Combine(Root, f)));
            long done = 0, told = 0;
            var copies = new List<string>();
            try
            {
                foreach (var item in items)
                {
                    var partial = Path.Combine(target, item + ".partial");
                    copies.Add(partial);
                    foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, item), "*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        var copy = Path.Combine(partial, Path.GetRelativePath(Path.Combine(Root, item), file));
                        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                        File.Copy(file, copy, overwrite: true);
                        done += new FileInfo(file).Length;
                        if (done - told >= 16 << 20 || done == total)
                        {
                            told = done;
                            Report(progress, "", "move", item + " 옮기는 중", done, total);
                        }
                    }
                }
                foreach (var item in items)
                    await MoveAsync(Path.Combine(target, item + ".partial"), Path.Combine(target, item), ct).ConfigureAwait(false);
            }
            catch
            {
                foreach (var partial in copies)
                    TryDelete(partial);
                throw;
            }
            foreach (var item in items)
                TryDelete(Path.Combine(Root, item));
        }

        // leftovers of an interrupted install and the old folder itself, when nothing else is in it
        foreach (var partial in Directory.Exists(Root) ? Directory.EnumerateDirectories(Root, "*.partial").ToList() : [])
            TryDelete(partial);
        try
        {
            if (Directory.Exists(Root) && !Directory.EnumerateFileSystemEntries(Root).Any())
                Directory.Delete(Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public async Task<ComponentInstallResult> InstallAsync(string id, IProgress<ComponentProgress>? progress = null, CancellationToken ct = default)
    {
        var spec = _catalog.Get(id);
        var watch = Stopwatch.StartNew();
        var final = FolderOf(id);
        var staging = final + ".partial";
        var tracker = new KeyTracker(progress);
        Directory.CreateDirectory(LogsDirectory);
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            var smoke = id switch
            {
                ComponentCatalog.Engine => await InstallEngineAsync(spec, staging, tracker, ct).ConfigureAwait(false),
                ComponentCatalog.BoardTools => await InstallBoardToolsAsync(spec, staging, tracker, ct).ConfigureAwait(false),
                ComponentCatalog.Zoo => await InstallZooAsync(spec, staging, tracker, ct).ConfigureAwait(false),
                _ => throw new NotSupportedException($"No installer for component '{id}'."),
            };
            var missing = spec.RequiredFiles.Where(f => !Exists(Path.Combine(staging, f))).ToList();
            if (missing.Count > 0)
                throw new InvalidDataException("missing after the install: " + string.Join(", ", missing));
            Report(tracker, id, "finish", "마무리하는 중");
            WriteRecord(spec, staging, smoke);
            await SwapAsync(staging, final, ct).ConfigureAwait(false);
            return new ComponentInstallResult { Component = id, Folder = final, Elapsed = watch.Elapsed, Smoke = smoke, LogDirectory = LogsDirectory };
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging);
            return new ComponentInstallResult { Component = id, Cancelled = true, Error = "cancelled", Elapsed = watch.Elapsed, LogDirectory = LogsDirectory, FailedKey = tracker.Key };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or HttpRequestException
                                       or UnauthorizedAccessException or NotSupportedException or KeyNotFoundException)
        {
            TryDelete(staging);
            File.WriteAllText(Path.Combine(LogsDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}_install-{id}.error.txt"), e.ToString());
            return new ComponentInstallResult
            {
                Component = id, Error = e.Message, Elapsed = watch.Elapsed, LogDirectory = LogsDirectory, FailedKey = tracker.Key,
                Mismatch = (e as ComponentDownloadMismatchException)?.Download, ActualSha256 = (e as ComponentDownloadMismatchException)?.ActualSha256,
            };
        }
    }

    // ------------------------------------------------------------------------------------------------ the engine

    /// <summary>
    /// The engine. The big toolkit zip is needed only after the Python packages are in (for its viewer wheel and the
    /// extract), so pip runs while it is still coming in; the steps report their keys in the order of the screen's lines.
    /// </summary>
    private async Task<string> InstallEngineAsync(ComponentSpec spec, string staging, IProgress<ComponentProgress> progress, CancellationToken ct)
    {
        var id = spec.Id;
        var toolkit = spec.Toolkit ?? throw new InvalidDataException("the engine has no toolkit entry in the catalog");
        var pythonZip = await FetchAsync(id, spec.Download("python"), progress, ct).ConfigureAwait(false);
        var pipWheel = await FetchAsync(id, spec.Download("pip"), progress, ct).ConfigureAwait(false);

        // Python: python38._pth would make it run isolated and leave the script folder off sys.path (feature/09 §2)
        Report(progress, id, "python", "Python 푸는 중");
        var py = Path.Combine(staging, "python38");
        ZipFile.ExtractToDirectory(pythonZip, py);
        File.Delete(Path.Combine(py, "python38._pth"));
        Directory.CreateDirectory(Path.Combine(py, "Lib", "site-packages"));
        var python = Path.Combine(py, "python.exe");
        var runner = new ToolRunner(new EngineLayout(staging, staging)); // native runs only
        var work = Directory.CreateDirectory(Path.Combine(CacheDirectory, "work")).FullName;

        Report(progress, id, "pip", "pip 준비하는 중");
        await RunAsync(runner, python, ["-m", "pip", "install", "--no-index", "--no-warn-script-location", "--disable-pip-version-check", "-q", pipWheel],
            "engine-pip", work, ct, new Dictionary<string, string> { ["PYTHONPATH"] = pipWheel }).ConfigureAwait(false);

        string[] pip = ["-s", "-m", "pip", "install", "--only-binary=:all:", "--no-deps", "--no-warn-script-location",
                        "--disable-pip-version-check", "--cache-dir", PipCacheDirectory, "-q"];
        if (spec.Packages is { } packages)
        {
            var lockPath = Path.Combine(_catalog.Directory, packages.Lock);
            var local = await FetchWheelsAsync(id, "packages", lockPath, progress, ct).ConfigureAwait(false);
            Report(progress, id, "packages", local is not null ? "Python 패키지 설치하는 중 (PyTorch 등, 1~2분 걸립니다)" : "Python 패키지 받아 설치하는 중 (PyTorch 등, 몇 분 걸립니다)");
            await RunAsync(runner, python, [.. pip, .. PipSources(local), "--require-hashes", "-r", lockPath], "engine-packages", work, ct).ConfigureAwait(false);
        }
        foreach (var extra in spec.Extras)
        {
            var lockPath = Path.Combine(_catalog.Directory, extra.Lock);
            var local = await FetchWheelsAsync(id, "extras", lockPath, progress, ct).ConfigureAwait(false);
            Report(progress, id, "extras", $"{extra.Target} 설치하는 중");
            await RunAsync(runner, python, [.. pip, .. PipSources(local), "--require-hashes", "--target", Native(staging, extra.Target), "-r", lockPath],
                "engine-extra", work, ct).ConfigureAwait(false);
        }

        var toolkitZip = await FetchAsync(id, spec.Download(toolkit.Download), progress, ct).ConfigureAwait(false);
        // wheels that come with the toolkit (netron): the zip itself was checked, so no hash per wheel
        Report(progress, id, "toolkit-wheels", "툴킷의 도구 설치하는 중");
        using (var zip = ZipFile.OpenRead(toolkitZip))
            foreach (var wheel in toolkit.Wheels)
            {
                var entry = zip.GetEntry(toolkit.Root + wheel) ?? throw new InvalidDataException($"the toolkit zip has no {wheel}");
                var local = Path.Combine(Directory.CreateDirectory(Path.Combine(CacheDirectory, "wheels")).FullName, Path.GetFileName(wheel));
                entry.ExtractToFile(local, overwrite: true);
                await RunAsync(runner, python, [.. pip, "--no-index", local], "engine-" + Path.GetFileNameWithoutExtension(wheel).Split('-')[0], work, ct).ConfigureAwait(false);
            }
        await RunAsync(runner, python, ["-s", "-m", "pip", "check", "--disable-pip-version-check"], "engine-pip-check", work, ct).ConfigureAwait(false);

        Report(progress, id, "prune", "필요 없는 파일 정리하는 중");
        Prune(spec.Prune, staging);

        Report(progress, id, "extract", "툴킷에서 필요한 부분 꺼내는 중");
        ExtractSubset(toolkitZip, toolkit, staging, ct);

        Report(progress, id, "smoke", "엔진이 도는지 확인하는 중");
        var smokeDir = Directory.CreateDirectory(Path.Combine(CacheDirectory, "smoke")).FullName; // outside the toolkit folder on purpose
        var pycache = "pycache_prefix=" + Path.Combine(CacheDirectory, "pycache");
        var sdk = Path.Combine(staging, "toolkit", "EnlightSDK");
        var run = await RunAsync(runner, python, ["-X", "utf8", "-u", "-s", "-X", pycache, "-c",
            $"import sys, os; sys.path.insert(0, r'{sdk}'); import torch, enlight; print('SDK', enlight.SDK_version, 'torch', torch.__version__)"],
            "engine-smoke", smokeDir, ct).ConfigureAwait(false);
        var smoke = run.StdoutLines.LastOrDefault(l => l.StartsWith("SDK ", StringComparison.Ordinal))
                    ?? throw new InvalidDataException("the engine smoke test printed no SDK version");
        foreach (var extra in spec.Extras.Where(e => e.Target.EndsWith("onnx", StringComparison.Ordinal)))
        {
            // a process of its own: onnx and the SDK cannot share one (protobuf)
            var onnx = await RunAsync(runner, python, ["-X", "utf8", "-u", "-s", "-X", pycache, "-c",
                $"import sys; sys.path.insert(0, r'{Native(staging, extra.Target)}'); import onnx; print('onnx', onnx.__version__)"],
                "engine-smoke-onnx", smokeDir, ct).ConfigureAwait(false);
            smoke += " | " + (onnx.StdoutLines.LastOrDefault(l => l.StartsWith("onnx ", StringComparison.Ordinal)) ?? "onnx ?");
        }
        return smoke;
    }

    /// <summary>Build-only files removed after pip (feature/09 §6-1). File patterns are <c>*.ext</c>, matched exactly.</summary>
    internal static void Prune(PruneSpec? prune, string folder)
    {
        if (prune is null)
            return;
        var extensions = prune.Files.Select(p => p.TrimStart('*')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rootRelative in prune.Roots)
        {
            var root = Native(folder, rootRelative);
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => extensions.Contains(Path.GetExtension(f))).ToList())
                File.Delete(file);
            foreach (var name in prune.Dirs)
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                             .Where(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(d => d.Length).ToList())
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
        }
        foreach (var relative in prune.Remove)
        {
            var path = Native(folder, relative);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// The folders of the toolkit zip the engine uses, with the folder and file names of each rule left out at any depth
    /// (what robocopy <c>/E /XD /XF</c> did from the extracted toolkit).
    /// </summary>
    internal static void ExtractSubset(string zipPath, ToolkitSpec spec, string folder, CancellationToken ct = default)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var rules = spec.Copy.Select(r => (Rule: r, Prefix: spec.Root + r.From.Trim('/') + "/")).ToList();
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (rule, prefix) in rules)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var relative = entry.FullName[prefix.Length..];
                var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var isDirectory = entry.FullName.EndsWith('/') || parts.Length == 0;
                var folders = isDirectory ? parts : parts[..^1];
                if (folders.Any(p => rule.ExcludeDirs.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    break;
                if (!isDirectory && rule.ExcludeFiles.Contains(parts[^1], StringComparer.OrdinalIgnoreCase))
                    break;
                var target = Path.GetFullPath(Path.Combine(Native(folder, rule.To), relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(top, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("a toolkit zip entry points outside the install folder: " + entry.FullName);
                if (isDirectory)
                    Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                break;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ board tools

    private async Task<string> InstallBoardToolsAsync(ComponentSpec spec, string staging, IProgress<ComponentProgress> progress, CancellationToken ct)
    {
        var archive = spec.Archive ?? throw new InvalidDataException("board tools have no archive entry in the catalog");
        var file = await FetchAsync(spec.Id, spec.Download(archive.Download), progress, ct).ConfigureAwait(false);
        // bsdtar with xz support, part of Windows 10 1803 and later (the app needs 1809)
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar))
            throw new FileNotFoundException("Windows tar.exe was not found (Windows 10 1803 or later has it)", tar);
        var runner = new ToolRunner(new EngineLayout(staging, staging));
        Report(progress, spec.Id, "archive", "필요한 파일만 꺼내는 중 (압축 파일을 한 번 다 읽어서 조금 걸립니다)");
        await RunAsync(runner, tar, ["-xf", file, "-C", staging, .. archive.Members.Select(m => archive.Root + "/" + m)], "board-tools-tar", staging, ct).ConfigureAwait(false);
        // gcc.exe and ld.exe are hard links in the archive; the extracted regular files get those names
        foreach (var rename in archive.Renames)
            File.Move(Native(Path.Combine(staging, archive.Root), rename.From), Native(Path.Combine(staging, archive.Root), rename.To));

        Report(progress, spec.Id, "smoke", "컴파일러가 도는지 확인하는 중");
        var gcc = Native(Path.Combine(staging, archive.Root), "bin/aarch64-none-linux-gnu-gcc.exe");
        var probe = Directory.CreateDirectory(Path.Combine(CacheDirectory, "smoke-board")).FullName;
        var temp = Directory.CreateDirectory(Path.Combine(probe, "tmp")).FullName;
        File.WriteAllText(Path.Combine(probe, "probe.c"), "#include <stdio.h>\n#include <stdint.h>\nint probe(void) { puts(\"ok\"); return 0; }\n");
        var environment = new Dictionary<string, string> { ["TMP"] = temp, ["TEMP"] = temp, ["TMPDIR"] = temp };
        await RunAsync(runner, gcc, ["-fPIC", "-O2", "-g", "-pg", "-c", "probe.c"], "board-tools-smoke-compile", probe, ct, environment).ConfigureAwait(false);
        // linked as net.so is (NetSoBuild.Link): with the library folder named, so that it links from "Program Files" too
        await RunAsync(runner, gcc, ["-shared", "-Wl,-soname,probe.so", "-Wl,--discard-all", .. NetSoBuild.LibraryFlags(gcc), "-o", "probe.so", "probe.o"],
            "board-tools-smoke-link", probe, ct, environment).ConfigureAwait(false);
        if (ElfHeader.TryRead(Path.Combine(probe, "probe.so")) is not { IsAArch64SharedObject: true })
            throw new InvalidDataException("the board tools smoke test did not produce an AArch64 shared object");
        var version = await RunAsync(runner, gcc, ["--version"], "board-tools-version", probe, ct, environment).ConfigureAwait(false);
        return (version.StdoutLines.FirstOrDefault() ?? "gcc") + " | aarch64 shared object built";
    }

    // ------------------------------------------------------------------------------------------------ the model zoo

    /// <summary>
    /// The model zoo's export packages (design §5.9: timm, Ultralytics, onnx 1.17, onnxslim ...) into <c>zoo\site</c>,
    /// with the engine's Python: <c>pip --target</c> from the hashed lock, the engine itself untouched. They run in a
    /// process of their own (<c>engine\helpers\zoo_export.py</c>): their opencv and onnx must not meet the toolkit's.
    /// </summary>
    private async Task<string> InstallZooAsync(ComponentSpec spec, string staging, IProgress<ComponentProgress> progress, CancellationToken ct)
    {
        var id = spec.Id;
        var python = Path.Combine(FolderOf(spec.Needs ?? ComponentCatalog.Engine), "python38", "python.exe");
        if (!File.Exists(python))
            throw new InvalidOperationException("the engine is not installed here, and the model zoo's packages run with its Python");
        if (spec.Extras.Count == 0)
            throw new InvalidDataException("the model zoo component has no package lock in the catalog");
        var runner = new ToolRunner(new EngineLayout(staging, staging)); // native runs only
        var work = Directory.CreateDirectory(Path.Combine(CacheDirectory, "zoo-install")).FullName;
        string[] pip = ["-s", "-m", "pip", "install", "--only-binary=:all:", "--no-deps", "--no-warn-script-location",
                        "--disable-pip-version-check", "--cache-dir", PipCacheDirectory, "-q"];
        foreach (var extra in spec.Extras)
        {
            var lockPath = Path.Combine(_catalog.Directory, extra.Lock);
            var local = await FetchWheelsAsync(id, "extras", lockPath, progress, ct).ConfigureAwait(false);
            Report(progress, id, "extras", (local is not null ? "Python 패키지 설치하는 중" : "Python 패키지 받아 설치하는 중") + " (timm · Ultralytics · onnx · onnxslim 등)");
            await RunAsync(runner, python, [.. pip, .. PipSources(local), "--require-hashes", "--target", Native(staging, extra.Target), "-r", lockPath],
                "zoo-packages", work, ct).ConfigureAwait(false);
        }
        Report(progress, id, "prune", "필요 없는 파일 정리하는 중");
        Prune(spec.Prune, staging);

        Report(progress, id, "smoke", "내보내기 도구가 도는지 확인하는 중");
        var site = Native(staging, spec.Extras[0].Target);
        var smokeDir = Directory.CreateDirectory(Path.Combine(CacheDirectory, "smoke-zoo")).FullName;
        var environment = new Dictionary<string, string>
        {
            ["YOLO_CONFIG_DIR"] = Path.Combine(smokeDir, "ultralytics"), ["YOLO_OFFLINE"] = "True", ["YOLO_AUTOINSTALL"] = "False",
        };
        var pycache = "pycache_prefix=" + Path.Combine(CacheDirectory, "pycache");
        var run = await RunAsync(runner, python, ["-X", "utf8", "-u", "-s", "-X", pycache, "-c",
            $"import sys; sys.path.insert(0, r'{site}'); import timm, ultralytics, onnx, onnxslim; " +
            "print('timm', timm.__version__, 'ultralytics', ultralytics.__version__, 'onnx', onnx.__version__, 'onnxslim', onnxslim.__version__)"],
            "zoo-smoke", smokeDir, ct, environment).ConfigureAwait(false);
        return run.StdoutLines.LastOrDefault(l => l.StartsWith("timm ", StringComparison.Ordinal))
               ?? throw new InvalidDataException("the model zoo smoke test printed no versions");
    }

    // ------------------------------------------------------------------------------------------------ downloads

    /// <summary>
    /// Starts everything the components need from the internet at once (2026-11-25): the files of the catalog, a big one
    /// over several connections (<see cref="ParallelDownload"/>), and the Python packages of their lock files. The installs
    /// join these downloads when they get to them. A failure is left to the install that needs the file, which then says
    /// what went wrong instead of downloading it again.
    /// </summary>
    public async Task PrefetchAsync(IReadOnlyList<string> ids, IProgress<ComponentProgress>? progress, CancellationToken ct)
    {
        _failures.Clear();
        progress = progress is null ? null : new AheadRelay(progress);
        var tasks = new List<Task>();
        foreach (var spec in ids.Select(_catalog.Get))
        {
            foreach (var download in spec.Downloads)
                tasks.Add(RememberFailure(Path.Combine(DownloadFolderOf(download), download.FileName), FetchAsync(spec.Id, download, progress, ct)));
            if (spec.Packages is { } packages)
                tasks.Add(FetchWheelsAsync(spec.Id, "packages", Path.Combine(_catalog.Directory, packages.Lock), progress, ct));
            foreach (var extra in spec.Extras)
                tasks.Add(FetchWheelsAsync(spec.Id, "extras", Path.Combine(_catalog.Directory, extra.Lock), progress, ct));
        }
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // each install reports its own failure; a cancel ends the installs as well
        }
    }

    private async Task RememberFailure(string target, Task<string> fetch)
    {
        try
        {
            await fetch.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _failures[target] = e;
            throw;
        }
    }

    /// <summary>
    /// A file of the catalog, checked: an earlier download or a file in the lookup folders with the right size and hash,
    /// or a fresh download from the official source. Callers that want the same file at the same time share one download.
    /// A download that does not match is deleted and the install stops.
    /// </summary>
    /// <param name="folder">Keep the file here instead of <see cref="DownloadsDirectory"/> (the model zoo's weights go to the
    /// user's own folder: the components folder may be read-only). A copy in the usual place is still used.</param>
    internal async Task<string> FetchAsync(string component, DownloadSpec download, IProgress<ComponentProgress>? progress, CancellationToken ct, string? folder = null)
    {
        var target = Path.Combine(folder ?? DownloadFolderOf(download), download.FileName);
        if (_failures.TryGetValue(target, out var failed))
            ExceptionDispatchInfo.Throw(failed);
        var job = Join("download:" + download.Id, target, progress, component, j => DownloadAsync(j, download, target, ct));
        return await job.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> DownloadAsync(Job job, DownloadSpec download, string target, CancellationToken ct)
    {
        await Task.Yield();
        return await GetFileAsync(download, target, (step, done, total) => job.Step(step, done, total), (done, total) => job.Bytes(done, total), ct).ConfigureAwait(false);
    }

    /// <summary>The checks and the download behind <see cref="FetchAsync"/>; <paramref name="step"/> and <paramref name="bytes"/> say how far it is.</summary>
    private async Task<string> GetFileAsync(DownloadSpec download, string target, Action<string, long?, long?>? step, Action<long, long>? bytes, CancellationToken ct)
    {
        foreach (var candidate in Candidates(download, target))
        {
            if (!File.Exists(candidate))
                continue;
            step?.Invoke(download.Name + " 확인하는 중", null, null);
            if (await VerifiedAsync(candidate, download, ct).ConfigureAwait(false))
                return candidate;
        }

        if (download.Sha256.Length == 0 && download.Sha256Prefix is not { Length: >= 8 })
            throw new InvalidDataException($"no hash to check {download.Name} against");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        step?.Invoke(download.Name + " 받는 중", 0, download.Bytes);
        await ParallelDownload.DownloadAsync(_http, download.Url, part, download.Bytes, (done, total) => bytes?.Invoke(done, total), ct, RetryDelay).ConfigureAwait(false);
        var (sha256, md5) = await HashAsync(part, download.Md5 is not null, ct).ConfigureAwait(false);
        if (!download.Accepts(sha256) || (download.Md5 is not null && !Same(md5, download.Md5)))
        {
            File.Delete(part);
            ParallelDownload.DeleteState(part);
            throw new ComponentDownloadMismatchException(download, sha256);
        }
        File.Move(part, target, overwrite: true);
        var info = new FileInfo(target);
        _verified[target] = (info.Length, info.LastWriteTimeUtc, sha256, md5);
        return target;
    }

    private string DownloadFolderOf(DownloadSpec download) =>
        download.Folder is { Length: > 0 } sub ? Path.Combine(DownloadsDirectory, sub) : DownloadsDirectory;

    private IEnumerable<string> Candidates(DownloadSpec download, string target)
    {
        yield return target;
        var usual = Path.Combine(DownloadFolderOf(download), download.FileName);
        if (!string.Equals(usual, target, StringComparison.OrdinalIgnoreCase))
            yield return usual;
        foreach (var dir in _lookup.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir, download.FileName, SearchOption.AllDirectories))
                yield return file;
    }

    /// <summary><see cref="MatchesAsync"/>, remembering the hash of a file this process read already.</summary>
    private async Task<bool> VerifiedAsync(string path, DownloadSpec download, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (download.Bytes > 0 && info.Length != download.Bytes))
            return false;
        if (!_verified.TryGetValue(path, out var known) || known.Length != info.Length || known.Written != info.LastWriteTimeUtc
            || (download.Md5 is not null && known.Md5 is null))
        {
            var (sha256, md5) = await HashAsync(path, download.Md5 is not null, ct).ConfigureAwait(false);
            known = (info.Length, info.LastWriteTimeUtc, sha256, md5);
            _verified[path] = known;
        }
        return download.Accepts(known.Sha256) && (download.Md5 is null || Same(known.Md5, download.Md5));
    }

    internal static async Task<bool> MatchesAsync(string path, DownloadSpec download, CancellationToken ct)
    {
        if (!File.Exists(path) || (download.Bytes > 0 && new FileInfo(path).Length != download.Bytes))
            return false;
        var (sha256, md5) = await HashAsync(path, download.Md5 is not null, ct).ConfigureAwait(false);
        return download.Accepts(sha256) && (download.Md5 is null || Same(md5, download.Md5));
    }

    private static async Task<(string Sha256, string? Md5)> HashAsync(string path, bool withMd5, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = withMd5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            md5?.AppendData(buffer, 0, read);
        }
        return (Convert.ToHexStringLower(sha.GetHashAndReset()), md5 is null ? null : Convert.ToHexStringLower(md5.GetHashAndReset()));
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None, MaxConnectionsPerServer = 32 })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TOPST-Toolkit");
        return http;
    }

    // ------------------------------------------------------------------------------------------------ Python packages

    /// <summary>Concurrent package downloads: enough to fill a line, few enough to stay friendly to PyPI's servers.</summary>
    private const int WheelConnections = 4;

    /// <summary>
    /// The wheels of a pip lock file, into <see cref="WheelsDirectory"/>, four at a time. Their addresses come from PyPI's
    /// JSON API, matched by the lock's SHA-256, so they are exactly the files pip would take. When every wheel is there:
    /// the folders they are in (the wheels folder, a lookup folder), for pip to install without the index; null when some
    /// could not be fetched — pip then downloads those itself.
    /// </summary>
    internal async Task<IReadOnlyList<string>?> FetchWheelsAsync(string component, string key, string lockPath, IProgress<ComponentProgress>? progress, CancellationToken ct)
    {
        var job = Join(key, "wheels:" + Path.GetFullPath(lockPath), progress, component, j => WheelsAsync(j, lockPath, ct));
        var folders = await job.Task.WaitAsync(ct).ConfigureAwait(false);
        return folders.Length == 0 ? null : folders.Split('\n');
    }

    private async Task<string> WheelsAsync(Job job, string lockPath, CancellationToken ct)
    {
        await Task.Yield();
        var packages = ReadLock(lockPath);
        if (packages.Count == 0)
            return WheelsDirectory;
        job.Step("Python 패키지 목록 확인하는 중");
        var wheels = await ResolveWheelsAsync(lockPath, packages, ct).ConfigureAwait(false);
        var known = wheels.OfType<DownloadSpec>().ToList();
        var total = known.Sum(w => w.Bytes);
        var done = new long[known.Count];
        var finished = 0;
        string Counted() => $"Python 패키지 받는 중 ({Volatile.Read(ref finished)} / {packages.Count}개)";
        job.Step(Counted(), 0, total);
        using var gate = new SemaphoreSlim(WheelConnections);
        var fetched = await Task.WhenAll(known.Select(async (wheel, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var path = await GetFileAsync(wheel, Path.Combine(WheelsDirectory, wheel.FileName), null, (d, _) =>
                {
                    Volatile.Write(ref done[i], d);
                    job.Bytes(done.Sum(), total);
                }, ct).ConfigureAwait(false);
                Volatile.Write(ref done[i], wheel.Bytes);
                Interlocked.Increment(ref finished);
                job.Step(Counted(), done.Sum(), total);
                return Path.GetDirectoryName(path);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException)
            {
                return null; // pip fetches it itself (and says what is wrong, if anything is)
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        // a wheel found in a lookup folder stays there: pip is told that folder too
        return known.Count == packages.Count && fetched.All(f => f is not null)
            ? string.Join('\n', fetched.OfType<string>().Prepend(WheelsDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
            : "";
    }

    /// <summary>The pip lines of a lock file with a version and a hash (comments and blank lines left out).</summary>
    public static IReadOnlyList<LockedPackage> ReadLock(string path) =>
        [.. File.ReadLines(path).Select(l => LockLine.Match(l.Trim())).Where(m => m.Success)
            .Select(m => new LockedPackage(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.ToLowerInvariant()))];

    private static readonly Regex LockLine = new(@"^([A-Za-z0-9_.\-]+)==(\S+)\s+--hash=sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);

    /// <summary>What PyPI said about each package of a lock, kept next to the wheels (<c>&lt;lock&gt;.json</c>): asked once.</summary>
    private sealed record WheelEntry(string File, string Url, long Bytes);

    private async Task<IReadOnlyList<DownloadSpec?>> ResolveWheelsAsync(string lockPath, IReadOnlyList<LockedPackage> packages, CancellationToken ct)
    {
        var indexPath = Path.Combine(WheelsDirectory, Path.GetFileNameWithoutExtension(lockPath) + ".json");
        var index = ReadIndex(indexPath);
        var asked = false;
        var result = new DownloadSpec?[packages.Count];
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(packages.Select(async (package, i) =>
        {
            if (!index.TryGetValue(package.Sha256, out var entry))
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    entry = await AskPypiAsync(package, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
                if (entry is null)
                    return;
                lock (index)
                    index[package.Sha256] = entry;
                asked = true;
            }
            result[i] = new DownloadSpec
            {
                Id = "wheel:" + entry.File, Name = $"{package.Name} {package.Version}", Url = entry.Url, Sha256 = package.Sha256,
                Bytes = entry.Bytes, LocalName = entry.File, Folder = "wheels", License = "PyPI",
            };
        })).ConfigureAwait(false);
        if (asked)
        {
            try
            {
                Directory.CreateDirectory(WheelsDirectory);
                File.WriteAllText(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // asked again next time
            }
        }
        return result;
    }

    private static Dictionary<string, WheelEntry> ReadIndex(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, WheelEntry>>(File.ReadAllText(path)) is { } index)
                return new Dictionary<string, WheelEntry>(index.Where(kv => IsWheelName(kv.Value.File) && IsHttps(kv.Value.Url)), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new Dictionary<string, WheelEntry>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The one file of a release with the lock's SHA-256, from <c>pypi.org/pypi/&lt;name&gt;/&lt;version&gt;/json</c>; null when PyPI cannot say.</summary>
    private async Task<WheelEntry?> AskPypiAsync(LockedPackage package, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"{PypiJsonBase}{Uri.EscapeDataString(package.Name)}/{Uri.EscapeDataString(package.Version)}/json", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var file in urls.EnumerateArray())
            {
                if (file.TryGetProperty("digests", out var digests) && digests.TryGetProperty("sha256", out var sha) && Same(sha.GetString(), package.Sha256)
                    && file.TryGetProperty("url", out var url) && url.GetString() is { } address && IsHttps(address)
                    && file.TryGetProperty("filename", out var name) && name.GetString() is { } fileName && IsWheelName(fileName))
                    return new WheelEntry(fileName, address, file.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0);
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A plain wheel file name: nothing that could point outside the wheels folder.</summary>
    private static bool IsWheelName(string name) =>
        name.EndsWith(".whl", StringComparison.OrdinalIgnoreCase) && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.StartsWith('.');

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Where pip takes the packages from: the wheels fetched beforehand alone (no index), or those and PyPI.</summary>
    private string[] PipSources(IReadOnlyList<string>? folders) =>
        folders is not null ? ["--no-index", .. folders.Where(Directory.Exists).SelectMany(f => new[] { "--find-links", f })]
        : Directory.Exists(WheelsDirectory) ? ["--find-links", WheelsDirectory] : [];

    private Dictionary<string, string> LockHashes(ComponentSpec spec)
    {
        var locks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { spec.Packages?.Lock }.Concat(spec.Extras.Select(e => e.Lock)).OfType<string>())
        {
            var path = Path.Combine(_catalog.Directory, name);
            if (File.Exists(path))
                locks[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return locks;
    }

    // ------------------------------------------------------------------------------------------------ shared downloads

    /// <summary>The download <paramref name="id"/>, joined when it runs already, started otherwise; <paramref name="progress"/> follows it.</summary>
    private Job Join(string key, string id, IProgress<ComponentProgress>? progress, string component, Func<Job, Task<string>> body)
    {
        Job job;
        var created = false;
        lock (_jobsGate)
        {
            if (!_jobs.TryGetValue(id, out job!))
            {
                job = new Job(key);
                _jobs[id] = job;
                created = true;
            }
            job.Watch(progress, component);
        }
        if (created)
            _ = RunJobAsync(job, id, body);
        return job;
    }

    private async Task RunJobAsync(Job job, string id, Func<Job, Task<string>> body)
    {
        try
        {
            await job.RunAsync(body).ConfigureAwait(false);
        }
        finally
        {
            lock (_jobsGate)
                _jobs.Remove(id);
        }
    }

    /// <summary>One download (or the packages of one lock file) that several callers may wait for, each told under its own component.</summary>
    private sealed class Job(string key)
    {
        private readonly object _gate = new();
        private readonly List<(IProgress<ComponentProgress> Progress, string Component)> _watchers = [];
        private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _step;
        private long? _done, _total;
        private long _toldBytes = -1, _toldAt;

        public Task<string> Task => _result.Task;

        public void Watch(IProgress<ComponentProgress>? progress, string component)
        {
            if (progress is null)
                return;
            lock (_gate)
            {
                _watchers.Add((progress, component));
                if (_step is not null)
                    progress.Report(new ComponentProgress(component, key, _step, _done, _total));
            }
        }

        /// <summary>A new stage ("… 확인하는 중", "… 받는 중"): always told.</summary>
        public void Step(string step, long? done = null, long? total = null)
        {
            lock (_gate)
            {
                _step = step;
                _done = done;
                _total = total;
                Tell();
            }
        }

        /// <summary>Bytes from the connections of a download: told every 4 MB or quarter second, and at the end; never backwards.</summary>
        public void Bytes(long done, long total)
        {
            lock (_gate)
            {
                if (_done is { } before && done < before && total == _total)
                    return;
                _done = done;
                _total = total;
                if (done == total || done - _toldBytes >= 4 << 20 || Environment.TickCount64 - _toldAt >= 250)
                    Tell();
            }
        }

        private void Tell()
        {
            _toldBytes = _done ?? -1;
            _toldAt = Environment.TickCount64;
            if (_step is null)
                return;
            foreach (var (progress, component) in _watchers)
                progress.Report(new ComponentProgress(component, key, _step, _done, _total));
        }

        public async Task RunAsync(Func<Job, Task<string>> body)
        {
            try
            {
                _result.TrySetResult(await body(this).ConfigureAwait(false));
            }
            catch (OperationCanceledException e)
            {
                _result.TrySetCanceled(e.CancellationToken);
            }
            catch (Exception e)
            {
                _result.TrySetException(e);
            }
        }
    }

    /// <summary>Marks what a prefetch tells as <see cref="ComponentProgress.Ahead"/>.</summary>
    private sealed class AheadRelay(IProgress<ComponentProgress> inner) : IProgress<ComponentProgress>
    {
        public void Report(ComponentProgress value) => inner.Report(value with { Ahead = true });
    }

    /// <summary>Remembers the last stage an install reported, for the failure screen (one per install, so installs can run side by side).</summary>
    private sealed class KeyTracker(IProgress<ComponentProgress>? inner) : IProgress<ComponentProgress>
    {
        private volatile string? _key;
        public string? Key => _key;

        public void Report(ComponentProgress value)
        {
            _key = value.Key;
            inner?.Report(value);
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private async Task<ToolRunResult> RunAsync(ToolRunner runner, string program, IReadOnlyList<string> arguments, string name, string workingDirectory,
        CancellationToken ct, IReadOnlyDictionary<string, string>? environment = null)
    {
        var request = new ToolRunRequest
        {
            WorkingDirectory = workingDirectory,
            PycacheDirectory = Path.Combine(CacheDirectory, "pycache"),
            LogDirectory = LogsDirectory,
            LogName = $"{DateTime.Now:yyyyMMdd-HHmmss}_{name}",
            Environment = environment,
        };
        var run = await runner.RunAsync(new ToolInvocation(ToolKind.Native, program, arguments, name), request, null, ct).ConfigureAwait(false);
        if (run.Cancelled)
            throw new OperationCanceledException(ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"{name} failed with exit code {run.ExitCode}: " +
                                                string.Join(" | ", run.StderrLines.Concat(run.StdoutLines).Where(l => l.Trim().Length > 0).TakeLast(4)));
        return run;
    }

    private void WriteRecord(ComponentSpec spec, string folder, string smoke)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        var record = new InstalledComponent
        {
            Id = spec.Id, InstalledAt = DateTimeOffset.Now, App = _appVersion,
            Downloads = spec.Downloads.ToDictionary(d => d.Id, d => d.Sha256), Locks = LockHashes(spec), Smoke = smoke,
            Files = files.Count, Bytes = files.Sum(f => f.Length),
        };
        File.WriteAllText(Path.Combine(folder, InstalledComponent.FileName), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Puts the new folder in place of the old one; the old one is removed only after the new one is there.</summary>
    private static async Task SwapAsync(string staging, string final, CancellationToken ct)
    {
        var old = final + ".old";
        TryDelete(old);
        if (Directory.Exists(final))
            await MoveAsync(final, old, ct).ConfigureAwait(false);
        await MoveAsync(staging, final, ct).ConfigureAwait(false);
        TryDelete(old);
    }

    /// <summary>
    /// Renames a folder, waiting while something holds a file in it for a moment. Seen 2026-11-22 right after the board
    /// tools smoke test: "access denied" on the freshly run compiler for about a second (most likely the virus scanner).
    /// </summary>
    private static async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 30)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250 * attempt, 2000)), ct).ConfigureAwait(false);
            }
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Native(string folder, string relative) => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void Report(IProgress<ComponentProgress>? progress, string component, string key, string step, long? done = null, long? total = null) =>
        progress?.Report(new ComponentProgress(component, key, step, done, total));
}
