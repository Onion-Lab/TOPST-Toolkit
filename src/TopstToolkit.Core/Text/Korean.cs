namespace TopstToolkit.Core.Text;

/// <summary>
/// Korean particles that depend on the word before them: "엔진을 설치하는 중", "보드 도구를 설치하는 중", "엔진과 보드 도구".
/// A word that does not end in a Hangul syllable (a number, a Latin name) gets both forms, "net.so을(를)".
/// </summary>
public static class Korean
{
    /// <summary>"엔진을", "보드 도구를".</summary>
    public static string Object(string word) => word + Pick(word, "을", "를", "을(를)");

    /// <summary>"엔진과", "보드 도구와".</summary>
    public static string And(string word) => word + Pick(word, "과", "와", "과(와)");

    /// <summary>"엔진이", "보드 도구가".</summary>
    public static string Subject(string word) => word + Pick(word, "이", "가", "이(가)");

    /// <summary>"엔진과 보드 도구", "엔진, 보드 도구와 모델 모음".</summary>
    public static string List(IReadOnlyList<string> words) => words.Count switch
    {
        0 => "",
        1 => words[0],
        _ => string.Join(", ", words.Take(words.Count - 2)) + (words.Count > 2 ? ", " : "") + And(words[^2]) + " " + words[^1],
    };

    /// <summary>
    /// A version as it is read aloud, by its last digit (영 · 일 · 이 · 삼 …): "0.2.0을", "0.1.2를" (app updates, design §7.5;
    /// "0.1.1으로" turned up in the update test 2026-11-26).
    /// </summary>
    public static string VersionObject(string version) =>
        version + (DigitFinal(version) is { } final ? (final == 0 ? "를" : "을") : "을(를)");

    /// <summary>"0.2.0으로", "0.1.1로": after no final consonant or ㄹ the particle is 로.</summary>
    public static string VersionToward(string version) =>
        version + (DigitFinal(version) is { } final ? (final is 0 or LieulFinal ? "로" : "으로") : "(으)로");

    private const int LieulFinal = 8; // ㄹ among the final consonants of a Hangul syllable

    /// <summary>The final consonant (0 = none) of the last digit as it is read; null when the text does not end in a digit.</summary>
    private static int? DigitFinal(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0 || !char.IsAsciiDigit(trimmed[^1]))
            return null;
        var reading = "영일이삼사오육칠팔구"[trimmed[^1] - '0'];
        return (reading - 0xAC00) % 28;
    }

    /// <summary>Whether the last syllable has a final consonant (받침); null when the word does not end in Hangul.</summary>
    public static bool? EndsWithConsonant(string word)
    {
        var text = word.TrimEnd();
        if (text.Length == 0)
            return null;
        var last = text[^1];
        if (last is < '가' or > '힣')
            return null;
        return (last - 0xAC00) % 28 != 0;
    }

    private static string Pick(string word, string afterConsonant, string afterVowel, string unknown) => EndsWithConsonant(word) switch
    {
        true => afterConsonant,
        false => afterVowel,
        null => unknown,
    };
}
