using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace TopstToolkit.Core.Updates;

/// <summary>
/// The publisher's stamp on <c>update.json</c> (design §7.5): ECDSA P-256 over SHA-256 of the exact file bytes, written
/// to <c>update.json.sig</c> as base64 (r‖s, 64 bytes). The private key stays on the publisher's PC; the app carries the
/// public keys (<see cref="UpdateKeys"/>). It does not need a code-signing certificate: whoever can change the files on
/// the update address still cannot make the app install something the publisher did not sign.
/// </summary>
public static class UpdateSignature
{
    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string Sign(ReadOnlySpan<byte> data, ECDsa key) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>True when <paramref name="signatureText"/> is a signature over <paramref name="data"/> by one of <paramref name="publicKeys"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> data, string? signatureText, IEnumerable<string> publicKeys)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String((signatureText ?? "").Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        if (signature.Length != 64)
            return false;
        foreach (var publicKey in publicKeys)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
                if (key.KeySize == 256 && key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    return true;
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                // a key that does not read is not one that signed
            }
        }
        return false;
    }

    /// <summary>The public half as the app lists it (base64 of SubjectPublicKeyInfo, one line).</summary>
    public static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>Reads a private key file (PKCS#8 PEM, as <see cref="WriteKeyFile"/> writes it).</summary>
    public static ECDsa ReadKeyFile(string path)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(path, Encoding.ASCII));
        return key;
    }

    /// <summary>
    /// Writes a new private key where only the current Windows user can read it. Refuses to replace a file: a lost key means
    /// the installed apps can no longer take updates, so it is never overwritten by accident.
    /// </summary>
    public static void WriteKeyFile(string path, ECDsa key)
    {
        if (File.Exists(path))
            throw new IOException($"{path} already exists; it is not replaced");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var pem = key.ExportPkcs8PrivateKeyPem();
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.ASCII.GetBytes(pem + "\n");
            file.Write(bytes);
        }
        if (OperatingSystem.IsWindows())
        {
            // only this user (and SYSTEM, which can read everything anyway)
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var me = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }
}

/// <summary>
/// The publisher's public keys: an update is used only when <c>update.json</c> carries a signature by one of them
/// (design §7.5). The private key of the first one is on the publisher's PC (<c>%USERPROFILE%\.topst-toolkit\update-key.pem</c>,
/// made by <c>TopstToolkit.Cli update key</c>). A second key can be added here before the first is retired: the version that
/// carries both is signed with the old key, the ones after it with the new.
/// </summary>
public static class UpdateKeys
{
    public static readonly IReadOnlyList<string> Trusted =
    [
        // made 2026-11-26 on the publisher's PC (update-key.pem)
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAErx+iDvmHiH84/naoSmeypC6UmtYWU0HWKt5ERJDHIRYXcs3Lp1eMZwSjwtdIzTGo6zLAq0zSvZTS0zXK6rHBRw==",
    ];
}

/// <summary>
/// Where the app looks for <c>update.json</c> (design §7.5): the address built into the app — the MSBuild property
/// <c>TopstUpdateUrl</c> in Directory.Build.props, kept as assembly metadata — or <see cref="UrlVariable"/> (tests). Null
/// when there is none, and then the app has no update screens at all.
/// </summary>
public sealed record UpdateSource(Uri BaseUrl, IReadOnlyList<string> Keys, bool IsLocalTest)
{
    public const string UrlVariable = "TOPST_UPDATE_URL";

    /// <summary>Tests only: one more public key, honoured only when the address is a server on this PC (http://127.0.0.1).</summary>
    public const string TestKeyVariable = "TOPST_UPDATE_TEST_KEY";

    public const string MetadataKey = "TopstUpdateUrl";

    public Uri ManifestUrl => new(BaseUrl, UpdateManifest.FileName);
    public Uri SignatureUrl => new(BaseUrl, UpdateManifest.SignatureFileName);

    public static UpdateSource? Find(Func<string, string?>? getEnvironmentVariable = null, string? builtIn = null, IReadOnlyList<string>? trusted = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var text = getEnvironmentVariable(UrlVariable) is { Length: > 0 } fromTest ? fromTest : builtIn ?? BuiltInUrl();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (!text.EndsWith('/'))
            text += "/";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
            return null;
        var local = url.Scheme == Uri.UriSchemeHttp && url.IsLoopback;
        if (url.Scheme != Uri.UriSchemeHttps && !local)
            return null;
        var keys = (trusted ?? UpdateKeys.Trusted).ToList();
        if (local && getEnvironmentVariable(TestKeyVariable) is { Length: > 0 } testKey)
            keys.Add(testKey.Trim());
        return keys.Count == 0 ? null : new UpdateSource(url, keys, local);
    }

    /// <summary>The address this build was made with; empty in development builds.</summary>
    public static string? BuiltInUrl() =>
        typeof(UpdateSource).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value;
}
