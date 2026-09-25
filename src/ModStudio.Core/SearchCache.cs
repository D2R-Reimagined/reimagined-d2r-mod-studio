using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ModStudio.Core;

/// <summary>
/// Searchable text of every project file, kept between searches so Find in files neither rereads nor reparses a file whose
/// size and write time are unchanged. A table is flattened into one string of its non-empty cells (see <see cref="CellIndex"/>),
/// so a plain-text query is a single vectorized scan instead of a regex call per cell. A file written in the last two seconds
/// is read again next time, since its timestamp cannot yet prove a later write did not land in the same tick.
/// </summary>
public sealed class SearchCache(bool retain = true)
{
    private static readonly TimeSpan RecentWrite = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, Entry> entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal abstract record Entry(long Length, long Written);
    /// <summary><see cref="Content"/> is null for files that are not searched as text (binary content, or above <see cref="WorkspaceSearch.MaxTextBytes"/>).</summary>
    internal sealed record TextEntry(long Length, long Written, string? Content) : Entry(Length, Written);
    internal sealed record TableEntry(long Length, long Written, CellIndex Cells) : Entry(Length, Written);

    /// <summary>Files currently held.</summary>
    public int Count => entries.Count;

    /// <summary>
    /// The searchable form of a file, read from disk only when the cache has nothing current for it. <paramref name="parsed"/>
    /// receives a table this call had to parse, so the search can hand it to a preview without parsing it again.
    /// </summary>
    internal Entry Get(FileInfo info, string relative, Action<TableData>? parsed = null)
    {
        long written = info.LastWriteTimeUtc.Ticks;
        if (entries.TryGetValue(info.FullName, out var known) && known.Length == info.Length && known.Written == written) return known;
        Entry entry;
        if (WorkspaceSearch.ParseTable(info.FullName, relative, info.Length) is { } table) { entry = new TableEntry(info.Length, written, CellIndex.From(table)); parsed?.Invoke(table); }
        else entry = new TextEntry(info.Length, written, info.Length <= WorkspaceSearch.MaxTextBytes ? WorkspaceSearch.ReadText(info.FullName) : null);
        if (retain && DateTime.UtcNow - info.LastWriteTimeUtc > RecentWrite) entries[info.FullName] = entry; else entries.TryRemove(info.FullName, out _);
        return entry;
    }

    /// <summary>Reads every searchable project file ahead of the first search, and drops files that no longer exist.</summary>
    public void Warm(ModProject project, CancellationToken token = default)
    {
        var seen = new ConcurrentDictionary<string, byte>(entries.Comparer);
        var files = project.SourceEntries().Where(f => !WorkspaceSearch.IsBinary(f.FullName)).ToArray();
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, file =>
        {
            seen[file.FullName] = 0;
            try { Get(file, Storage.Relative(project.Root, file.FullName)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        });
        foreach (var key in entries.Keys) if (!seen.ContainsKey(key)) entries.TryRemove(key, out _);
    }

    public void Clear() => entries.Clear();
}

/// <summary>
/// A table's non-empty cells joined into one string, each followed by a NUL, in row-major column order; parallel arrays map a
/// cell back to its row and column. NUL is not a word character, so whole-word lookarounds behave at a cell edge as they do at
/// the edge of a lone cell value.
/// </summary>
internal sealed class CellIndex
{
    private string blob = "";
    private int[] starts = [], rows = [], columns = [];
    private string[] names = [], labels = [];
    /// <summary>False when some cell itself contains NUL, so a match could straddle cells; such a table is matched cell by cell.</summary>
    private bool separable = true;

    public static CellIndex From(TableData table)
    {
        var text = new StringBuilder(); var starts = new List<int>(); var rows = new List<int>(); var columns = new List<int>();
        var labels = new string[table.Records.Count]; bool separable = true;
        int labelColumn = table.IsCatalog && table.Columns.Length > 1 ? 1 : 0;
        for (int row = 0; row < table.Records.Count; row++)
        {
            labels[row] = table.Columns.Length == 0 ? "" : table.Cell(row, table.Columns[labelColumn]);
            for (int column = 0; column < table.Columns.Length; column++)
            {
                var value = table.Cell(row, table.Columns[column]); if (value.Length == 0) continue;
                if (value.Contains('\0')) separable = false;
                starts.Add(text.Length); rows.Add(row); columns.Add(column); text.Append(value).Append('\0');
            }
        }
        return new() { blob = text.ToString(), starts = [.. starts], rows = [.. rows], columns = [.. columns], names = table.Columns, labels = labels, separable = separable };
    }

    private int End(int cell) => (cell + 1 < starts.Length ? starts[cell + 1] : blob.Length) - 1;

    /// <summary>Adds the first match of each matching cell, in the same order and form as <see cref="WorkspaceSearch.SearchTable"/>.</summary>
    public void Search(string file, Regex matcher, bool literal, List<SearchHit> hits)
    {
        if (literal && separable)
        {
            // A literal cannot contain NUL, so every match lies inside one cell and the leftmost match in the blob is that cell's first.
            int from = 0;
            while (from < blob.Length)
            {
                var match = matcher.Match(blob, from); if (!match.Success) return;
                int cell = Array.BinarySearch(starts, match.Index); if (cell < 0) cell = ~cell - 1;
                int end = End(cell);
                if (match.Index + match.Length <= end && Add(file, cell, match, hits)) return;
                from = end + 1;
            }
            return;
        }
        // Patterns may anchor or span, so each cell is matched as a whole input of its own.
        for (int cell = 0; cell < starts.Length; cell++)
        {
            var match = matcher.Match(blob, starts[cell], End(cell) - starts[cell]);
            if (match.Success && Add(file, cell, match, hits)) return;
        }
    }

    private bool Add(string file, int cell, Match match, List<SearchHit> hits)
    {
        int start = starts[cell];
        hits.Add(new(file, rows[cell], names[columns[cell]], -1, blob[start..End(cell)], match.Index - start, match.Length, labels[rows[cell]]));
        return hits.Count >= WorkspaceSearch.MaxHits;
    }
}
