using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TopstToolkit.Core.Components;
using TopstToolkit.Core.Settings;
using TopstToolkit.Core.Workspace;

namespace TopstToolkit.App.Services;

/// <summary>
/// <c>TopstToolkit.exe --install-components …</c>: installs components without a window (design §7.2, 2026-11-25). Setup
/// runs it right after copying the app, so the engine, the board tools and the model zoo's packages come with the install;
/// the app runs it with administrator rights when the components folder is in Program Files and needs repairing.
/// <code>
///   --components engine,board-tools,zoo   what to install (default: all), only what is missing or older than the catalog
///   --root &lt;folder&gt;                     the components folder (default: where the app looks, ComponentsRoot)
///   --lookup &lt;folder&gt;                   a folder with files downloaded before (repeatable; offline installs, tests)
///   --sequential                          one after another (the app's screen); otherwise the engine and board tools together
///   --force  --clean                      install even what is current; delete downloads and caches after a full success
///   --progress &lt;file&gt;  --cancel &lt;file&gt;  JSON lines for the app; stop when the cancel file appears
///   --lines &lt;file&gt;                     Setup's lines (below) into a file too, with I &lt;pid&gt; first and X &lt;exit code&gt; last
///   --parent &lt;pid&gt;                      stop when that process ends
///   --remove-component &lt;id&gt; | --delete-kept-files    the two other changes the app may need rights for
/// </code>
/// Standard output (and <c>--lines</c>), for Setup's page: <c>P &lt;0-1000&gt;</c>, <c>T &lt;what it does&gt;</c>,
/// <c>S &lt;download line&gt;</c>, <c>R &lt;id&gt; ok|failed|cancelled</c>, <c>E &lt;message&gt;</c>, in UTF-8. Setup reads the file
/// rather than standard output: while Inno Setup waits for a program it locks its window, and the page's
/// "나중에 설치" button has to work. Exit code 0 when everything is in place, 1 when
/// something failed, 2 when cancelled, 3 when it could not start.
/// </summary>
public static class InstallHelper
{
    public const string Switch = "--install-components";

    /// <summary><c>--prepare-user</c>: Setup runs it as the user who installs (not elevated), to make <c>내 문서\TOPST Toolkit</c>.</summary>
    public const string PrepareUserSwitch = "--prepare-user";

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        string? Option(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Count).Select(x => args[x.i + 1]).LastOrDefault();
        var lookups = args.Select((a, i) => (a, i)).Where(x => x.a == "--lookup" && x.i + 1 < args.Count).Select(x => args[x.i + 1]).ToList();
        using var output = new HelperOutput(Option("--progress"), Option("--lines"));
        output.Line($"I {Environment.ProcessId}");

        var catalog = ComponentCatalog.Find();
        var settings = AppSettings.Load();
        var root = Option("--root") ?? ComponentsRoot.DefaultLocation(configured: settings.ComponentsRoot);
        if (catalog is null || root is null)
        {
            output.Error(catalog is null ? "구성 요소 목록(engine\\components\\components.json)을 찾지 못했어요." : "구성 요소를 설치할 영문 경로를 찾지 못했어요.");
            output.End(3);
            return 3;
        }
        var installer = new ComponentInstaller(catalog, root, lookups, appVersion: AppServices.AppVersion);

        if (Option("--remove-component") is { } remove)
            return output.End(Try(output, () => installer.Remove(remove)));
        if (args.Contains("--delete-kept-files"))
            return output.End(Try(output, installer.DeleteKeptFiles));

        List<string> requested = Option("--components") is { } list ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [.. catalog.Components.Select(c => c.Id)];
        var unknown = requested.Where(id => catalog.Components.All(c => c.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            output.Error("모르는 구성 요소: " + string.Join(", ", unknown));
            return output.End(3);
        }
        var ids = args.Contains("--force")
            ? [.. catalog.Components.Select(c => c.Id).Where(requested.Contains)]
            : ComponentBatch.Needed(installer, requested);
        if (ids.Count == 0)
        {
            output.Line("P 1000");
            output.Line("T 구성 요소가 모두 설치되어 있어요");
            return output.End(0);
        }

        using var cts = new CancellationTokenSource();
        using var watcher = Watch(Option("--cancel"), Option("--parent"), cts);
        var progress = new BatchProgress(catalog, ids);
        var reporter = new Reporter(output, progress);
        using var ticker = new Timer(_ => reporter.Tick(), null, 250, 250);
        var results = await ComponentBatch.RunAsync(installer, ids, together: !args.Contains("--sequential"),
            id => output.Json(new { t = "start", c = id }), reporter, cts.Token).ConfigureAwait(false);
        foreach (var result in results)
        {
            progress.Finished(result.Component, result.Success);
            output.Json(new
            {
                t = "r", c = result.Component, ok = result.Success, cancelled = result.Cancelled, error = result.Error, key = result.FailedKey,
                elapsed = result.Elapsed.TotalSeconds, smoke = result.Smoke, folder = result.Folder, log = result.LogDirectory,
                mismatch = result.Mismatch?.Id, actual = result.ActualSha256,
            });
            output.Line($"R {result.Component} {(result.Success ? "ok" : result.Cancelled ? "cancelled" : "failed")} {catalog.Get(result.Component).Title}");
        }
        reporter.Tick(force: true);

        var failed = results.Where(r => !r.Success && !r.Cancelled).ToList();
        var cancelled = results.Any(r => r.Cancelled) || cts.IsCancellationRequested;
        if (failed.Count > 0)
            output.Error(Korean(catalog, failed));
        if (failed.Count == 0 && !cancelled && args.Contains("--clean"))
        {
            // the installs are in place; what was downloaded for them is not needed any more (Program Files stays small)
            try
            {
                installer.DeleteKeptFiles();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return output.End(failed.Count > 0 ? 1 : cancelled ? 2 : 0);
    }

    /// <summary>For Setup's message: which components did not get installed and the likely reason, in plain words.</summary>
    private static string Korean(ComponentCatalog catalog, IReadOnlyList<ComponentInstallResult> failed)
    {
        var names = string.Join(" · ", failed.Select(f => catalog.Get(f.Component).Title));
        var network = failed.Any(f => f.Error?.Contains("HttpRequest", StringComparison.Ordinal) == true || f.Error?.Contains("host", StringComparison.OrdinalIgnoreCase) == true
                                      || f.Error?.Contains("Response status code", StringComparison.Ordinal) == true || f.Error?.Contains("connection", StringComparison.OrdinalIgnoreCase) == true);
        var why = failed.Any(f => f.Mismatch is not null) ? "받은 파일이 앱이 확인한 파일과 달라요(공식 파일이 바뀌었을 수 있어요)."
            : network ? "인터넷에서 파일을 받지 못했어요."
            : "설치하는 중에 문제가 생겼어요.";
        return $"{names}: {why}";
    }

    private static int Try(HelperOutput output, Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            output.Error(e.Message);
            return 1;
        }
    }

    /// <summary>Cancels when the cancel file appears or the parent process ends.</summary>
    private static Timer? Watch(string? cancelFile, string? parent, CancellationTokenSource cts)
    {
        Process? process = null;
        if (int.TryParse(parent, out var pid))
        {
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { cts.Cancel(); }
        }
        if (cancelFile is null && process is null)
            return null;
        return new Timer(_ =>
        {
            try
            {
                if ((cancelFile is not null && File.Exists(cancelFile)) || process?.HasExited == true)
                    cts.Cancel();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
        }, null, 500, 500);
    }

    /// <summary>Progress into the JSON file (each report, a little thinned) and Setup's lines (four times a second).</summary>
    private sealed class Reporter(HelperOutput output, BatchProgress progress) : IProgress<ComponentProgress>
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string, string), (string Step, long At)> _last = [];
        private string _p = "", _t = "", _s = "";

        public void Report(ComponentProgress value)
        {
            progress.Report(value);
            var now = Environment.TickCount64;
            lock (_gate)
            {
                var key = (value.Component, value.Key);
                if (_last.TryGetValue(key, out var last) && last.Step == value.Step && now - last.At < 200 && value.Done != value.Total)
                    return;
                _last[key] = (value.Step, now);
            }
            output.Json(new { t = "p", c = value.Component, k = value.Key, s = value.Step, d = value.Done, n = value.Total, a = value.Ahead });
        }

        public void Tick(bool force = false)
        {
            string p = "P " + (int)Math.Floor(progress.Overall * 1000), t = "T " + progress.Headline, s = "S " + progress.DownloadLine;
            lock (_gate)
            {
                if (force || p != _p)
                    output.Line(_p = p);
                if (force || t != _t)
                    output.Line(_t = t);
                if (force || s != _s)
                    output.Line(_s = s);
            }
        }
    }
}

/// <summary>Where the helper writes: standard output and the lines file (Setup reads them) and the JSON lines file (the app reads it).</summary>
internal sealed class HelperOutput : IDisposable
{
    private readonly object _gate = new();
    private readonly Stream? _stdout;
    private readonly FileStream? _lines;
    private readonly StreamWriter? _json;

    public HelperOutput(string? progressFile, string? linesFile = null)
    {
        if (linesFile is not null)
        {
            // UTF-8 with a BOM, each line written whole: Setup reads the file while it grows (LoadStringsFromLockedFile)
            _lines = new FileStream(linesFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _lines.Write([0xEF, 0xBB, 0xBF]);
            _lines.Flush();
        }
        try
        {
            _stdout = Console.OpenStandardOutput();
            if (_stdout == Stream.Null)
                _stdout = null;
        }
        catch (IOException)
        {
            _stdout = null;
        }
        if (progressFile is not null)
            _json = new StreamWriter(new FileStream(progressFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Line(string line)
    {
        if (_stdout is null && _lines is null)
            return;
        var bytes = Encoding.UTF8.GetBytes(line.Replace('\n', ' ').Replace('\r', ' ') + "\r\n");
        lock (_gate)
        {
            foreach (var stream in new Stream?[] { _stdout, _lines })
            {
                if (stream is null)
                    continue;
                try
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                catch (IOException) { }
            }
        }
    }

    public void Json(object value)
    {
        if (_json is null)
            return;
        lock (_gate)
        {
            try
            {
                _json.WriteLine(JsonSerializer.Serialize(value));
            }
            catch (IOException) { }
        }
    }

    public void Error(string message)
    {
        Line("E " + message);
        Json(new { t = "error", message });
    }

    public int End(int code)
    {
        Json(new { t = "end", code });
        Line("X " + code);
        return code;
    }

    public void Dispose()
    {
        _json?.Dispose();
        _lines?.Dispose();
        _stdout?.Dispose();
    }
}
