using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopstToolkit.Core.Updates;

/// <summary>Why an update file, a download or an install was refused; <see cref="Message"/> is for the log, not the screen.</summary>
public sealed class UpdateException(UpdateProblem problem, string message) : Exception(message)
{
    public UpdateProblem Problem { get; } = problem;
}

/// <summary>What went wrong, for the screen to say it in its own words (design §7.5).</summary>
public enum UpdateProblem
{
    /// <summary>No answer from the update address: no internet, a proxy, the server is down, or nothing published there yet.</summary>
    Offline,
    /// <summary>The version file is not signed by a key this app trusts, or it was changed after it was signed.</summary>
    BadSignature,
    /// <summary>Signed, but not something this app can use (another product, an address that is not https ...).</summary>
    BadManifest,
    /// <summary>The downloaded installer is not the one the version file describes (size or SHA-256): not used, deleted.</summary>
    Mismatch,
    /// <summary>The download stopped (the line dropped, the disk is full); what arrived is kept for the next try.</summary>
    Interrupted,
    /// <summary>Setup ended before it began to install: the user said no to Windows' question, or it could not start.</summary>
    SetupNotStarted,
    /// <summary>The patch could not be put in place; every file was put back as it was (the installer is the way then).</summary>
    PatchFailed,
}

/// <summary>
/// The version file the publisher puts next to the installer (design §7.5): which version is out, where its installer is,
/// its size and SHA-256, and what changed. Published as two files at the update address: <c>update.json</c> (these bytes)
/// and <c>update.json.sig</c> (the publisher's signature over exactly those bytes, <see cref="UpdateSignature"/>).
/// </summary>
public sealed record UpdateManifest
{
    public const string ProductName = "TOPST Toolkit";
    public const string FileName = "update.json";
    public const string SignatureFileName = "update.json.sig";

    /// <summary>The largest version file the app reads (a few notes take a few hundred bytes).</summary>
    public const int MaxBytes = 64 * 1024;

    [JsonPropertyName("product")] public string Product { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    /// <summary>The day it came out, <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("released")] public string? Released { get; init; }
    [JsonPropertyName("installer")] public UpdateInstaller Installer { get; init; } = new();
    /// <summary>What changed, one short sentence each, as the screen shows them.</summary>
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>An https page to read more or to download by hand (the copy that was not installed by Setup).</summary>
    [JsonPropertyName("page")] public string? Page { get; init; }
    /// <summary>
    /// Only the files that changed since the version before, for an app that is exactly that version (design §7.5
    /// "차등 패치"); null when this release has no patch, and every app takes the installer.
    /// </summary>
    [JsonPropertyName("patch")] public UpdatePatch? Patch { get; init; }

    [JsonIgnore] public Version ParsedVersion => UpdateVersion.Parse(Version);

    /// <summary>The release day as the screen writes it ("12월 1일"), or null.</summary>
    [JsonIgnore]
    public DateTime? ReleasedOn => DateTime.TryParseExact(Released, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Korean notes stay readable in the file
    };

    /// <summary>The file the publisher signs (UTF-8, no BOM).</summary>
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>
    /// Reads and checks a version file. <paramref name="allowLoopbackHttp"/>: a test server on this PC may serve the
    /// installer over plain http; anything on the network must be https.
    /// </summary>
    public static UpdateManifest Parse(ReadOnlySpan<byte> bytes, bool allowLoopbackHttp = false)
    {
        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json);
        }
        catch (JsonException e)
        {
            throw new UpdateException(UpdateProblem.BadManifest, "update.json is not valid JSON: " + e.Message);
        }
        if (manifest is null)
            throw new UpdateException(UpdateProblem.BadManifest, "update.json is empty");
        manifest.Validate(allowLoopbackHttp);
        return manifest;
    }

    private void Validate(bool allowLoopbackHttp)
    {
        void Fail(string why) => throw new UpdateException(UpdateProblem.BadManifest, "update.json: " + why);
        if (Product != ProductName)
            Fail($"for another product ({Product})");
        if (!UpdateVersion.TryParse(Version, out _))
            Fail($"version '{Version}' is not a version");
        if (!IsAllowedAddress(Installer.Url, allowLoopbackHttp))
            Fail("the installer address must be https");
        if (Installer.Size is <= 0 or > 2L << 30)
            Fail($"installer size {Installer.Size}");
        if (Installer.Sha256.Length != 64 || !Installer.Sha256.All(Uri.IsHexDigit))
            Fail("installer sha256 is not 64 hex digits");
        if (Notes.Count > 20 || Notes.Any(n => n is null || n.Length > 300))
            Fail("too many or too long notes");
        if (Page is not null && !IsAllowedAddress(Page, allowLoopbackHttp: false))
            Fail("the page must be https");
        if (Released is not null && ReleasedOn is null)
            Fail($"released '{Released}' is not yyyy-MM-dd");
        if (Patch is { } patch)
        {
            if (!UpdateVersion.TryParse(patch.From, out var from) || from >= ParsedVersion)
                Fail($"the patch starts at '{patch.From}', not at an older version");
            if (!IsAllowedAddress(patch.Url, allowLoopbackHttp))
                Fail("the patch address must be https");
            if (patch.Size is <= 0 or > 1L << 30 || !IsSha256(patch.Sha256))
                Fail("patch size or sha256");
            if (patch.Files.Count is 0 or > 5000)
                Fail($"{patch.Files.Count} patch files");
            foreach (var file in patch.Files)
            {
                if (!UpdatePatch.IsSafePath(file.Path))
                    Fail($"patch path '{file.Path}'");
                // a patch only changes or removes files the installer put there; a new file needs the installer (its uninstaller knows it)
                if (!IsSha256(file.From) || (file.To is not null && !IsSha256(file.To)))
                    Fail($"patch hashes of '{file.Path}'");
            }
            if (patch.Files.Select(f => f.Path.ToLowerInvariant()).Distinct().Count() != patch.Files.Count)
                Fail("a patch path twice");
        }
    }

    private static bool IsSha256(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);

    /// <summary>https, or plain http to this PC when a test allows it.</summary>
    public static bool IsAllowedAddress(string? text, bool allowLoopbackHttp) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}

public sealed record UpdateInstaller
{
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    /// <summary>Lower-case hex.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
}

/// <summary>
/// A zip with the files that changed between <see cref="From"/> and the release (design §7.5 "차등 패치"). Each entry
/// says what the file must be now (<see cref="UpdatePatchFile.From"/>) and what it becomes (<see cref="UpdatePatchFile.To"/>,
/// null when it goes away); the zip holds the new files under the same paths.
/// </summary>
public sealed record UpdatePatch
{
    [JsonPropertyName("from")] public string From { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("files")] public IReadOnlyList<UpdatePatchFile> Files { get; init; } = [];

    /// <summary>A path inside the app folder: relative, '/' between parts, no "..", no drive or root.</summary>
    public static bool IsSafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/'))
            return false;
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return path.Split('/').All(part => part.Length > 0 && part != "." && part != ".." && part.IndexOfAny(invalid) < 0 && part.Trim() == part);
    }
}

public sealed record UpdatePatchFile
{
    /// <summary>Relative to the app folder, '/' between parts.</summary>
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    /// <summary>SHA-256 of the file in the version the patch starts at.</summary>
    [JsonPropertyName("from")] public string From { get; init; } = "";
    /// <summary>SHA-256 of the new file; null when the release no longer has it.</summary>
    [JsonPropertyName("to")] public string? To { get; init; }
}

/// <summary>Versions as the app shows them: major.minor.patch ("0.2.0"); a leading "v" and anything after '-' or '+' are ignored.</summary>
public static class UpdateVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0)
            s = s[..cut];
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4)
            return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return false;
            if (i < 3)
                numbers[i] = n;
        }
        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public static Version Parse(string? text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a version");

    /// <summary>"0.2.0" for the screen.</summary>
    public static string Show(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    /// <summary>Only a strictly newer version is offered: an old signed file served again cannot take the app back.</summary>
    public static bool IsNewer(string? offered, Version current) =>
        TryParse(offered, out var v) && v > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
}
