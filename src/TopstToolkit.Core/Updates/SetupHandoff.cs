using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TopstToolkit.Core.Components;

namespace TopstToolkit.Core.Updates;

/// <summary>What Setup wrote about this copy of the app (<c>setup.json</c> next to <c>TopstToolkit.exe</c>, design §7.2).</summary>
/// <param name="AllUsers">Installed for everyone (Program Files, administrator); null when an older Setup did not say.</param>
public sealed record SetupRecord(bool? AllUsers, string? Version)
{
    /// <summary>Null when Setup did not install this copy (the double-click folder, a development build).</summary>
    public static SetupRecord? Read(string? appDirectory = null)
    {
        var path = Path.Combine(appDirectory ?? AppContext.BaseDirectory, ComponentsRoot.SetupMarker);
        if (!File.Exists(path))
            return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = json.RootElement;
            bool? allUsers = root.TryGetProperty("allUsers", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null;
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new SetupRecord(allUsers, version);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SetupRecord(null, null);
        }
    }
}

/// <summary>How the hand-over to Setup ended.</summary>
/// <param name="Started">Setup is installing (with administrator rights when it needs them): the app must close now.</param>
/// <param name="ExitCode">Setup's exit code when it ended before it started to install.</param>
public sealed record HandoffResult(bool Started, int? ExitCode = null);

/// <summary>
/// The app hands over to the downloaded Setup (design §7.5): it runs it quietly with a progress window
/// (<c>/SILENT</c>), in the install mode Setup recorded, and waits until Setup says it has started — after Windows' question
/// for an install in Program Files — before it closes itself. Setup waits for the app to be gone before it replaces its
/// files, installs, and starts the app again as the user (not as administrator) whether it succeeded or not; the app then
/// reads <see cref="PendingUpdate"/> to tell which.
/// </summary>
public static class SetupHandoff
{
    /// <summary>Tests: more Setup arguments (<c>/SKIPCOMPONENTS=1</c> so no component is downloaded).</summary>
    public const string ExtraArgsVariable = "TOPST_UPDATE_SETUP_ARGS";

    /// <summary>The command line for Setup. Each value is a file path, which never holds a quote.</summary>
    public static string Arguments(SetupRecord record, string signalFile, string appExe, int appProcessId, string logFile, string? extra = null)
    {
        var args = new List<string> { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/SP-" };
        // the same install mode as before; without it Setup would decide by itself (UsePreviousPrivileges)
        if (record.AllUsers is { } allUsers)
            args.Add(allUsers ? "/ALLUSERS" : "/CURRENTUSER");
        args.Add($"/LOG=\"{logFile}\"");
        args.Add($"/UPDATESIGNAL=\"{signalFile}\"");
        args.Add($"/RELAUNCH=\"{appExe}\"");
        args.Add("/WAITPID=" + appProcessId.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(extra))
            args.Add(extra.Trim());
        return string.Join(' ', args);
    }

    /// <summary>
    /// Starts Setup and waits until it has started to install (it writes <paramref name="signalFile"/>), or until it ends
    /// without starting. The installer is checked against <paramref name="manifest"/> once more and held open so nothing
    /// can change it in between.
    /// </summary>
    public static async Task<HandoffResult> StartAsync(string installer, UpdateManifest manifest, string arguments, string signalFile,
        CancellationToken ct = default, Func<ProcessStartInfo, Process?>? start = null)
    {
        using var hold = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(hold, ct).ConfigureAwait(false));
        if (hold.Length != manifest.Installer.Size || !string.Equals(sha, manifest.Installer.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateProblem.Mismatch, "the installer changed after it was downloaded");
        try
        {
            File.Delete(signalFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        Process? process;
        try
        {
            process = (start ?? Process.Start)(new ProcessStartInfo(installer, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(installer)!,
            });
        }
        catch (Win32Exception e)
        {
            throw new UpdateException(UpdateProblem.SetupNotStarted, e.Message);
        }
        if (process is null)
            throw new UpdateException(UpdateProblem.SetupNotStarted, "Setup did not start");
        using (process)
        {
            while (true)
            {
                if (File.Exists(signalFile))
                    return new HandoffResult(true);
                if (process.HasExited)
                {
                    // the signal may have been written just before Setup ended
                    await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
                    return File.Exists(signalFile) ? new HandoffResult(true) : new HandoffResult(false, process.ExitCode);
                }
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
    }
}
