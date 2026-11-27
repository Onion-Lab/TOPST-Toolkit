using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TopstToolkit.Core.Components;

namespace TopstToolkit.App.Services;

/// <summary>
/// How the app installs, repairs and removes components (design D12 and §7.2): in its own process when it can write the
/// components folder (the double-click folder, Setup's "나만 쓰기" install), otherwise through <see cref="InstallHelper"/>
/// started with administrator rights — Windows asks the user once — when the app was installed for all users under
/// Program Files. Either way the screen gets the same progress reports.
/// </summary>
public static class ComponentRuns
{
    /// <summary>Tests: start the helper without asking for administrator rights.</summary>
    public const string NoElevationVariable = "TOPST_HELPER_NO_ELEVATE";

    /// <summary>Tests: go through the helper even when the folder can be written (with <see cref="NoElevationVariable"/>, no question).</summary>
    public const string ForceHelperVariable = "TOPST_HELPER_FORCE";

    /// <summary>The components folder can only be changed with administrator rights.</summary>
    public static bool NeedsRights(ComponentInstaller installer) =>
        Environment.GetEnvironmentVariable(ForceHelperVariable) == "1" || !installer.CanWrite();

    /// <summary>
    /// The components one after another (the install screen shows one at a time). <paramref name="started"/> and
    /// <paramref name="progress"/> may be called from another thread. Null when the user said no to Windows' question.
    /// </summary>
    public static async Task<IReadOnlyList<ComponentInstallResult>?> InstallAsync(ComponentInstaller installer, IReadOnlyList<string> ids,
        Action<string> started, IProgress<ComponentProgress> progress, CancellationToken ct)
    {
        if (!NeedsRights(installer))
            return await Task.Run(() => ComponentBatch.RunAsync(installer, ids, together: false, started, progress, ct), CancellationToken.None);
        return await HelperAsync(installer, ["--components", string.Join(',', ids), "--sequential", "--force", "--clean"], started, progress, ct);
    }

    /// <summary>Removes a component; null when the user said no to Windows' question, else the error (empty when it worked).</summary>
    public static async Task<string?> RemoveAsync(ComponentInstaller installer, string id)
    {
        if (!NeedsRights(installer))
        {
            try
            {
                installer.Remove(id);
                return "";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return e.Message;
            }
        }
        return await SimpleAsync(installer, ["--remove-component", id]);
    }

    /// <summary>Deletes the downloads and caches; null when the user said no to Windows' question, else the error (empty when it worked).</summary>
    public static async Task<string?> DeleteKeptAsync(ComponentInstaller installer)
    {
        if (!NeedsRights(installer))
        {
            try
            {
                installer.DeleteKeptFiles();
                return "";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return e.Message;
            }
        }
        return await SimpleAsync(installer, ["--delete-kept-files"]);
    }

    private static async Task<string?> SimpleAsync(ComponentInstaller installer, IReadOnlyList<string> args)
    {
        string? error = null;
        var results = await HelperAsync(installer, args, _ => { }, new Progress<ComponentProgress>(), CancellationToken.None, message => error = message);
        return results is null ? null : error ?? "";
    }

    private static async Task<IReadOnlyList<ComponentInstallResult>?> HelperAsync(ComponentInstaller installer, IReadOnlyList<string> args,
        Action<string> started, IProgress<ComponentProgress> progress, CancellationToken ct, Action<string>? error = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "TOPST-Toolkit", "helper-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var progressFile = Path.Combine(folder, "progress.jsonl");
        var cancelFile = Path.Combine(folder, "cancel");
        File.WriteAllText(progressFile, "");
        string[] all = [InstallHelper.Switch, .. args, "--root", installer.Root, "--progress", progressFile, "--cancel", cancelFile,
                        "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)];
        var elevate = Environment.GetEnvironmentVariable(NoElevationVariable) != "1";
        var start = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TopstToolkit.exe"))
        {
            UseShellExecute = true, Verb = elevate ? "runas" : "", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory, Arguments = CommandLine(all),
        };
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("the helper did not start");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            TryDelete(folder);
            return null; // "아니요" in Windows' administrator question
        }

        var results = new List<ComponentInstallResult>();
        var ended = false;
        string? lastError = null;
        using var registration = ct.Register(() =>
        {
            try { File.WriteAllText(cancelFile, ""); }
            catch (IOException) { }
        });
        using (process)
        await using (var stream = new FileStream(progressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
        {
            var pending = new StringBuilder();
            var buffer = new char[16384];
            while (true)
            {
                var exited = process.HasExited;
                int read;
                while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
                    pending.Append(buffer, 0, read);
                for (var at = pending.ToString().IndexOf('\n'); at >= 0; at = pending.ToString().IndexOf('\n'))
                {
                    var line = pending.ToString(0, at).Trim();
                    pending.Remove(0, at + 1);
                    if (line.Length > 0)
                        Handle(line);
                }
                if (exited)
                    break;
                await Task.Delay(200, CancellationToken.None);
            }

            void Handle(string line)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var e = json.RootElement;
                    string? S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    long? L(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
                    bool B(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                    switch (S("t"))
                    {
                        case "start" when S("c") is { } id:
                            started(id);
                            break;
                        case "p" when S("c") is { } id && S("k") is { } key:
                            progress.Report(new ComponentProgress(id, key, S("s") ?? "", L("d"), L("n")) { Ahead = B("a") });
                            break;
                        case "r" when S("c") is { } id:
                            var mismatch = S("mismatch") is { } download && installer.Catalog.Components.FirstOrDefault(c => c.Id == id) is { } spec
                                ? spec.Downloads.FirstOrDefault(d => d.Id == download) : null;
                            results.Add(new ComponentInstallResult
                            {
                                Component = id, Error = B("ok") ? null : S("error") ?? "failed", Cancelled = B("cancelled"), FailedKey = S("key"),
                                Elapsed = TimeSpan.FromSeconds(e.TryGetProperty("elapsed", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0),
                                Smoke = S("smoke"), Folder = S("folder"), LogDirectory = S("log") ?? installer.LogsDirectory, Mismatch = mismatch, ActualSha256 = S("actual"),
                            });
                            break;
                        case "error":
                            lastError = S("message");
                            break;
                        case "end":
                            ended = true;
                            break;
                    }
                }
                catch (JsonException)
                {
                    // a line cut off by a crash: the exit code tells the rest
                }
            }
        }

        if (lastError is not null)
            error?.Invoke(lastError);
        if (!ended && error is not null)
            error("관리자 권한으로 실행한 설치 도우미가 끝까지 마치지 못했어요.");
        if (!ended && args.Contains("--components"))
        {
            // the helper stopped without a word (closed, crashed): the component it was on did not get installed
            var ids = args[args.ToList().IndexOf("--components") + 1].Split(',');
            if (ids.FirstOrDefault(id => results.All(r => r.Component != id)) is { } next)
                results.Add(new ComponentInstallResult
                {
                    Component = next, Error = "the installer helper with administrator rights stopped" + (lastError is null ? "" : ": " + lastError),
                    Cancelled = ct.IsCancellationRequested, LogDirectory = installer.LogsDirectory,
                });
        }
        TryDelete(folder);
        return results;
    }

    /// <summary>A Windows command line for these arguments (the rules <c>CommandLineToArgvW</c> reads them back with).</summary>
    internal static string CommandLine(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg;
        var text = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            text.Append('\\', ch == '"' ? backslashes * 2 + 1 : backslashes).Append(ch);
            backslashes = 0;
        }
        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
