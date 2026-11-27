using System.Security.Cryptography;
using System.Text;

namespace TopstToolkit.Core.Updates;

/// <summary>
/// The publisher's side (design §7.5): <c>update.json</c> for a new installer, and its signature. Used by
/// <c>TopstToolkit.Cli update manifest</c>, which <c>build\publish-app.ps1 -Installer</c> runs after it built Setup.
/// </summary>
public static class UpdatePublisher
{
    /// <summary>The default place of the private key: in the user's profile, not in the repository and not in AppData.</summary>
    public static string DefaultKeyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".topst-toolkit", "update-key.pem");

    /// <summary>
    /// The download address of an installer from the update address: on GitHub the "latest" address of the version file
    /// (<c>…/releases/latest/download/</c>) becomes the address of that version's release (<c>…/releases/download/v0.2.0/</c>),
    /// so a version file always points at its own installer; anywhere else the installer sits next to the version file.
    /// </summary>
    public static string InstallerUrl(string updateBaseUrl, string version, string fileName)
    {
        var baseUrl = updateBaseUrl.TrimEnd('/') + "/";
        const string latest = "/releases/latest/download/";
        var at = baseUrl.IndexOf(latest, StringComparison.OrdinalIgnoreCase);
        if (at >= 0 && baseUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            return baseUrl[..at] + "/releases/download/v" + version + "/" + Uri.EscapeDataString(fileName);
        return baseUrl + Uri.EscapeDataString(fileName);
    }

    /// <summary>Notes from a text file: one per line; empty lines and lines starting with '#' are skipped.</summary>
    public static IReadOnlyList<string> ReadNotes(string path) =>
        File.ReadAllLines(path, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? l[2..].Trim() : l).ToList();

    /// <summary>
    /// Writes <c>update.json</c> and <c>update.json.sig</c> into <paramref name="outDir"/> and checks them the way the app
    /// will. Refuses a key the app does not list in <paramref name="trusted"/>: nobody could install what it signs.
    /// </summary>
    public static UpdateManifest Write(string installer, string version, string installerUrl, IReadOnlyList<string> notes, string? page,
        ECDsa key, string outDir, IReadOnlyList<string>? trusted = null, DateTime? released = null)
    {
        trusted ??= UpdateKeys.Trusted;
        var publicKey = UpdateSignature.PublicKeyOf(key);
        if (!trusted.Contains(publicKey))
            throw new InvalidOperationException("This key is not one of the app's update keys (UpdateKeys.Trusted). The app would refuse what it signs.");
        var (size, sha) = UpdateChecker.Measure(installer);
        var manifest = new UpdateManifest
        {
            Product = UpdateManifest.ProductName,
            Version = UpdateVersion.Show(UpdateVersion.Parse(version)),
            Released = (released ?? DateTime.Today).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Installer = new UpdateInstaller { Url = installerUrl, Size = size, Sha256 = sha },
            Notes = notes,
            Page = string.IsNullOrWhiteSpace(page) ? null : page,
        };
        var bytes = manifest.ToBytes();
        // the same checks the app makes, before anything is published
        var parsed = UpdateManifest.Parse(bytes, allowLoopbackHttp: Uri.TryCreate(installerUrl, UriKind.Absolute, out var u) && u.IsLoopback);
        var signature = UpdateSignature.Sign(bytes, key);
        if (!UpdateSignature.Verify(bytes, signature, trusted))
            throw new CryptographicException("the signature does not verify");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, UpdateManifest.FileName), bytes);
        File.WriteAllText(Path.Combine(outDir, UpdateManifest.SignatureFileName), signature + "\n", Encoding.ASCII);
        return parsed;
    }
}
