using System.Security.Cryptography;
using System.Text;

namespace TopstToolkit.Core.Workspace;

public sealed record StagedFile(string Source, string Staged, string Sha256, long Size);

/// <param name="ListSha256">Hash of the staged set (names and contents) — part of the pipeline cache key (design §4.5).</param>
public sealed record ImageStagingResult(IReadOnlyList<StagedFile> Files, int DuplicatesSkipped, int UnsupportedSkipped, string ListSha256);

/// <summary>
/// Copies user files into the project with ASCII names (design §4.3). Copying fixes Korean/space paths and
/// the <c>.jpeg</c> double count in one go, and keeps the project reproducible.
/// </summary>
public static class FileStager
{
    /// <summary>Extensions the toolkit's Custom dataset reads (feature/04 §3.1).</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".ppm", ".pgm", ".tif", ".tiff", ".webp",
    };

    /// <summary>Copies one file into <paramref name="destinationDir"/> under an ASCII name.</summary>
    /// <param name="fileName">Name to use instead of the source's (sanitized as well).</param>
    public static StagedFile StageFile(string source, string destinationDir, string? fileName = null, string fallbackStem = "model")
    {
        source = Path.GetFullPath(source);
        if (!File.Exists(source))
            throw new FileNotFoundException("File to import was not found.", source);
        Directory.CreateDirectory(destinationDir);

        var target = Path.Combine(destinationDir, NameRules.ToAsciiFileName(fileName ?? Path.GetFileName(source), fallbackStem));
        var sourceHash = Sha256Of(source);
        if (!File.Exists(target) || !string.Equals(Sha256Of(target), sourceHash, StringComparison.Ordinal))
            File.Copy(source, target, overwrite: true);
        return new StagedFile(source, target, sourceHash, new FileInfo(target).Length);
    }

    /// <summary>
    /// Collects images from files and folders (recursively, in a stable order), skips identical contents and
    /// copies them flat into <paramref name="destinationDir"/> with unique ASCII stems. Unique stems matter:
    /// enlight_sim names each result folder after the image stem, so <c>a\dog.png</c> and <c>b\dog.jpg</c> would overwrite each other.
    /// </summary>
    /// <param name="limit">Stop after this many images (calibration needs a few hundred at most).</param>
    public static ImageStagingResult StageImages(IEnumerable<string> sources, string destinationDir, int? limit = null, bool clearDestination = true) =>
        StageDataFiles(sources, destinationDir, ImageExtensions, limit, clearDestination);

    /// <summary>Extensions of the toolkit's CustomNPY dataset (feature/04 §3.2): one float32 tensor per file.</summary>
    public static readonly IReadOnlySet<string> NpyExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".npy" };

    /// <summary>Same as <see cref="StageImages"/> for any set of data file extensions (for example <see cref="NpyExtensions"/>).</summary>
    public static ImageStagingResult StageDataFiles(IEnumerable<string> sources, string destinationDir, IReadOnlySet<string> extensions,
        int? limit = null, bool clearDestination = true)
    {
        Directory.CreateDirectory(destinationDir);
        if (clearDestination)
            foreach (var old in Directory.EnumerateFiles(destinationDir))
                File.Delete(old);

        var staged = new List<StagedFile>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var usedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0, unsupported = 0;

        foreach (var file in EnumerateCandidates(sources))
        {
            if (limit is { } max && staged.Count >= max)
                break;
            var ext = Path.GetExtension(file);
            if (!extensions.Contains(ext))
            {
                unsupported++;
                continue;
            }
            var hash = Sha256Of(file);
            if (!seenHashes.Add(hash))
            {
                duplicates++;
                continue;
            }

            var stem = NameRules.ToAsciiStem(Path.GetFileNameWithoutExtension(file), ReferenceEquals(extensions, ImageExtensions) ? "image" : "data");
            var unique = stem;
            for (var n = 2; !usedStems.Add(unique); n++)
                unique = $"{stem}_{n}";

            var target = Path.Combine(destinationDir, unique + NameRules.StagedImageExtension(ext));
            File.Copy(file, target, overwrite: true);
            staged.Add(new StagedFile(file, target, hash, new FileInfo(target).Length));
        }

        var listText = string.Join("\n", staged.Select(f => $"{Path.GetFileName(f.Staged)}:{f.Sha256}"));
        var listHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(listText)));
        return new ImageStagingResult(staged, duplicates, unsupported, listHash);
    }

    private static IEnumerable<string> EnumerateCandidates(IEnumerable<string> sources)
    {
        foreach (var source in sources)
        {
            var full = Path.GetFullPath(source);
            if (File.Exists(full))
            {
                yield return full;
            }
            else if (Directory.Exists(full))
            {
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    yield return f;
            }
            else
            {
                throw new FileNotFoundException("Image file or folder was not found.", full);
            }
        }
    }

    public static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
