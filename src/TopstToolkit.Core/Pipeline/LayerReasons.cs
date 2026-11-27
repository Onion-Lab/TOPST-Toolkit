using System.Text.RegularExpressions;

namespace TopstToolkit.Core.Pipeline;

/// <summary>
/// The converter's reasons for a layer the NPU cannot run, in plain Korean where the wording is known
/// (feature/06 §3, feature/09 §3). An unknown wording is shown as the converter wrote it.
/// </summary>
public static partial class LayerReasons
{
    /// <summary>One sentence, for tables and for the note next to an operator.</summary>
    public static string Explain(string type, string description)
    {
        var d = description ?? "";
        Match m;
        if (NotSupported().IsMatch(d))
            return $"NPU가 지원하지 않는 연산이에요 ({type})";
        if (ConcatDim().IsMatch(d))
            return "NPU가 지원하지 않는 방향으로 값을 이어 붙여요";
        if (Perms().IsMatch(d))
            return "NPU가 지원하지 않는 순서로 축을 바꿔요"; // the permutations themselves are in the converter's line (tooltip)
        if ((m = SliceStart().Match(d)).Success)
            return $"채널 {m.Groups[1].Value}번부터 잘라내는데, NPU는 {m.Groups[2].Value}의 배수 위치에서만 자를 수 있어요";
        return d;
    }

    /// <summary>A few words, for narrow lists.</summary>
    public static string Brief(string type, string description)
    {
        var d = description ?? "";
        if (NotSupported().IsMatch(d))
            return "지원하지 않는 연산";
        if (ConcatDim().IsMatch(d))
            return "채널 아닌 방향으로 이음";
        if (Perms().IsMatch(d))
            return "지원하지 않는 축 순서";
        var m = SliceStart().Match(d);
        if (m.Success)
            return $"{m.Groups[2].Value}의 배수 아닌 위치";
        return d;
    }

    [GeneratedRegex("not supported in ENLIGHT NPU", RegexOptions.IgnoreCase)]
    private static partial Regex NotSupported();

    // dim:2 not the same as 1. / dim:-1 not the same as 1.
    [GeneratedRegex(@"dim:\s*-?\d+ not the same", RegexOptions.IgnoreCase)]
    private static partial Regex ConcatDim();

    // perms:[0, 2, 1, 3] not the same as [0, 2, 3, 1].
    [GeneratedRegex(@"perms:\s*\[([^\]]*)\] not the same as \[([^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Perms();

    // Slice is not supported for channel start value:2 that is not multiple of 32.
    [GeneratedRegex(@"channel start value:\s*(\d+) that is not multiple of (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SliceStart();
}
