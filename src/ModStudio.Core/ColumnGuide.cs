using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModStudio.Core;

/// <summary>Field documentation for game tables, imported from the community d2rdoc data guide (see scripts/import-column-guide.mjs).</summary>
public sealed record ColumnGuideEntry(string Name, string Description, string[]? AltNames, string? Type, string? Format, string[][]? Table, int TableTruncated, string[]? Bits, string? RefFile = null, string? RefField = null)
{
    /// <summary>Plain-text summary suitable for a tooltip: description, type, parse format, and a bounded slice of any reference table.</summary>
    public string Summary(int maxTableRows = 12)
    {
        var lines = new List<string> { Description };
        if (!string.IsNullOrEmpty(Type)) lines.Add("Type: " + Type);
        if (!string.IsNullOrEmpty(Format)) lines.Add("Format: " + Format);
        if (Table is { Length: > 1 })
        {
            lines.Add("");
            foreach (var row in Table.Take(maxTableRows + 1)) lines.Add(string.Join("  |  ", row.Select(c => c.Replace('\n', ' '))));
            int total = TableTruncated > 0 ? TableTruncated : Table.Length - 1;
            if (total > maxTableRows) lines.Add($"… {total - maxTableRows} more rows in the online guide");
        }
        if (Bits is { Length: > 0 })
        {
            lines.Add("");
            for (int i = 0; i < Math.Min(Bits.Length, 32); i++) if (!string.IsNullOrWhiteSpace(Bits[i])) lines.Add($"bit {i} ({1L << i}): {Bits[i]}");
        }
        return string.Join("\n", lines);
    }
}
public sealed record ColumnGuideFile(string Key, string Title, string Overview, ColumnGuideEntry[] Fields)
{
    /// <summary>Column headers in documented order, with numbered families (prop#, min#) expanded through their alternative names.</summary>
    public string[] Headers()
    {
        var headers = new List<string>();
        foreach (var field in Fields)
        {
            var numbered = field.Name.Contains('#') ? (field.AltNames ?? []).Where(alt => !alt.Contains('#')).ToArray() : [];
            headers.AddRange(numbered.Length > 0 ? numbered : [field.Name.Contains('#') ? field.Name.Replace("#", "1") : field.Name]);
        }
        return headers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    /// <summary>The native file this page documents, as a build target under the data root.</summary>
    public string Target => "global/excel/" + (Title.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? Title : Key + ".txt");
    private Dictionary<string, ColumnGuideEntry>? index;
    public ColumnGuideEntry? Find(string column)
    {
        index ??= BuildIndex();
        var key = ColumnGuide.Normalize(column);
        if (index.TryGetValue(key, out var entry)) return entry;
        // Numbered columns (prop7, min12) are documented once as prop# / min#.
        return index.TryGetValue(Regex.Replace(key, "[0-9]+", "#"), out entry) ? entry : null;
    }
    private Dictionary<string, ColumnGuideEntry> BuildIndex()
    {
        var map = new Dictionary<string, ColumnGuideEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            map.TryAdd(ColumnGuide.Normalize(field.Name), field);
            foreach (var alt in field.AltNames ?? []) map.TryAdd(ColumnGuide.Normalize(alt), field);
        }
        return map;
    }
}
public static class ColumnGuide
{
    public const string Source = "https://eezstreet.github.io/d2rdoc/";
    private sealed record Bundle(string Source, string Repository, string Commit, string Generated, Dictionary<string, ColumnGuideFile> Files);
    private static readonly Lazy<Bundle> bundle = new(Load);
    private static Bundle Load()
    {
        using var stream = typeof(ColumnGuide).Assembly.GetManifestResourceStream("ModStudio.Core.Assets.column-guide.json")
            ?? throw new InvalidDataException("Column guide resource is missing from ModStudio.Core.");
        return JsonSerializer.Deserialize<Bundle>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Column guide resource is empty.");
    }
    public static string Generated => bundle.Value.Generated;
    /// <summary>Every documented game table, sorted by key: the catalog offered when creating a new table.</summary>
    public static IReadOnlyList<ColumnGuideFile> Files => bundle.Value.Files.Values.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    public static string Commit => bundle.Value.Commit;
    /// <summary>Guide page for a table name such as "uniqueitems" or a target like "global/excel/UniqueItems.txt".</summary>
    public static ColumnGuideFile? File(string? table)
    {
        if (string.IsNullOrWhiteSpace(table)) return null;
        var key = Path.GetFileNameWithoutExtension(table.Replace('\\', '/')).Trim().ToLowerInvariant();
        return bundle.Value.Files.TryGetValue(key, out var file) ? file : null;
    }
    public static ColumnGuideEntry? Find(string? table, string column) => File(table)?.Find(column);
    /// <summary>Page URL for a table, with an optional field anchor.</summary>
    public static string Url(string table, string? column = null)
    {
        var file = File(table); if (file == null) return Source;
        var anchor = column == null ? null : file.Find(column) is { } entry ? (entry.AltNames?.FirstOrDefault(a => a.Equals(Normalize(column), StringComparison.OrdinalIgnoreCase)) ?? entry.Name) : null;
        return Source + "files/" + file.Key + ".html" + (anchor == null ? "" : "#" + Uri.EscapeDataString(anchor));
    }
    /// <summary>Studio suffixes duplicated headers with #N; the guide documents the bare header.</summary>
    internal static string Normalize(string column) => Regex.Replace(column.Trim(), "#[0-9]+$", "").ToLowerInvariant();
}
