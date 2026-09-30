using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ModStudio.Core;

/// <summary>
/// Searchable text of every project file, kept between searches so Find in files neither rereads nor reparses a file whose
/// size and write time are unchanged. A table is flattened into one string of its non-empty cells (see <see cref="CellIndex"/>),
/// so a plain-text query is a single vectorized scan instead of a regex call per cell. A file written in the last two seconds
/// is read again next time, since its timestamp cannot yet prove a later write did not land in the same tick.
/// The whole cache can be saved to a snapshot file and loaded at the next launch, so only files changed since are read again.
/// </summary>
public sealed class SearchCache(bool retain = true)
{
    private static readonly TimeSpan RecentWrite = TimeSpan.FromSeconds(2);
    private const int SnapshotVersion = 1;
    private readonly ConcurrentDictionary<string, Entry> entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    /// <summary>Nonzero once an entry was read from disk or dropped since the snapshot was loaded or saved.</summary>
    private int changed;

    internal abstract record Entry(long Length, long Written);
    /// <summary>
    /// <see cref="Utf8"/> is null for files that are not searched as text (binary content, or above <see cref="WorkspaceSearch.MaxTextBytes"/>).
    /// Text is held as UTF-8, half the memory of a string, and decoded into a pooled buffer per search.
    /// </summary>
    internal sealed record TextEntry(long Length, long Written, byte[]? Utf8) : Entry(Length, Written);
    internal sealed record TableEntry(long Length, long Written, CellIndex Cells) : Entry(Length, Written);

    /// <summary>Files currently held.</summary>
    public int Count => entries.Count;

    /// <summary>Where the project's search snapshot lives; <c>.studio</c> is ignored by git and by the project file watcher.</summary>
    public static string SnapshotFile(ModProject project) => Path.Combine(project.Cache, "search-index.bin");

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
        else entry = new TextEntry(info.Length, written, info.Length <= WorkspaceSearch.MaxTextBytes ? WorkspaceSearch.ReadUtf8(info.FullName) : null);
        if (retain && DateTime.UtcNow - info.LastWriteTimeUtc > RecentWrite) { entries[info.FullName] = entry; changed = 1; }
        else if (entries.TryRemove(info.FullName, out _)) changed = 1;
        return entry;
    }

    /// <summary>
    /// Reads every searchable project file ahead of the first search, and drops files that no longer exist. The work runs on
    /// <paramref name="workers"/> dedicated below-normal-priority threads (the caller's included) rather than the thread pool, so
    /// warming a large project neither starves the pool the UI awaits on nor competes with it for CPU. With a
    /// <paramref name="snapshot"/>, the files it holds are loaded first (each still checked against its size and write time),
    /// and the snapshot is rewritten afterwards when anything had to be read.
    /// </summary>
    public void Warm(ModProject project, CancellationToken token = default, int workers = 2, string? snapshot = null)
    {
        if (snapshot != null) Load(snapshot, project.Root);
        var seen = new ConcurrentDictionary<string, byte>(entries.Comparer);
        var files = project.SourceEntries().Where(f => !WorkspaceSearch.IsBinary(f.FullName)).ToArray();
        int next = -1;
        void Work()
        {
            for (int index; !token.IsCancellationRequested && (index = Interlocked.Increment(ref next)) < files.Length;)
            {
                var file = files[index]; seen[file.FullName] = 0;
                try { Get(file, Storage.Relative(project.Root, file.FullName)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }
        var helpers = Enumerable.Range(1, Math.Clamp(workers, 1, Environment.ProcessorCount) - 1)
            .Select(_ => new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Search index" }).ToArray();
        foreach (var helper in helpers) helper.Start();
        Work();
        foreach (var helper in helpers) helper.Join();
        token.ThrowIfCancellationRequested();
        foreach (var key in entries.Keys) if (!seen.ContainsKey(key) && entries.TryRemove(key, out _)) changed = 1;
        if (snapshot != null && changed != 0) Save(snapshot, project.Root);
    }

    public void Clear() => entries.Clear();

    /// <summary>
    /// Adds the entries of a snapshot written by <see cref="Save"/>, keeping any the cache already holds. A missing, outdated or
    /// damaged snapshot is ignored, and the files are simply read again. Strings and arrays are read straight into their final
    /// storage, so loading allocates little beyond what the cache keeps.
    /// </summary>
    internal void Load(string snapshot, string root)
    {
        try
        {
            using var file = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            using var reader = new BinaryReader(new BufferedStream(new DeflateStream(file, CompressionMode.Decompress), 1 << 16));
            if (reader.ReadInt32() != SnapshotVersion) return;
            for (int count = reader.ReadInt32(); count > 0; count--)
            {
                var full = Path.GetFullPath(Path.Combine(root, reader.ReadString())); long length = reader.ReadInt64(), written = reader.ReadInt64();
                Entry entry = reader.ReadByte() switch
                {
                    0 => new TextEntry(length, written, null),
                    1 => new TextEntry(length, written, ReadBytes(reader)),
                    2 => new TableEntry(length, written, CellIndex.Read(reader)),
                    _ => throw new InvalidDataException("Unknown search snapshot entry.")
                };
                entries.TryAdd(full, entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException or OverflowException) { }
    }

    /// <summary>Writes every held entry, with paths relative to <paramref name="root"/> so a moved project keeps its snapshot. Failure only costs the next launch a full read.</summary>
    internal void Save(string snapshot, string root)
    {
        var temporary = snapshot + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1))
            using (var writer = new BinaryWriter(new BufferedStream(new DeflateStream(file, CompressionLevel.Fastest), 1 << 16)))
            {
                var held = entries.ToArray();
                writer.Write(SnapshotVersion); writer.Write(held.Length);
                foreach (var (full, entry) in held)
                {
                    writer.Write(Storage.Relative(root, full)); writer.Write(entry.Length); writer.Write(entry.Written);
                    switch (entry)
                    {
                        case TextEntry { Utf8: null }: writer.Write((byte)0); break;
                        case TextEntry { Utf8: { } utf8 }: writer.Write((byte)1); writer.Write(utf8.Length); writer.Write(utf8); break;
                        case TableEntry table: writer.Write((byte)2); table.Cells.Write(writer); break;
                    }
                }
            }
            File.Move(temporary, snapshot, overwrite: true); changed = 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { try { File.Delete(temporary); } catch (Exception) { } }
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        var bytes = new byte[reader.ReadInt32()]; reader.BaseStream.ReadExactly(bytes); return bytes;
    }
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

    /// <summary>Strings and arrays go out as their raw in-memory bytes, so <see cref="Read"/> fills each in place.</summary>
    public void Write(BinaryWriter writer)
    {
        writer.Write(separable); writer.Write(blob.Length); writer.Write(MemoryMarshal.AsBytes(blob.AsSpan()));
        writer.Write(starts.Length); foreach (var array in new[] { starts, rows, columns }) writer.Write(MemoryMarshal.AsBytes(array.AsSpan()));
        writer.Write(names.Length); foreach (var name in names) writer.Write(name);
        writer.Write(labels.Length); foreach (var label in labels) writer.Write(label);
    }

    public static CellIndex Read(BinaryReader reader)
    {
        var stream = reader.BaseStream; bool separable = reader.ReadBoolean();
        var blob = string.Create(reader.ReadInt32(), stream, static (chars, s) => s.ReadExactly(MemoryMarshal.AsBytes(chars)));
        int cells = reader.ReadInt32(); int[] starts = new int[cells], rows = new int[cells], columns = new int[cells];
        foreach (var array in new[] { starts, rows, columns }) stream.ReadExactly(MemoryMarshal.AsBytes(array.AsSpan()));
        var names = new string[reader.ReadInt32()]; for (int i = 0; i < names.Length; i++) names[i] = reader.ReadString();
        var labels = new string[reader.ReadInt32()]; for (int i = 0; i < labels.Length; i++) labels[i] = reader.ReadString();
        if (cells > 0 && (starts[^1] >= blob.Length || rows.Any(r => (uint)r >= (uint)labels.Length) || columns.Any(c => (uint)c >= (uint)names.Length))) throw new InvalidDataException("Damaged search snapshot table.");
        return new() { blob = blob, starts = starts, rows = rows, columns = columns, names = names, labels = labels, separable = separable };
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
