using System.Globalization;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>The identity of a unique or set item row, read on the UI thread for the builder's search list.</summary>
public sealed record BuilderRow(int Row, string SourceId, string Index, string Code, string RequiredLevel, string Set);

/// <summary>One unique or set item as the builder's search lists it: its row, localized name and base.</summary>
public sealed record BuilderEntry(int Row, string SourceId, string Index, string Name, string Code, string BaseName, string RequiredLevel, string Set)
{
    /// <summary>Header and separator rows (no base code) are listed but cannot hold an item.</summary>
    public bool Inactive => Code.Length == 0;
    public string SearchText { get; } = string.Join(' ', Index, Name, Code, BaseName, Set).ToLowerInvariant();
}

/// <summary>A property code offered by the builder, with what properties.txt documents about its parameter and range.</summary>
public sealed record BuilderProperty(string Code, string Tooltip, string Parameter, string Min, string Max, string Notes)
{
    public string SearchText { get; } = string.Join(' ', Code, Tooltip, Notes).ToLowerInvariant();
    public override string ToString() => Code;
}

/// <summary>A base item code (weapons, armor or misc) with its localized name and its tier's HD asset key (normal, uber, ultra).</summary>
public sealed record BuilderBase(string Code, string Name, string Table, string RequiredLevel, string Tier)
{
    public string SearchText { get; } = (Code + " " + Name).ToLowerInvariant();
    public override string ToString() => Code;
}

/// <summary>A set a set item can belong to: its sets.txt index and localized name.</summary>
public sealed record BuilderSet(string Index, string Name)
{
    public string SearchText { get; } = (Index + " " + Name).ToLowerInvariant();
    public override string ToString() => Index;
}

public sealed record VisualBuilderCatalog(BuilderEntry[] Entries, BuilderProperty[] Properties, BuilderBase[] Bases, BuilderSet[] Sets, string[] Issues);

/// <summary>
/// The columns one property is written in. <see cref="Pieces"/> is the set-item bonus tier (aprop2a is worn with 3
/// pieces under add func 2); 0 for the item's own properties.
/// </summary>
public sealed record PropertySlot(string Prop, string Parameter, string Min, string Max, int Pieces)
{
    public bool SetBonus => Pieces > 0;
    public IEnumerable<string> Columns => [Prop, Parameter, Min, Max];
}

/// <summary>What the Visual Builder reads besides the row being edited. Worker-owned; reads authored data only.</summary>
public sealed class VisualBuilderResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();

    public VisualBuilderCatalog Catalog(ModProject project, string profile, string locale, IReadOnlyList<BuilderRow> rows, CancellationToken token)
    {
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var bases = new List<BuilderBase>();
        foreach (var table in new[] { "weapons", "armor", "misc" })
            foreach (var row in data.Rows(table, false))
            {
                var code = row.S("code"); if (code.Length == 0) continue;
                bases.Add(new(code, data.Localize(row.S("namestr", code), false), table, row.S("levelreq"), ItemSprites.Tier(row, code)));
            }
        var baseNames = bases.GroupBy(b => b.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        token.ThrowIfCancellationRequested();
        var entries = rows.Select(r => new BuilderEntry(r.Row, r.SourceId, r.Index, r.Index.Length == 0 ? "" : data.Localize(r.Index, false), r.Code,
            baseNames.GetValueOrDefault(r.Code, ""), r.RequiredLevel, r.Set.Length == 0 ? "" : data.Localize(r.Set, false))).ToArray();
        var properties = data.Rows("properties", false).Where(p => p.S("code").Length > 0)
            .Select(p => new BuilderProperty(p.S("code"), p.S("*Tooltip"), p.S("*Parameter"), p.S("*Min"), p.S("*Max"), p.S("*Notes")))
            .DistinctBy(p => p.Code, StringComparer.Ordinal).ToArray();
        var sets = data.Rows("sets", false).Where(s => s.S("index").Length > 0).Select(s => new BuilderSet(s.S("index"), data.Localize(s.S("index"), false))).ToArray();
        return new(entries, properties, [.. bases.DistinctBy(b => b.Code, StringComparer.Ordinal)], sets, [.. issues.Distinct()]);
    }
}

public static partial class VisualBuilder
{
    public static bool Supports(string? table) => table is "uniqueitems" or "setitems" or "missiles" or "monstats" or "weapons" or "armor" or "misc" or "cubemain";

    /// <summary>The search list's view of a table: every row's identity, base code, required level and set.</summary>
    public static BuilderRow[] Rows(TableData table)
    {
        string code = table.Name == "setitems" ? "item" : "code";
        return [.. Enumerable.Range(0, table.Records.Count).Select(i => new BuilderRow(i, table.Records[i].S("sourceId"), table.Cell(i, "index"), table.Cell(i, code), table.Cell(i, "lvl req"), table.Cell(i, "set")))];
    }

    /// <summary>
    /// The property slots a table's columns hold, in column order: propN/parN/minN/maxN, and for set items the
    /// apropNa/b bonus slots. A slot is offered only when all four of its columns exist.
    /// </summary>
    public static PropertySlot[] Slots(string table, IReadOnlyList<string> columns)
    {
        var present = columns.ToHashSet(StringComparer.Ordinal); var slots = new List<PropertySlot>();
        foreach (var column in columns)
        {
            if (Own().Match(column) is { Success: true } own && present.IsSupersetOf(["par" + own.Groups[1].Value, "min" + own.Groups[1].Value, "max" + own.Groups[1].Value]))
                slots.Add(new(column, "par" + own.Groups[1].Value, "min" + own.Groups[1].Value, "max" + own.Groups[1].Value, 0));
            else if (table == "setitems" && Bonus().Match(column) is { Success: true } bonus)
            {
                var n = bonus.Groups[1].Value + bonus.Groups[2].Value;
                if (present.IsSupersetOf(["apar" + n, "amin" + n, "amax" + n]))
                    slots.Add(new(column, "apar" + n, "amin" + n, "amax" + n, int.Parse(bonus.Groups[1].Value, CultureInfo.InvariantCulture) + 1));
            }
        }
        return [.. slots];
    }

    /// <summary>Whether an item row's search text matches every word of a query.</summary>
    public static bool Matches(string searchText, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => searchText.Contains(word, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("^prop([0-9]+)$")] private static partial Regex Own();
    [GeneratedRegex("^aprop([0-9]+)([ab])$")] private static partial Regex Bonus();
}
