using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ModStudio.Core;

public enum UiJsonKind { Object, Array, String, Number, True, False, Null }

/// <summary>A member of a parsed object: its key and where the member starts (the key's opening quote).</summary>
public sealed record UiJsonMember(string Key, int Start, UiJsonNode Value);

/// <summary>
/// A value of a game JSON file with the span it occupies in the text, so an edit can replace exactly those characters and
/// leave comments, trailing commas and hand formatting alone. Spans are [Start, End) character offsets.
/// </summary>
public sealed class UiJsonNode
{
    public UiJsonKind Kind { get; init; }
    public int Start { get; init; }
    public int End { get; internal set; }
    /// <summary>The decoded string, or the number exactly as written.</summary>
    public string? Text { get; init; }
    public List<UiJsonMember> Members { get; } = [];
    public List<UiJsonNode> Items { get; } = [];

    public bool IsObject => Kind == UiJsonKind.Object;
    public bool IsArray => Kind == UiJsonKind.Array;
    /// <summary>The value of a key (the last one when a key repeats, as a JSON reader would); null when absent or not an object.</summary>
    public UiJsonNode? this[string key] => Member(key)?.Value;
    public UiJsonMember? Member(string key)
    {
        for (int i = Members.Count - 1; i >= 0; i--) if (Members[i].Key == key) return Members[i];
        return null;
    }
    public string? String => Kind == UiJsonKind.String ? Text : null;
    public double? Number => Kind == UiJsonKind.Number && double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The value as an ordinary JSON node (a fresh copy every call).</summary>
    public JsonNode? ToJsonNode() => Kind switch
    {
        UiJsonKind.Object => new JsonObject(Members.GroupBy(m => m.Key).Select(g => KeyValuePair.Create(g.Key, g.Last().Value.ToJsonNode()))),
        UiJsonKind.Array => new JsonArray(Items.Select(i => i.ToJsonNode()).ToArray()),
        UiJsonKind.String => JsonValue.Create(Text),
        UiJsonKind.Number => Number is { } n ? (Math.Abs(n % 1) < double.Epsilon && Math.Abs(n) < 1e15 ? JsonValue.Create((long)n) : JsonValue.Create(n)) : JsonValue.Create(0),
        UiJsonKind.True => JsonValue.Create(true),
        UiJsonKind.False => JsonValue.Create(false),
        _ => null
    };
}

public sealed class UiJsonException(string message, int offset, int line, int column) : FormatException(message)
{
    public int Offset { get; } = offset;
    public int Line { get; } = line;
    public int Column { get; } = column;
}

/// <summary>
/// Reads the relaxed JSON the game's UI files are written in (// and /* */ comments, trailing commas) into
/// <see cref="UiJsonNode"/>s that remember their source spans.
/// </summary>
public static class UiJsonParser
{
    public static UiJsonNode Parse(string text)
    {
        int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        Skip(text, ref i);
        var root = Value(text, ref i, 0);
        Skip(text, ref i);
        if (i < text.Length) Fail(text, i, "Unexpected text after the end of the document");
        return root;
    }

    private static UiJsonNode Value(string t, ref int i, int depth)
    {
        if (depth > 256) Fail(t, i, "Nesting is too deep");
        if (i >= t.Length) Fail(t, i, "Unexpected end of file");
        char c = t[i];
        switch (c)
        {
            case '{':
            {
                var node = new UiJsonNode { Kind = UiJsonKind.Object, Start = i }; i++;
                while (true)
                {
                    Skip(t, ref i);
                    if (i >= t.Length) Fail(t, i, "Unclosed object");
                    if (t[i] == '}') { i++; break; }
                    if (t[i] != '"') Fail(t, i, "Expected a property name in double quotes");
                    int keyStart = i; var key = StringLiteral(t, ref i);
                    Skip(t, ref i);
                    if (i >= t.Length || t[i] != ':') Fail(t, i, $"Expected ':' after \"{key}\"");
                    i++; Skip(t, ref i);
                    node.Members.Add(new(key, keyStart, Value(t, ref i, depth + 1)));
                    Skip(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == '}') { i++; break; }
                    Fail(t, i, "Expected ',' or '}'");
                }
                node.End = i; return node;
            }
            case '[':
            {
                var node = new UiJsonNode { Kind = UiJsonKind.Array, Start = i }; i++;
                while (true)
                {
                    Skip(t, ref i);
                    if (i >= t.Length) Fail(t, i, "Unclosed array");
                    if (t[i] == ']') { i++; break; }
                    node.Items.Add(Value(t, ref i, depth + 1));
                    Skip(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == ']') { i++; break; }
                    Fail(t, i, "Expected ',' or ']'");
                }
                node.End = i; return node;
            }
            case '"':
            {
                int start = i; var s = StringLiteral(t, ref i);
                return new UiJsonNode { Kind = UiJsonKind.String, Start = start, End = i, Text = s };
            }
            default:
                if (c == '-' || c == '+' || c == '.' || char.IsAsciiDigit(c))
                {
                    int start = i; i++;
                    while (i < t.Length && (char.IsAsciiDigit(t[i]) || t[i] is '.' or 'e' or 'E' or '-' or '+')) i++;
                    var number = t[start..i];
                    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) Fail(t, start, $"Invalid number {number}");
                    return new UiJsonNode { Kind = UiJsonKind.Number, Start = start, End = i, Text = number };
                }
                foreach (var (word, kind) in new[] { ("true", UiJsonKind.True), ("false", UiJsonKind.False), ("null", UiJsonKind.Null) })
                    if (string.CompareOrdinal(t, i, word, 0, word.Length) == 0) { int start = i; i += word.Length; return new UiJsonNode { Kind = kind, Start = start, End = i }; }
                Fail(t, i, $"Unexpected character '{c}'");
                return null!;
        }
    }

    private static string StringLiteral(string t, ref int i)
    {
        int start = i; i++;
        StringBuilder? sb = null; int run = i;
        while (true)
        {
            if (i >= t.Length || t[i] is '\n' or '\r') Fail(t, start, "Unterminated string");
            char c = t[i];
            if (c == '"') { var result = sb == null ? t[run..i] : sb.Append(t, run, i - run).ToString(); i++; return result; }
            if (c != '\\') { i++; continue; }
            sb ??= new StringBuilder(); sb.Append(t, run, i - run);
            if (i + 1 >= t.Length) Fail(t, i, "Unterminated escape");
            char e = t[i + 1]; i += 2;
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    int code = 0;
                    if (i + 4 > t.Length || !int.TryParse(t.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) Fail(t, i, "Invalid \\u escape");
                    sb.Append((char)code); i += 4; break;
                default: sb.Append(e); break;
            }
            run = i;
        }
    }

    /// <summary>Skips whitespace and comments.</summary>
    internal static void Skip(string t, ref int i)
    {
        while (i < t.Length)
        {
            char c = t[i];
            if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                int end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) Fail(t, i, "Unclosed comment");
                i = end + 2; continue;
            }
            break;
        }
    }

    public static (int Line, int Column) Position(string text, int offset)
    {
        int line = 1, column = 1;
        for (int k = 0; k < offset && k < text.Length; k++) if (text[k] == '\n') { line++; column = 1; } else column++;
        return (line, column);
    }

    private static void Fail(string text, int offset, string message)
    {
        var (line, column) = Position(text, offset);
        throw new UiJsonException($"{message} (line {line}, column {column})", offset, line, column);
    }
}

/// <summary>One replacement in a text: [Start, Start + Length) becomes Text.</summary>
public readonly record struct UiTextEdit(int Start, int Length, string Text);

/// <summary>
/// Minimal text edits against a parsed game JSON file. Values are replaced in place; new members and array items are written in
/// the style around them (one line or one per line, the indentation and line endings in use, trailing commas kept), so an
/// edit made on the canvas shows up in a diff as the few characters that changed.
/// </summary>
public static class UiJsonEditor
{
    public static string Apply(string text, IEnumerable<UiTextEdit> edits)
    {
        var sb = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.Length))
            sb.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        return sb.ToString();
    }

    public static string NewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>Replaces a value with a literal.</summary>
    public static UiTextEdit Replace(UiJsonNode node, string literal) => new(node.Start, node.End - node.Start, literal);

    /// <summary>Sets a member of an object: replaces its value, or adds it after the last member in the object's own style.</summary>
    public static IEnumerable<UiTextEdit> SetMember(string text, UiJsonNode obj, string key, string literal)
    {
        if (obj.Member(key) is { } existing) return [Replace(existing.Value, literal)];
        return Insert(text, obj, obj.Members.Select(m => (m.Start, m.Value.End)).ToList(), Quote(key) + ": " + literal, '}');
    }

    /// <summary>Appends an item to an array (a child widget), in the style of the items already there.</summary>
    public static IEnumerable<UiTextEdit> AppendItem(string text, UiJsonNode array, string literal) =>
        Insert(text, array, array.Items.Select(n => (n.Start, n.End)).ToList(), literal, ']');

    /// <summary>Inserts an item right after another item of the same array, on its own line when the array is written one item per line.</summary>
    public static IEnumerable<UiTextEdit> InsertItemAfter(string text, UiJsonNode array, int index, string literal)
    {
        if (index >= array.Items.Count - 1) return AppendItem(text, array, literal);
        var item = array.Items[index]; var next = array.Items[index + 1];
        // Between two items there is exactly one comma; the new item goes after it, indented like the next item.
        int comma = CommaAfter(text, item.End);
        if (LineStart(text, next.Start) is var nextLine && nextLine > comma)
        {
            var indent = text[nextLine..next.Start];
            return [new(nextLine, 0, indent + literal + "," + NewLine(text))];
        }
        return [new(next.Start, 0, literal + ", ")];
    }

    /// <summary>Removes a member, with its comma and, when it sits alone on its lines, the whole lines.</summary>
    public static UiTextEdit RemoveMember(string text, UiJsonNode obj, string key)
    {
        var member = obj.Member(key) ?? throw new InvalidOperationException($"No member {key}.");
        return RemoveSpan(text, member.Start, member.Value.End);
    }

    public static UiTextEdit RemoveItem(string text, UiJsonNode array, int index) => RemoveSpan(text, array.Items[index].Start, array.Items[index].End);

    /// <summary>Swaps two items of an array (their text only; the separators between them stay).</summary>
    public static IEnumerable<UiTextEdit> SwapItems(string text, UiJsonNode array, int a, int b)
    {
        var x = array.Items[Math.Min(a, b)]; var y = array.Items[Math.Max(a, b)];
        return [new(x.Start, x.End - x.Start, text[y.Start..y.End]), new(y.Start, y.End - y.Start, text[x.Start..x.End])];
    }

    /// <summary>The text of a node, re-indented so its first line starts at the given indentation (for copying an item elsewhere).</summary>
    public static string Reindent(string text, UiJsonNode node, string indent)
    {
        var body = text[node.Start..node.End];
        var original = text[LineStart(text, node.Start)..node.Start];
        if (!string.IsNullOrWhiteSpace(original)) original = new string(original.TakeWhile(char.IsWhiteSpace).ToArray());
        var nl = NewLine(text);
        var lines = body.Replace("\r\n", "\n").Split('\n');
        for (int k = 1; k < lines.Length; k++)
            lines[k] = lines[k].StartsWith(original, StringComparison.Ordinal) ? indent + lines[k][original.Length..] : lines[k];
        return string.Join(nl, lines);
    }

    /// <summary>The whitespace a node's line starts with.</summary>
    public static string IndentOf(string text, int offset)
    {
        int start = LineStart(text, offset); int k = start;
        while (k < text.Length && text[k] is ' ' or '\t') k++;
        return text[start..k];
    }

    private static UiTextEdit RemoveSpan(string text, int start, int end)
    {
        int after = end; int k = end; UiJsonParser.Skip(text, ref k);
        bool comma = k < text.Length && text[k] == ',';
        if (comma) after = k + 1;
        int lineStart = LineStart(text, start);
        bool aloneBefore = string.IsNullOrWhiteSpace(text[lineStart..start]);
        int lineEnd = after; while (lineEnd < text.Length && text[lineEnd] is ' ' or '\t') lineEnd++;
        bool aloneAfter = lineEnd >= text.Length || text[lineEnd] is '\r' or '\n';
        if (aloneBefore && aloneAfter)
        {
            if (lineEnd < text.Length && text[lineEnd] == '\r') lineEnd++;
            if (lineEnd < text.Length && text[lineEnd] == '\n') lineEnd++;
            return new(lineStart, lineEnd - lineStart, "");
        }
        if (!comma)
        {
            // The last item: take the comma before it instead, so "a, b" becomes "a" rather than "a, ".
            int p = start - 1; while (p >= 0 && char.IsWhiteSpace(text[p])) p--;
            if (p >= 0 && text[p] == ',') return new(p, end - p, "");
        }
        while (after < text.Length && text[after] == ' ') after++;
        return new(start, after - start, "");
    }

    private static IEnumerable<UiTextEdit> Insert(string text, UiJsonNode container, List<(int Start, int End)> entries, string entry, char close)
    {
        var nl = NewLine(text);
        int closing = container.End - 1;
        if (entries.Count == 0)
        {
            bool multiline = text.IndexOf('\n', container.Start, closing - container.Start) >= 0;
            if (!multiline) return [new(container.Start + 1, closing - container.Start - 1, " " + entry + " ")];
            var indent = IndentOf(text, closing);
            return [new(container.Start + 1, closing - container.Start - 1, nl + indent + "    " + entry + "," + nl + indent)];
        }
        var last = entries[^1];
        int k = last.End; UiJsonParser.Skip(text, ref k);
        bool trailingComma = k < text.Length && text[k] == ',';
        bool perLine = entries.Count > 1 ? LineStart(text, entries[^1].Start) != LineStart(text, entries[^2].Start) : LineStart(text, last.Start) != LineStart(text, container.Start);
        if (!perLine)
        {
            // Same line: after the last entry (and its comma), before any padding in front of the closing bracket.
            int at = trailingComma ? k + 1 : last.End;
            return [new(at, 0, (trailingComma ? " " : ", ") + entry + (trailingComma ? "," : ""))];
        }
        var entryIndent = IndentOf(text, last.Start);
        // Insert at the end of the last entry's line, so a comment after it stays on its line.
        int lineEnd = trailingComma ? k + 1 : last.End;
        int scan = lineEnd; while (scan < text.Length && text[scan] is not ('\r' or '\n') && scan < closing) scan++;
        var tail = text[lineEnd..scan];
        int insertAt = string.IsNullOrWhiteSpace(tail) || tail.TrimStart().StartsWith("//", StringComparison.Ordinal) ? scan : lineEnd;
        var line = nl + entryIndent + entry + (trailingComma ? "," : "");
        // The separating comma goes right after the last entry; when the new line starts there too, both are one insertion (two at one offset could apply in either order).
        if (trailingComma) return [new(insertAt, 0, line)];
        return insertAt == last.End ? [new(insertAt, 0, "," + line)] : [new(last.End, 0, ","), new(insertAt, 0, line)];
    }

    private static int CommaAfter(string text, int offset) { int k = offset; UiJsonParser.Skip(text, ref k); return k; }
    private static int LineStart(string text, int offset) { int k = offset; while (k > 0 && text[k - 1] != '\n') k--; return k; }

    // ── Literals ────────────────────────────────────────────────────────────────────────────────────

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c < ' ' ? $"\\u{(int)c:x4}" : c.ToString() });
        return sb.Append('"').ToString();
    }

    public static string Number(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 1e-9) return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        return Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>A value written the way the game's files write small objects: on one line, { "x": 1, "y": 2 }.</summary>
    public static string Literal(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => o.Count == 0 ? "{ }" : "{ " + string.Join(", ", o.Select(p => Quote(p.Key) + ": " + Literal(p.Value))) + " }",
        JsonArray a => a.Count == 0 ? "[ ]" : "[ " + string.Join(", ", a.Select(Literal)) + " ]",
        JsonValue v when v.TryGetValue<string>(out var s) => Quote(s),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<double>(out var d) => Number(d),
        _ => node.ToJsonString()
    };
}
