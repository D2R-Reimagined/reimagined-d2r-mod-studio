using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// A cube input or output cell as its comma-separated tokens, in the order they were written: the item (a base code, an
/// item type, a unique or set name, or a special word) then modifiers such as <c>mag</c>, <c>eth</c>, <c>sock=4</c> and
/// <c>qty=3</c>. Edits change or add one token and leave the others where they were, so a toggle never reorders a cell.
/// </summary>
public sealed class CubeTokens
{
    private readonly List<(string Key, string? Value)> tokens = [];
    public string Head { get; set; } = "";

    public static CubeTokens Parse(string text)
    {
        var parts = DropCalculator.Unquote(text).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var result = new CubeTokens { Head = parts.FirstOrDefault() ?? "" };
        foreach (var part in parts.Skip(1)) { var pair = part.Split('=', 2); result.tokens.Add((pair[0], pair.Length > 1 ? pair[1] : null)); }
        return result;
    }

    public bool Has(string key) => tokens.Any(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    public string? Value(string key) => tokens.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    public IEnumerable<string> Keys => tokens.Select(t => t.Key);
    public int Quantity => int.TryParse(Value("qty"), out var n) && n > 0 ? n : 1;

    /// <summary>Adds a flag, or removes it when <paramref name="on"/> is false.</summary>
    public void Flag(string key, bool on)
    {
        if (!on) { tokens.RemoveAll(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); return; }
        if (!Has(key)) tokens.Add((key, null));
    }

    /// <summary>Sets <c>key=value</c> in place, adds it at the end, or removes it when the value is empty.</summary>
    public void Set(string key, string? value)
    {
        int index = tokens.FindIndex(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(value)) { if (index >= 0) tokens.RemoveAt(index); return; }
        if (index >= 0) tokens[index] = (tokens[index].Key, value); else tokens.Add((key, value));
    }

    /// <summary>Picks one of a group of exclusive flags (item qualities, tiers), or none.</summary>
    public void Choose(IEnumerable<string> group, string? chosen)
    {
        var options = group.ToArray();
        int index = tokens.FindIndex(t => t.Value == null && options.Contains(t.Key, StringComparer.OrdinalIgnoreCase));
        tokens.RemoveAll(t => t.Value == null && options.Contains(t.Key, StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(chosen)) return;
        tokens.Insert(index >= 0 ? Math.Min(index, tokens.Count) : tokens.Count, (chosen, null));
    }

    public string? Chosen(IEnumerable<string> group) => tokens.FirstOrDefault(t => t.Value == null && group.Contains(t.Key, StringComparer.OrdinalIgnoreCase)).Key;

    /// <summary>The quantity token: qty=N, dropped at 1 (the default).</summary>
    public void SetQuantity(int quantity) => Set("qty", quantity > 1 ? quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);

    public override string ToString() => Head.Length == 0 && tokens.Count == 0 ? "" : string.Join(",", tokens.Select(t => t.Value == null ? t.Key : t.Key + "=" + t.Value).Prepend(Head));
}

/// <summary>
/// Something a cube cell can name, with where its picture comes from: a base item (its code), an item type (a base item
/// of that type stands in), a unique or set item (its index and base), or a special output (portals, useitem).
/// </summary>
public sealed record CubeChoice(string Head, string Label, string Kind, string PictureTable, string PictureIndex, string PictureCode, string BaseTable, string Tier)
{
    public string SearchText { get; } = (Head + " " + Label + " " + Kind).ToLowerInvariant();
    public override string ToString() => Head;
}

/// <summary>One cubemain row as the builder's list shows it.</summary>
public sealed record CubeEntry(int Row, string SourceId, string Description, string Summary, bool Enabled, bool Inactive)
{
    public string SearchText { get; } = (Description + " " + Summary).ToLowerInvariant();
}

/// <summary>A cubemain row's identity and cells the list needs, read on the UI thread.</summary>
public sealed record CubeRow(int Row, string SourceId, string Description, string Enabled, string Inputs, string Outputs);

public sealed record CubeBuilderCatalog(CubeEntry[] Entries, CubeChoice[] Choices, BuilderProperty[] Properties, string[] Issues)
{
    private readonly Dictionary<string, CubeChoice> byHead = Choices.GroupBy(c => c.Head, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    public CubeChoice? Find(string head) => byHead.GetValueOrDefault(head);
}

/// <summary>What the cube builder reads besides the recipe being edited. Worker-owned; reads authored data only.</summary>
public sealed class CubeBuilderResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();

    /// <summary>Outputs that are not items.</summary>
    public static readonly (string Head, string Label)[] SpecialOutputs = [
        ("useitem", "The input 1 item itself"), ("usetype", "A new item of input 1's type"), ("Cow Portal", "Portal to the Moo Moo Farm"),
        ("Pandemonium Portal", "One of the Pandemonium portals"), ("Pandemonium Finale Portal", "Portal to Uber Tristram"), ("Red Portal", "Red portal to a level (lvl=)")];

    public CubeBuilderCatalog Catalog(ModProject project, string profile, string locale, IReadOnlyList<CubeRow> rows, CancellationToken token)
    {
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var catalog = new ItemCatalog(data);
        var choices = new List<CubeChoice> { new("any", "Any item", "special", "", "", "", "", "normal") };
        foreach (var (table, row) in catalog.Items)
        {
            var code = row.S("code");
            choices.Add(new(code, catalog.ItemName(code), "item", table, "", code, table, ItemSprites.Tier(row, code)));
        }
        token.ThrowIfCancellationRequested();
        // A type is pictured by the first base item of that type (a helm for "helm", a ruby for "gemr").
        foreach (var type in data.Rows("itemtypes", false).Select(t => t.S("Code")).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var example = catalog.Items.FirstOrDefault(i => catalog.ItemIs(i.Row, type));
            choices.Add(new(type, "Any " + catalog.TypeName(type), "type", example.Table ?? "", "", example.Row?.S("code") ?? "", example.Table ?? "", example.Row == null ? "normal" : ItemSprites.Tier(example.Row, example.Row.S("code"))));
        }
        foreach (var (table, column) in new[] { ("uniqueitems", "code"), ("setitems", "item") })
            foreach (var row in data.Rows(table, false))
            {
                var index = row.S("index"); var code = row.S(column);
                if (index.Length == 0 || code.Length == 0) continue;
                var base_ = catalog.Item(code);
                choices.Add(new(index, $"{data.Localize(index, false)} ({catalog.ItemName(code)})", table == "uniqueitems" ? "unique" : "set", table, index, code, base_?.Table ?? "", base_ is { } b ? ItemSprites.Tier(b.Row, code) : "normal"));
            }
        foreach (var (head, label) in SpecialOutputs) choices.Add(new(head, label, "special", "", "", "", "", "normal"));
        var names = choices.GroupBy(c => c.Head, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.OrdinalIgnoreCase);
        string Name(string cell)
        {
            var tokens = CubeTokens.Parse(cell);
            return (tokens.Quantity > 1 ? tokens.Quantity + " × " : "") + names.GetValueOrDefault(tokens.Head, tokens.Head);
        }
        var entries = rows.Select(r =>
        {
            var inputs = r.Inputs.Split('\n', StringSplitOptions.RemoveEmptyEntries); var outputs = r.Outputs.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var summary = string.Join(" + ", inputs.Select(Name)) + (outputs.Length > 0 ? " → " + string.Join(", ", outputs.Select(Name)) : "");
            return new CubeEntry(r.Row, r.SourceId, r.Description, summary, r.Enabled == "1", inputs.Length == 0 && outputs.Length == 0);
        }).ToArray();
        var properties = data.Rows("properties", false).Where(p => p.S("code").Length > 0)
            .Select(p => new BuilderProperty(p.S("code"), p.S("*Tooltip"), p.S("*Parameter"), p.S("*Min"), p.S("*Max"), p.S("*Notes"))).DistinctBy(p => p.Code, StringComparer.Ordinal).ToArray();
        return new(entries, [.. choices], properties, [.. issues.Distinct()]);
    }

    public static CubeRow[] Rows(TableData table)
    {
        string Cell(int row, string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column).Trim() : "";
        return [.. Enumerable.Range(0, table.Records.Count).Select(i => new CubeRow(i, table.Records[i].S("sourceId"), Cell(i, "description"), Cell(i, "enabled"),
            string.Join('\n', Enumerable.Range(1, 7).Select(n => Cell(i, "input " + n)).Where(v => v.Length > 0)),
            string.Join('\n', new[] { "output", "output b", "output c" }.Select(c => Cell(i, c)).Where(v => v.Length > 0))))];
    }
}
