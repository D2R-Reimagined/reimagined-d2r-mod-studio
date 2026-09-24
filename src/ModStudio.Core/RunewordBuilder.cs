using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>A base item a runeword can be made in: enough sockets from some item level, and of an allowed, not excluded, type.</summary>
public sealed record RunewordBase(string Code, string Name, string Table, string Tier, int Qlvl, int FromItemLevel, int RequiredLevel);

/// <summary>A rune: its code, name and the level it requires.</summary>
public sealed record RunewordRune(string Code, string Name, int RequiredLevel)
{
    public string SearchText { get; } = (Code + " " + Name).ToLowerInvariant();
    public override string ToString() => Code;
}

/// <summary>An item type a runeword can be allowed on or excluded from.</summary>
public sealed record RunewordType(string Code, string Name)
{
    public string SearchText { get; } = (Code + " " + Name).ToLowerInvariant();
    public override string ToString() => Code;
}

/// <summary>One runes.txt row as the builder's list shows it.</summary>
public sealed record RunewordEntry(int Row, string SourceId, string Key, string Name, string Runes, bool Complete)
{
    public bool Inactive => Key.Length == 0;
    public string SearchText { get; } = (Key + " " + Name + " " + Runes).ToLowerInvariant();
}

/// <summary>A runes.txt row's identity and runes, read on the UI thread.</summary>
public sealed record RunewordRow(int Row, string SourceId, string Key, string Comment, string Complete, string Runes);

public sealed record RunewordBuilderCatalog(RunewordEntry[] Entries, RunewordRune[] Runes, RunewordType[] Types, BuilderProperty[] Properties, string[] Issues);

/// <summary>What a runeword needs: its rune count, the level its runes require, and every base that can hold it, lowest drop level first; plus bases whose sockets never reach.</summary>
public sealed record RunewordFit(int Sockets, int RuneLevel, RunewordBase[] Bases, string[] NeverEnoughSockets);

/// <summary>What the runeword builder reads besides the row being edited. Worker-owned; reads authored data only.</summary>
public sealed class RunewordBuilderResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();

    public RunewordBuilderCatalog Catalog(ModProject project, string profile, string locale, IReadOnlyList<RunewordRow> rows, CancellationToken token)
    {
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var catalog = new ItemCatalog(data);
        var runes = catalog.Items.Where(i => catalog.ItemIs(i.Row, "rune")).Select(i => new RunewordRune(i.Row.S("code"), catalog.ItemName(i.Row.S("code")), DropCalculator.Int(i.Row, "levelreq")))
            .OrderBy(r => r.Code, StringComparer.Ordinal).ToArray();
        var names = runes.ToDictionary(r => r.Code, r => r.Name.Replace(" Rune", ""), StringComparer.Ordinal);
        token.ThrowIfCancellationRequested();
        var entries = rows.Select(r => new RunewordEntry(r.Row, r.SourceId, r.Key, r.Key.Length == 0 ? "" : data.Localize(r.Key, false) is var name && name != r.Key ? name : r.Comment.Length > 0 ? r.Comment : r.Key,
            string.Join(" ", r.Runes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(code => names.GetValueOrDefault(code, code))), r.Complete == "1")).ToArray();
        var types = data.Rows("itemtypes", false).Where(t => t.S("Code").Length > 0).Select(t => new RunewordType(t.S("Code"), t.S("ItemType"))).DistinctBy(t => t.Code).ToArray();
        var properties = data.Rows("properties", false).Where(p => p.S("code").Length > 0)
            .Select(p => new BuilderProperty(p.S("code"), p.S("*Tooltip"), p.S("*Parameter"), p.S("*Min"), p.S("*Max"), p.S("*Notes"))).DistinctBy(p => p.Code, StringComparer.Ordinal).ToArray();
        return new(entries, runes, types, properties, [.. issues.Distinct()]);
    }

    /// <summary>The bases a runeword row can be made in, as the recipe preview counts them, with the numbers the builder draws.</summary>
    public RunewordFit Fit(ModProject project, JsonObject record, string profile, string locale, CancellationToken token)
    {
        var data = tables.Open(project, profile, locale, [], token);
        var catalog = new ItemCatalog(data);
        var row = data.Effective("runes", record);
        var runes = Enumerable.Range(1, 6).Select(i => row.S("Rune" + i)).Where(r => r.Length > 0).ToArray();
        int runeLevel = runes.Select(r => catalog.Item(r)?.Row).OfType<JsonObject>().Select(r => DropCalculator.Int(r, "levelreq")).DefaultIfEmpty(0).Max();
        var allowed = Enumerable.Range(1, 6).Select(i => row.S("itype" + i)).Where(t => t.Length > 0).ToArray();
        var excluded = Enumerable.Range(1, 3).Select(i => row.S("etype" + i)).Where(t => t.Length > 0).ToArray();
        var bases = new List<RunewordBase>(); var never = new List<string>();
        foreach (var (table, item) in catalog.Items)
        {
            token.ThrowIfCancellationRequested();
            if (table == "misc" || !allowed.Any(t => catalog.ItemIs(item, t)) || excluded.Any(t => catalog.ItemIs(item, t))) continue;
            var code = item.S("code");
            var from = Enumerable.Range(1, 99).FirstOrDefault(l => catalog.MaxSockets(item, l) >= Math.Max(1, runes.Length));
            if (from == 0) { never.Add(catalog.ItemName(code)); continue; }
            bases.Add(new(code, catalog.ItemName(code), table, ItemSprites.Tier(item, code), DropCalculator.Int(item, "level"), from, DropCalculator.Int(item, "levelreq")));
        }
        return new(runes.Length, runeLevel, [.. bases.OrderBy(b => b.Qlvl).ThenBy(b => b.Name, StringComparer.Ordinal)], [.. never]);
    }

    public static RunewordRow[] Rows(TableData table)
    {
        string Cell(int row, string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        return [.. Enumerable.Range(0, table.Records.Count).Select(i => new RunewordRow(i, table.Records[i].S("sourceId"), Cell(i, "Name"), Cell(i, "*Rune Name"), Cell(i, "complete"),
            string.Join(",", Enumerable.Range(1, 6).Select(n => Cell(i, "Rune" + n)).Where(r => r.Length > 0))))];
    }
}
