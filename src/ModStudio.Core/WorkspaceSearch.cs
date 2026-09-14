using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>What Find in files looks for. <paramref name="Scope"/> is a project-relative folder ("" for everything); <paramref name="FileMask"/> holds semicolon-separated globs on file names.</summary>
public sealed record SearchQuery(string Text, bool MatchCase = false, bool WholeWord = false, bool Regex = false, string FileMask = "", string Scope = "");

/// <summary>
/// One match. Table files match per cell (<see cref="Row"/> ≥ 0 with a <see cref="Column"/>); other files match per line
/// (<see cref="Line"/> is 1-based). <see cref="Text"/> is the cell value or a snippet of the line, and Start/Length locate the first
/// match in it; <see cref="Offset"/> is that match's character position in the full line, for placing a caret.
/// <see cref="Label"/> names the row (its first column) so a cell hit reads as "Cap · code = cap".
/// </summary>
public sealed record SearchHit(string File, int Row, string Column, int Line, string Text, int Start, int Length, string Label = "", int Offset = 0)
{
    public bool IsCell => Row >= 0;
}

/// <summary><see cref="Tables"/> holds the parsed table of every file with a cell hit, so a preview does not parse it again.</summary>
public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int Files, int MatchedFiles, bool Truncated, IReadOnlyList<string> Issues, IReadOnlyDictionary<string, TableData> Tables);

public static class WorkspaceSearch
{
    public const int MaxHits = 2000;
    /// <summary>Text files above this size are skipped; game data folders carry multi-megabyte layouts no one searches by hand.</summary>
    public const long MaxTextBytes = 4 * 1024 * 1024;
    /// <summary>Scopes offered by the UI: label and project-relative folder prefix.</summary>
    public static readonly (string Label, string Folder)[] Scopes = [("Source (tables, strings)", "source"), ("Tables", "source/tables"), ("Strings", "source/strings"), ("Data files", "data"), ("Compatibility profiles", "compatibility"), ("Whole project", "")];
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dds", ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".gif", ".webp", ".ico", ".ogg", ".wav", ".mp3", ".wem", ".bnk", ".flac",
        ".mpq", ".bin", ".dat", ".pak", ".zip", ".7z", ".exe", ".dll", ".pdb", ".model", ".anim", ".tex", ".fbx", ".gltf", ".glb",
        ".ttf", ".otf", ".woff", ".woff2", ".mp4", ".webm", ".mov", ".avi", ".db", ".sqlite", ".pdf", ".psd", ".blend"
    };
    private static readonly Regex NoMatch = new("(?!)");

    /// <summary>The matcher a query describes; throws <see cref="ArgumentException"/> for an invalid regular expression.</summary>
    public static Regex Matcher(SearchQuery query)
    {
        if (query.Text.Length == 0) return NoMatch;
        var pattern = query.Regex ? query.Text : System.Text.RegularExpressions.Regex.Escape(query.Text);
        if (query.WholeWord) pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
        var options = RegexOptions.CultureInvariant | (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { return new Regex(pattern, options, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException e) { throw new ArgumentException("Invalid regular expression: " + e.Message.Split('\n')[0].Trim(), e); }
    }

    /// <summary>File-name filter from "*.json; *.txt"; null when the mask is blank (every file passes).</summary>
    public static Regex? MaskMatcher(string mask)
    {
        var globs = mask.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (globs.Length == 0) return null;
        var parts = globs.Select(g => "^" + System.Text.RegularExpressions.Regex.Escape(g).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
        return new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Searches the project's build inputs. <paramref name="tables"/> and <paramref name="texts"/> (keyed by full path) stand in
    /// for the disk copy of files whose unsaved state lives in an editor; a file in <paramref name="texts"/> is searched as text
    /// even when it is a table, since raw source under edit has no reliable cells.
    /// </summary>
    public static SearchResult Run(ModProject project, SearchQuery query, IReadOnlyDictionary<string, TableData>? tables = null, IReadOnlyDictionary<string, string>? texts = null, CancellationToken token = default, Action<int>? progress = null)
    {
        var matcher = Matcher(query); var mask = MaskMatcher(query.FileMask);
        var hits = new List<SearchHit>(); var issues = new List<string>(); var parsed = new Dictionary<string, TableData>(StringComparer.Ordinal);
        int files = 0, matchedFiles = 0; bool truncated = false;
        if (query.Text.Length == 0) return new(hits, 0, 0, false, issues, parsed);
        var scope = query.Scope.Trim('/');
        foreach (var entry in project.SourceEntries())
        {
            token.ThrowIfCancellationRequested();
            var file = entry.FullName; var relative = Relative(project.Root, file);
            if (scope.Length > 0 && !relative.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (mask != null && !mask.IsMatch(Path.GetFileName(file))) continue;
            if (BinaryExtensions.Contains(Path.GetExtension(file))) continue;
            files++; if (files % 200 == 0) progress?.Invoke(files);
            int before = hits.Count;
            try
            {
                if (texts?.TryGetValue(file, out var text) == true) SearchText(file, text, matcher, hits);
                else if (LoadTable(file, relative, entry.Length, tables, parsed) is { } table) SearchTable(file, table, matcher, hits);
                else if (entry.Length <= MaxTextBytes && ReadText(file) is { } content) SearchText(file, content, matcher, hits);
            }
            catch (RegexMatchTimeoutException) { issues.Add($"{relative}: the pattern took too long to match; simplify it."); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { issues.Add($"{relative}: {e.Message}"); }
            if (hits.Count > before) { matchedFiles++; if (parsed.ContainsKey(file) && !hits.Skip(before).Any(h => h.IsCell)) parsed.Remove(file); }
            else parsed.Remove(file);
            if (hits.Count >= MaxHits) { truncated = true; hits.RemoveRange(MaxHits, hits.Count - MaxHits); break; }
        }
        progress?.Invoke(files);
        return new(hits, files, matchedFiles, truncated, issues, parsed);
    }

    /// <summary>Table files under source/, and tab-separated .txt files elsewhere, are searched by cell. A file that fails to parse is searched as text instead.</summary>
    private static TableData? LoadTable(string file, string relative, long length, IReadOnlyDictionary<string, TableData>? buffers, Dictionary<string, TableData> parsed)
    {
        if (buffers?.TryGetValue(file, out var buffered) == true) { parsed[file] = buffered; return buffered; }
        bool sourceTable = relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && (relative.StartsWith("source/tables/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("source/strings/", StringComparison.OrdinalIgnoreCase));
        bool tsv = relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && length <= MaxTextBytes;
        if (!sourceTable && !tsv) return null;
        try
        {
            TableData? table = null;
            if (sourceTable) { var node = Read(file); if (TableData.IsTableFile(node)) table = TableData.FromFile(node, file); }
            else { var bytes = File.ReadAllBytes(file); if (bytes.Contains((byte)'\t') && TextFileEncoding.LooksLikeText(bytes[..Math.Min(bytes.Length, 8192)])) table = TableData.FromTsv(bytes, Path.GetFileNameWithoutExtension(file), "global/excel/" + Path.GetFileName(file)); }
            if (table != null) parsed[file] = table;
            return table;
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException) { return null; }
    }

    private static string? ReadText(string file)
    {
        var bytes = File.ReadAllBytes(file);
        if (!TextFileEncoding.LooksLikeText(bytes[..Math.Min(bytes.Length, 8192)])) return null;
        var (encoding, preamble) = TextFileEncoding.Detect(bytes);
        return encoding.GetString(bytes, preamble, bytes.Length - preamble);
    }

    public static void SearchTable(string file, TableData table, Regex matcher, List<SearchHit> hits)
    {
        int labelColumn = table.IsCatalog && table.Columns.Length > 1 ? 1 : 0;
        for (int row = 0; row < table.Records.Count; row++)
            foreach (var column in table.Columns)
            {
                var value = table.Cell(row, column); if (value.Length == 0) continue;
                var match = matcher.Match(value); if (!match.Success) continue;
                hits.Add(new(file, row, column, -1, value, match.Index, match.Length, table.Cell(row, table.Columns[labelColumn])));
                if (hits.Count >= MaxHits) return;
            }
    }

    public static void SearchText(string file, string text, Regex matcher, List<SearchHit> hits)
    {
        int line = 1, start = 0;
        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
            int stop = end > start && text[end - 1] == '\r' ? end - 1 : end;
            var match = matcher.Match(text, start, stop - start);
            if (match.Success && match.Index < stop)
            {
                // Long lines (minified JSON) are trimmed around the first match so a result stays readable.
                int from = Math.Max(start, match.Index - 120), to = Math.Min(stop, match.Index + match.Length + 200);
                hits.Add(new(file, -1, "", line, (from > start ? "…" : "") + text[from..to] + (to < stop ? "…" : ""), match.Index - from + (from > start ? 1 : 0), Math.Min(match.Length, to - match.Index), Offset: match.Index - start));
                if (hits.Count >= MaxHits) return;
            }
            if (end >= text.Length) break;
            start = end + 1; line++;
        }
    }
}
