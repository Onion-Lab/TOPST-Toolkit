using System.Text;
using System.Text.RegularExpressions;

namespace TopstToolkit.Core.Workspace;

/// <summary>
/// Naming rules for everything the toolkit sees (design §4.3, feature/09 §4):
/// the converter's model path and the <c>.enlight</c> file name must be ASCII.
/// </summary>
public static partial class NameRules
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int MaxStemLength = 64;

    public static bool IsAscii(string value) => value.All(c => c < 128);

    /// <summary>
    /// Keeps <c>[A-Za-z0-9._-]</c>, turns everything else (Korean, spaces, symbols) into <c>_</c>,
    /// collapses repeats and trims the ends. Falls back to <paramref name="fallback"/> when nothing is left.
    /// <c>"모바일넷 v2"</c> → <c>"v2"</c>, <c>"모델"</c> → <c>"model"</c>.
    /// </summary>
    public static string ToAsciiStem(string stem, string fallback = "model")
    {
        var sb = new StringBuilder(stem.Length);
        foreach (var c in stem)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        var s = Underscores().Replace(sb.ToString(), "_").Trim('_', '.', '-');
        if (s.Length > MaxStemLength)
            s = s[..MaxStemLength].TrimEnd('_', '.', '-');
        if (s.Length == 0)
            s = fallback;
        if (ReservedDeviceNames.Contains(s))
            s += "_";
        return s;
    }

    /// <summary>ASCII file name with a lower-case extension. Extensions with non-ASCII characters are dropped.</summary>
    public static string ToAsciiFileName(string fileName, string fallbackStem = "model")
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (ext.Length > 0 && (!IsAscii(ext) || ext.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '.'))))
        {
            stem = fileName;
            ext = string.Empty;
        }
        return ToAsciiStem(stem, fallbackStem) + ext.ToLowerInvariant();
    }

    /// <summary>
    /// Image extension for staging: lower case, and <c>.jpeg</c> becomes <c>.jpg</c> — the toolkit globs
    /// <c>*.jpeg</c> and <c>*.JPEG</c> separately and Windows matches both, so every .jpeg would be read twice (feature/09 §4 #7).
    /// </summary>
    public static string StagedImageExtension(string extension)
    {
        var e = extension.ToLowerInvariant();
        return e == ".jpeg" ? ".jpg" : e;
    }

    /// <summary>
    /// Why a workspace location cannot be used, in plain Korean, or null when it is fine.
    /// Long roots leave no room under the 260-character path limit for project, job and output folders.
    /// </summary>
    public static string? WorkspaceRootProblem(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            return "전체 경로(예: D:\\TOPST-Work)로 지정해 주세요.";
        if (!IsAscii(path))
            return "작업 공간 경로에는 영문·숫자만 쓸 수 있어요. 한글이 없는 위치를 골라 주세요.";
        if (path.Length > 100)
            return "작업 공간 경로가 너무 길어요. 100자 이하인 위치를 골라 주세요.";
        return null;
    }

    [GeneratedRegex("_{2,}")]
    private static partial Regex Underscores();
}
