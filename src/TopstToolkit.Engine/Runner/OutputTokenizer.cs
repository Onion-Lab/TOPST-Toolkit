using System.Text;
using System.Text.RegularExpressions;

namespace TopstToolkit.Engine.Runner;

public enum TokenKind
{
    /// <summary>A finished console line (ended by <c>\n</c> or <c>\r\n</c>).</summary>
    Line,
    /// <summary>A progress rewrite: text that a lone <c>\r</c> is about to overwrite.</summary>
    Update,
}

public readonly record struct OutputToken(TokenKind Kind, string Text);

/// <summary>
/// Splits a decoded console stream the way a terminal shows it (feature/04 §5.3):
/// <c>\r\n</c> or <c>\n</c> ends a line, a lone <c>\r</c> rewrites the current line.
/// A <c>\r</c> at the end of a chunk is held until the next chunk shows whether <c>\n</c> follows.
/// When a line ends right after rewrites (<c>"[1 / 1]\r\r\n"</c>), the last rewrite becomes the line,
/// which is what the terminal would leave on screen. ANSI escape codes are removed.
/// </summary>
public sealed class OutputTokenizer
{
    private readonly StringBuilder _current = new();
    private string? _lastUpdate;
    private bool _pendingCr;

    public void Feed(ReadOnlySpan<char> chunk, List<OutputToken> sink)
    {
        foreach (char c in chunk)
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                if (c == '\n')
                {
                    EmitLine(sink);
                    continue;
                }
                EmitUpdate(sink);
            }

            switch (c)
            {
                case '\r':
                    _pendingCr = true;
                    break;
                case '\n':
                    EmitLine(sink);
                    break;
                default:
                    _current.Append(c);
                    break;
            }
        }
    }

    public void Feed(string chunk, List<OutputToken> sink) => Feed(chunk.AsSpan(), sink);

    /// <summary>Flushes what is left at end of stream.</summary>
    public void Complete(List<OutputToken> sink)
    {
        if (_pendingCr)
        {
            _pendingCr = false;
            EmitUpdate(sink);
        }
        if (_current.Length > 0 || _lastUpdate is not null)
            EmitLine(sink);
    }

    private void EmitUpdate(List<OutputToken> sink)
    {
        if (_current.Length == 0)
            return; // "\r\r": nothing new on the line
        var text = AnsiText.Strip(_current.ToString());
        _current.Clear();
        _lastUpdate = text;
        sink.Add(new OutputToken(TokenKind.Update, text));
    }

    private void EmitLine(List<OutputToken> sink)
    {
        var text = _current.Length > 0 ? AnsiText.Strip(_current.ToString()) : _lastUpdate ?? string.Empty;
        _current.Clear();
        _lastUpdate = null;
        sink.Add(new OutputToken(TokenKind.Line, text));
    }

    /// <summary>Convenience for tests and log replay: tokenizes a whole text at once.</summary>
    public static List<OutputToken> TokenizeAll(string text)
    {
        var tokenizer = new OutputTokenizer();
        var tokens = new List<OutputToken>();
        tokenizer.Feed(text, tokens);
        tokenizer.Complete(tokens);
        return tokens;
    }
}

public static partial class AnsiText
{
    // CSI sequences (ESC [ ... final) and two-character escapes. The toolkit colors sys.exit() messages
    // with ESC[38;5;208m ... ESC[0;0m (feature/04 §5.4).
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|[@-Z\\-_])")]
    private static partial Regex Escape();

    public static string Strip(string text) => text.Contains('\x1B') ? Escape().Replace(text, string.Empty) : text;
}
