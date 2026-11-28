using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TopstToolkit.Core.Updates;

namespace TopstToolkit.App.Services;

/// <summary>
/// How a patch ended: in place, the user said no to Windows' question, or it failed (every file back as it was).
/// <see cref="ByHelper"/>: the helper with administrator rights did it (an app in Program Files).
/// </summary>
public sealed record PatchRunResult(bool Ok, bool Declined = false, UpdateProblem? Problem = null, string? Error = null, bool ByHelper = false);

/// <summary>
/// Puts a patch in place (design §7.5 "차등 패치"): in this process when the app folder can be written (나만 쓰기), else
/// through <c>TopstToolkit.exe --apply-patch</c> started with administrator rights — Windows asks once — for an app in
/// Program Files. The helper checks the signed <c>update.json</c> and the zip again itself: it must never put in
/// Program Files what another program changed in the user's folder after this app checked it.
/// <code>
///   --apply-patch --zip &lt;patch.zip&gt; --manifest &lt;update.json&gt; --signature &lt;update.json.sig&gt; --app &lt;folder&gt; --result &lt;file&gt;
/// </code>
/// </summary>
public static class PatchRuns
{
    public const string Switch = "--apply-patch";

    public static async Task<PatchRunResult> ApplyAsync(string appDir, string zip, UpdateManifest manifest, byte[] manifestBytes, string signature)
    {
        var useHelper = Environment.GetEnvironmentVariable(ComponentRuns.ForceHelperVariable) == "1" || !PatchApplier.CanWrite(appDir);
        if (!useHelper)
        {
            try
            {
                await Task.Run(() => PatchApplier.Apply(appDir, zip, manifest));
                return new PatchRunResult(true);
            }
            catch (UpdateException e)
            {
                return new PatchRunResult(false, Problem: e.Problem, Error: e.Message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new PatchRunResult(false, Problem: UpdateProblem.PatchFailed, Error: e.Message);
            }
        }

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "TOPST-Toolkit", "patch-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var manifestFile = Path.Combine(folder, UpdateManifest.FileName);
        var signatureFile = Path.Combine(folder, UpdateManifest.SignatureFileName);
        var resultFile = Path.Combine(folder, "result.json");
        await File.WriteAllBytesAsync(manifestFile, manifestBytes);
        await File.WriteAllTextAsync(signatureFile, signature, Encoding.ASCII);
        var elevate = Environment.GetEnvironmentVariable(ComponentRuns.NoElevationVariable) != "1";
        var start = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TopstToolkit.exe"))
        {
            UseShellExecute = true, Verb = elevate ? "runas" : "", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = appDir,
            Arguments = ComponentRuns.CommandLine([Switch, "--zip", zip, "--manifest", manifestFile, "--signature", signatureFile,
                                                  "--app", appDir, "--result", resultFile]),
        };
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("the helper did not start");
            await process.WaitForExitAsync();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            TryDeleteFolder(folder);
            return new PatchRunResult(false, Declined: true, ByHelper: true); // "아니요" in Windows' administrator question
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            TryDeleteFolder(folder);
            return new PatchRunResult(false, Problem: UpdateProblem.PatchFailed, Error: "the helper did not start: " + e.Message, ByHelper: true);
        }
        PatchRunResult result;
        try
        {
            result = JsonSerializer.Deserialize<PatchRunResult>(await File.ReadAllTextAsync(resultFile, Encoding.UTF8))
                     ?? new PatchRunResult(false, Problem: UpdateProblem.PatchFailed, Error: "the helper wrote nothing");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            result = new PatchRunResult(false, Problem: UpdateProblem.PatchFailed, Error: "the helper ended without a result: " + e.Message);
        }
        TryDeleteFolder(folder);
        return result with { ByHelper = true };
    }

    /// <summary>The helper (<see cref="Switch"/>), with or without administrator rights. Exit code 0 when the patch is in place.</summary>
    public static int RunHelper(IReadOnlyList<string> args)
    {
        string? Option(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Count).Select(x => args[x.i + 1]).LastOrDefault();
        var resultFile = Option("--result");
        PatchRunResult result;
        try
        {
            if (Option("--zip") is not { } zip || Option("--manifest") is not { } manifestFile || Option("--signature") is not { } signatureFile
                || Option("--app") is not { } appDir)
                throw new UpdateException(UpdateProblem.PatchFailed, "missing arguments");
            var bytes = File.ReadAllBytes(manifestFile);
            var signature = File.ReadAllText(signatureFile, Encoding.ASCII);
            var source = UpdateSource.Find();
            var keys = source?.Keys ?? UpdateKeys.Trusted;
            if (!UpdateSignature.Verify(bytes, signature, keys))
                throw new UpdateException(UpdateProblem.BadSignature, "update.json is not signed by a key this app trusts");
            var manifest = UpdateManifest.Parse(bytes, allowLoopbackHttp: source?.IsLocalTest == true);
            PatchApplier.Apply(appDir, zip, manifest);
            result = new PatchRunResult(true);
        }
        catch (UpdateException e)
        {
            result = new PatchRunResult(false, Problem: e.Problem, Error: e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            result = new PatchRunResult(false, Problem: UpdateProblem.PatchFailed, Error: e.Message);
        }
        if (resultFile is not null)
        {
            try
            {
                File.WriteAllText(resultFile, JsonSerializer.Serialize(result), new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result.Ok ? 0 : 1;
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
