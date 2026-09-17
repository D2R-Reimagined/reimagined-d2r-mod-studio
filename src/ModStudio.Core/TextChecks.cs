namespace ModStudio.Core;

/// <summary>
/// Checks on a piece of text as it is typed: cell length against the game's limit, bracket balance, and the partner of a
/// bracket at a caret. Formula cells (calc columns, skill descriptions) are the ones people used to paste into another editor
/// to count characters and match parentheses.
/// </summary>
public static class TextChecks
{
    /// <summary>The game truncates a TXT cell past this many characters.</summary>
    public const int GameCellLimit = 255;

    /// <summary>"123 / 255 chars" plus the bracket verdict; the length is flagged once it passes the game's limit.</summary>
    public static string Describe(string? text)
    {
        text ??= "";
        var length = text.Length > GameCellLimit ? $"{text.Length} / {GameCellLimit} chars — over the game's limit, the value will be cut off" : $"{text.Length} / {GameCellLimit} chars";
        var brackets = BracketProblem(text);
        bool hasBrackets = text.IndexOfAny(['(', ')', '[', ']', '{', '}']) >= 0;
        return length + (brackets != null ? " · " + brackets : hasBrackets ? " · brackets balanced" : "");
    }

    /// <summary>Null when every ( [ { has its partner in the right order; otherwise a short description of the first mismatch.</summary>
    public static string? BracketProblem(string text)
    {
        var open = new Stack<(char Bracket, int Index)>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{') { open.Push((c, i)); continue; }
            if (c is not (')' or ']' or '}')) continue;
            if (open.Count == 0) return $"‘{c}’ at position {i + 1} has no opening bracket";
            var (bracket, index) = open.Pop();
            if (c != Partner(bracket)) return $"‘{bracket}’ at position {index + 1} is closed by ‘{c}’ at position {i + 1}";
        }
        if (open.Count == 0) return null;
        var (unclosed, at) = open.Peek();
        return open.Count == 1 ? $"‘{unclosed}’ at position {at + 1} is never closed" : $"{open.Count} brackets are never closed, the last ‘{unclosed}’ at position {at + 1}";
    }

    /// <summary>Texts beyond this size are not scanned per caret move.</summary>
    public const int MatchScanLimit = 4_000_000;
    /// <summary>The bracket pair touching the caret (the one just before it wins, then the one under it), ignoring brackets inside double-quoted strings; null when there is none or the pair is mismatched.</summary>
    public static (int Open, int Close)? MatchBracket(string text, int caret)
    {
        if (text.Length == 0 || text.Length > MatchScanLimit) return null;
        foreach (var at in new[] { caret - 1, caret })
            if (at >= 0 && at < text.Length && (text[at] is '(' or ')' or '[' or ']' or '{' or '}') && Match(text, at) is { } pair) return pair;
        return null;
    }
    private static (int Open, int Close)? Match(string text, int at)
    {
        // One pass from the start: it tells whether the bracket sits inside a string, and the stack holds the partner of a closing bracket.
        var stack = new Stack<int>(); bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
            if (c == '"') { inString = true; continue; }
            if (c is '(' or '[' or '{') { if (i == at) return Forward(text, at); stack.Push(i); continue; }
            if (c is ')' or ']' or '}')
            {
                if (stack.Count == 0) { if (i == at) return null; continue; }
                int open = stack.Pop();
                if (i == at) return Partner(text[open]) == c ? (open, i) : null;
            }
        }
        return null;
    }
    private static (int Open, int Close)? Forward(string text, int open)
    {
        int depth = 0; bool inString = false;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
            if (c == '"') { inString = true; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if ((c is ')' or ']' or '}') && --depth == 0) return c == Partner(text[open]) ? (open, i) : null;
        }
        return null;
    }
    private static char Partner(char open) => open switch { '(' => ')', '[' => ']', _ => '}' };
}
