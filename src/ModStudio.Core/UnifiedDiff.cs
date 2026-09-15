namespace ModStudio.Core;

public enum DiffRowKind { Equal, Added, Removed, Modified }

/// <summary>One aligned row of a side-by-side view. A null side is a filler slot with no line behind it.</summary>
public sealed record DiffRow(DiffRowKind Kind, int? LeftLine, string? Left, int? RightLine, string? Right);

/// <summary>
/// Turns unified diff text into rows for a two-column view. Fed a full-context diff (<c>-U&lt;huge&gt;</c>) it reproduces both
/// files completely, so git's own change detection decides the alignment and no second diff algorithm is needed.
/// </summary>
public static class UnifiedDiff
{
    public static bool IsBinary(string unified) => unified.Contains("Binary files ", StringComparison.Ordinal) && !unified.Contains("\n@@", StringComparison.Ordinal);

    public static IReadOnlyList<DiffRow> Rows(string unified)
    {
        var rows = new List<DiffRow>();
        var removed = new List<(int Line, string Text)>(); var added = new List<(int Line, string Text)>();
        int left = 0, right = 0; bool inHunk = false;
        void Flush()
        {
            int paired = Math.Min(removed.Count, added.Count);
            for (int i = 0; i < paired; i++) rows.Add(new DiffRow(DiffRowKind.Modified, removed[i].Line, removed[i].Text, added[i].Line, added[i].Text));
            for (int i = paired; i < removed.Count; i++) rows.Add(new DiffRow(DiffRowKind.Removed, removed[i].Line, removed[i].Text, null, null));
            for (int i = paired; i < added.Count; i++) rows.Add(new DiffRow(DiffRowKind.Added, null, null, added[i].Line, added[i].Text));
            removed.Clear(); added.Clear();
        }
        foreach (var raw in unified.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush(); inHunk = true;
                // @@ -12,7 +12,8 @@ optional heading
                var parts = line.Split(' ');
                if (parts.Length >= 3) { left = Start(parts[1]); right = Start(parts[2]); }
                continue;
            }
            if (!inHunk || line.Length == 0) continue;
            switch (line[0])
            {
                case ' ': Flush(); rows.Add(new DiffRow(DiffRowKind.Equal, left, line[1..], right, line[1..])); left++; right++; break;
                case '-': removed.Add((left++, line[1..])); break;
                case '+': added.Add((right++, line[1..])); break;
                case '\\': break; // "\ No newline at end of file"
                default: inHunk = false; Flush(); break; // next file header
            }
        }
        Flush();
        return rows;
    }

    private static int Start(string range)
    {
        var digits = range.TrimStart('-', '+'); var comma = digits.IndexOf(',');
        return int.TryParse(comma < 0 ? digits : digits[..comma], out var n) ? Math.Max(n, 1) : 1;
    }

    /// <summary>Row indexes where each change block begins, for previous / next navigation.</summary>
    public static IReadOnlyList<int> ChangeStarts(IReadOnlyList<DiffRow> rows)
    {
        var starts = new List<int>();
        for (int i = 0; i < rows.Count; i++) if (rows[i].Kind != DiffRowKind.Equal && (i == 0 || rows[i - 1].Kind == DiffRowKind.Equal)) starts.Add(i);
        return starts;
    }
}
