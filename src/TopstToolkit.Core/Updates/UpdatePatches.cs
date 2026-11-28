using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TopstToolkit.Core.Components;

namespace TopstToolkit.Core.Updates;

/// <summary>One file of a released app folder: its path ('/' between parts), size and SHA-256.</summary>
public sealed record ReleaseFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// Every file of a released app folder (what Setup puts in Program Files, without its own <c>setup.json</c>), kept by the
/// publisher for each release so the next one can carry only what changed (<c>build\update\released\&lt;version&gt;.json</c>).
/// </summary>
public sealed record ReleaseFiles
{
    /// <summary>Written by the build for information; it differs in every build and is never patched.</summary>
    public static readonly IReadOnlyList<string> NotTracked = ["dist-manifest.json", ComponentsRoot.SetupMarker];

    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("files")] public IReadOnlyList<ReleaseFile> Files { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ReleaseFiles Measure(string folder, string version)
    {
        var root = Path.GetFullPath(folder);
        var files = new List<ReleaseFile>();
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (NotTracked.Contains(relative, StringComparer.OrdinalIgnoreCase))
                continue;
            var (size, sha) = UpdateChecker.Measure(full);
            files.Add(new ReleaseFile(relative, size, sha));
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ReleaseFiles { Version = UpdateVersion.Show(UpdateVersion.Parse(version)), Files = files };
    }

    public static ReleaseFiles Load(string path) =>
        JsonSerializer.Deserialize<ReleaseFiles>(File.ReadAllText(path, Encoding.UTF8), Json) ?? throw new InvalidDataException(path + " is empty");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
    }
}

/// <summary>
/// The publisher's side of a patch (design §7.5 "차등 패치"): which files changed between two releases, and the zip that
/// carries them. A patch is made only when the app folder kept its shape: no new files (the uninstaller would not know
/// them), the same .NET runtime and the same components catalog (Setup reinstalls components when that changes).
/// </summary>
public static class PatchBuilder
{
    /// <summary>The files to change, or why no patch is made (English, for the publisher's console).</summary>
    public sealed record Comparison(IReadOnlyList<UpdatePatchFile> Files, string? Refusal);

    public static Comparison Compare(ReleaseFiles from, ReleaseFiles to)
    {
        var before = from.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var after = to.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var added = after.Keys.Where(p => !before.ContainsKey(p)).ToList();
        if (added.Count > 0)
            return new([], $"new files ({string.Join(", ", added.Take(5))}{(added.Count > 5 ? " ..." : "")}): only Setup can add files");
        var files = new List<UpdatePatchFile>();
        foreach (var (path, old) in before.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!after.TryGetValue(path, out var now))
                files.Add(new UpdatePatchFile { Path = old.Path, From = old.Sha256, To = null });
            else if (!string.Equals(old.Sha256, now.Sha256, StringComparison.OrdinalIgnoreCase))
                files.Add(new UpdatePatchFile { Path = now.Path, From = old.Sha256, To = now.Sha256 });
        }
        if (files.Count == 0)
            return new([], "nothing changed");
        if (files.FirstOrDefault(f => f.Path.StartsWith("dotnet/", StringComparison.OrdinalIgnoreCase)) is { } runtime)
            return new([], $"the .NET runtime changed ({runtime.Path}): the installer carries it");
        if (files.FirstOrDefault(f => f.Path.StartsWith("engine/components/", StringComparison.OrdinalIgnoreCase)) is { } catalog)
            return new([], $"the components catalog changed ({catalog.Path}): Setup installs components again");
        return new(files, null);
    }

    /// <summary>Writes the zip with the new files (those with a <see cref="UpdatePatchFile.To"/>) from the release's folder.</summary>
    public static (long Size, string Sha256) WriteZip(string releaseFolder, IReadOnlyList<UpdatePatchFile> files, string zipPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in files.Where(f => f.To is not null))
            {
                var source = Path.Combine(releaseFolder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!string.Equals(UpdateChecker.Measure(source).Sha256, file.To, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{source} is not the file the comparison measured");
                zip.CreateEntryFromFile(source, file.Path, CompressionLevel.SmallestSize);
            }
        }
        return UpdateChecker.Measure(zipPath);
    }

    public static string FileName(string from, string to) =>
        $"TOPST-Toolkit-Patch-{UpdateVersion.Show(UpdateVersion.Parse(from))}-{UpdateVersion.Show(UpdateVersion.Parse(to))}.zip";
}

/// <summary>
/// Puts a patch in place in the app folder (design §7.5 "차등 패치"). The app may be running from these files: Windows
/// lets a file in use be renamed, not replaced, so each current file is moved into <c>.update-old</c> (the same volume)
/// and the new one moved in; the running app keeps the old code until it starts again. Anything that goes wrong puts
/// every file back as it was. The folder <c>.update-old</c> is removed on the next start that can write the folder, by
/// the next patch, or by Setup.
/// </summary>
public static class PatchApplier
{
    public const string OldFolder = ".update-old";
    public const string StagingFolder = ".update-staging";

    /// <summary>
    /// Null when this app folder can take the patch: it is the version the patch starts at, and every file the patch
    /// changes or removes is the one the publisher measured (or already the new one). Otherwise what is in the way.
    /// </summary>
    public static string? CheckApplicable(string appDir, Version current, UpdatePatch patch)
    {
        if (!UpdateVersion.TryParse(patch.From, out var from) || from != new Version(current.Major, current.Minor, Math.Max(0, current.Build)))
            return $"this app is {UpdateVersion.Show(current)}, the patch starts at {patch.From}";
        foreach (var file in patch.Files)
        {
            var path = Resolve(appDir, file.Path);
            if (!File.Exists(path))
            {
                if (file.To is null)
                    continue; // already gone
                return $"{file.Path} is missing";
            }
            var sha = UpdateChecker.Measure(path).Sha256;
            if (!Same(sha, file.From) && !Same(sha, file.To))
                return $"{file.Path} is not the file of {patch.From}";
        }
        return null;
    }

    /// <summary>
    /// Checks the zip against the signed <paramref name="manifest"/>, then swaps the files. Throws
    /// <see cref="UpdateException"/> (<see cref="UpdateProblem.PatchFailed"/> or <see cref="UpdateProblem.Mismatch"/>)
    /// with every file back in its place.
    /// </summary>
    public static void Apply(string appDir, string zipPath, UpdateManifest manifest)
    {
        var patch = manifest.Patch ?? throw new UpdateException(UpdateProblem.PatchFailed, "the release has no patch");
        appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDir));
        CleanOld(appDir);
        var staging = Path.Combine(appDir, StagingFolder);
        var old = Path.Combine(appDir, OldFolder);

        // the zip is read once, from a stream nobody else can change while it is open, after its hash was checked
        try
        {
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (stream.Length != patch.Size || !Same(sha, patch.Sha256))
                throw new UpdateException(UpdateProblem.Mismatch, "the patch is not the one update.json describes");
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var file in patch.Files.Where(f => f.To is not null))
            {
                var entry = zip.GetEntry(file.Path) ?? throw new UpdateException(UpdateProblem.Mismatch, $"the patch lacks {file.Path}");
                var target = Resolve(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                if (!Same(UpdateChecker.Measure(target).Sha256, file.To))
                    throw new UpdateException(UpdateProblem.Mismatch, $"{file.Path} in the patch is not the signed file");
            }
        }
        catch (UpdateException)
        {
            TryDeleteFolder(staging);
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDeleteFolder(staging);
            throw new UpdateException(UpdateProblem.PatchFailed, "the patch could not be unpacked: " + e.Message);
        }

        var moved = new List<(string Current, string Aside)>();
        var placed = new List<string>();
        var at = ""; // the file being swapped, for the error (an IOException does not always name it)
        try
        {
            foreach (var file in patch.Files)
            {
                at = file.Path;
                var current = Resolve(appDir, file.Path);
                if (File.Exists(current) && file.To is not null && Same(UpdateChecker.Measure(current).Sha256, file.To))
                    continue; // already the new file
                if (File.Exists(current))
                {
                    var aside = Resolve(old, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                    File.Move(current, aside, overwrite: true);
                    moved.Add((current, aside));
                }
                if (file.To is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                    File.Move(Resolve(staging, file.Path), current);
                    placed.Add(current);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var path in Enumerable.Reverse(placed))
                TryDelete(path);
            foreach (var (current, aside) in Enumerable.Reverse(moved))
            {
                try
                {
                    File.Move(aside, current, overwrite: true);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    throw new UpdateException(UpdateProblem.PatchFailed, $"{at}: {e.Message}; and {current} could not be put back: {again.Message}");
                }
            }
            TryDeleteFolder(staging);
            throw new UpdateException(UpdateProblem.PatchFailed, $"{at}: {e.Message}");
        }
        TryDeleteFolder(staging);
        RecordVersion(appDir, manifest.Version);
    }

    /// <summary>The leftovers of an earlier patch; quietly kept while they cannot be deleted (in use, no rights).</summary>
    public static void CleanOld(string appDir)
    {
        TryDeleteFolder(Path.Combine(appDir, OldFolder));
        TryDeleteFolder(Path.Combine(appDir, StagingFolder));
    }

    /// <summary>Whether this process may change files in the folder (Program Files needs administrator rights).</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The new version in <c>setup.json</c> and in Windows' list of installed apps ("설정 › 앱").</summary>
    private static void RecordVersion(string appDir, string version)
    {
        var marker = Path.Combine(appDir, ComponentsRoot.SetupMarker);
        try
        {
            if (File.Exists(marker) && JsonNode.Parse(File.ReadAllText(marker, Encoding.UTF8)) is JsonObject json)
            {
                json["version"] = version;
                File.WriteAllText(marker, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        if (!OperatingSystem.IsWindows())
            return;
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default),
                     (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
                     (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32) })
        {
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view).OpenSubKey(uninstall);
                if (root is null)
                    continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(name);
                    if (entry?.GetValue("InstallLocation") is not string location
                        || !string.Equals(Path.TrimEndingDirectorySeparator(location), appDir, StringComparison.OrdinalIgnoreCase))
                        continue;
                    using var writable = root.OpenSubKey(name, writable: true);
                    writable?.SetValue("DisplayVersion", version);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // the list shows the old number until the next Setup; the app itself is new
            }
        }
    }

    /// <summary>A patch path under <paramref name="root"/>; refuses anything that would leave it.</summary>
    private static string Resolve(string root, string relative)
    {
        if (!UpdatePatch.IsSafePath(relative))
            throw new UpdateException(UpdateProblem.BadManifest, $"patch path '{relative}'");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateProblem.BadManifest, $"patch path '{relative}' leaves the folder");
        return full;
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
