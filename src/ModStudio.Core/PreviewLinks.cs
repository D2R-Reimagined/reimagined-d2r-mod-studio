using System.Text;

namespace ModStudio.Core;

/// <summary>A table cell a preview read: the table, the row's sourceId and the column.</summary>
public sealed record CellLink(string Table, string SourceId, string Column)
{
    public override string ToString() => $"{Table} · {Column}";
}

/// <summary>A run of preview text that was read from one or more cells.</summary>
public sealed record LinkSpan(int Start, int Length, CellLink[] Targets);

/// <summary>
/// Preview text with the cells its parts came from. Resolvers mark a part with <see cref="Mark"/> while they build a line;
/// the marks are private-use characters, so result records strip them (<see cref="Plain"/>) and keep the parsed runs beside
/// the plain text for the card to make clickable.
/// </summary>
public sealed record PreviewText(string Text, LinkSpan[] Links)
{
    private const char Open = '', Body = '', Close = '', Field = '', Next = '';

    /// <summary>The text marked as read from the targets; unchanged when there are none or it already holds marks.</summary>
    public static string Mark(string text, IEnumerable<CellLink?> targets)
    {
        var cells = targets.OfType<CellLink>().Distinct().ToArray();
        if (cells.Length == 0 || text.Length == 0 || text.IndexOf(Open) >= 0) return text;
        return Open + string.Join(Next, cells.Select(c => c.Table + Field + c.SourceId + Field + c.Column)) + Body + text + Close;
    }

    public static string Plain(string marked)
    {
        if (marked.IndexOf(Open) < 0) return marked;
        var text = new StringBuilder(marked.Length);
        for (int i = 0; i < marked.Length; i++)
        {
            if (marked[i] == Open) { i = marked.IndexOf(Body, i); continue; }
            if (marked[i] != Close) text.Append(marked[i]);
        }
        return text.ToString();
    }

    public static PreviewText Parse(string marked)
    {
        if (marked.IndexOf(Open) < 0) return new(marked, []);
        var text = new StringBuilder(marked.Length); var links = new List<LinkSpan>();
        for (int i = 0; i < marked.Length; i++)
        {
            if (marked[i] != Open) { if (marked[i] != Close) text.Append(marked[i]); continue; }
            int body = marked.IndexOf(Body, i), close = marked.IndexOf(Close, body);
            var targets = marked[(i + 1)..body].Split(Next).Select(t => t.Split(Field)).Select(p => new CellLink(p[0], p[1], p[2])).ToArray();
            links.Add(new(text.Length, close - body - 1, targets));
            text.Append(marked, body + 1, close - body - 1);
            i = close;
        }
        return new(text.ToString(), [.. links]);
    }

    public static string[] Plain(string[] marked) => [.. marked.Select(Plain)];
    public static PreviewText[] Parse(string[] marked) => [.. marked.Select(Parse)];
}
