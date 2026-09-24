using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// A base item (weapons, armor or misc row) as the game shows it: the tooltip lines, top to bottom, and the facts a
/// modder needs beside it (tier and its codes, type, drop level, sockets by item level, price). Every value keeps the
/// cells it was read from.
/// </summary>
public sealed record BaseItemPreviewResult(string Name, string Tier, PreviewText[] Tooltip, (string Label, PreviewText Value)[] Facts, string[] Issues);

/// <summary>One weapons/armor/misc row as the builder's search lists it.</summary>
public sealed record BaseItemEntry(int Row, string SourceId, string Code, string Name, string Type, string Level, string Tier)
{
    public bool Inactive => Code.Length == 0;
    public string SearchText { get; } = string.Join(' ', Code, Name, Type, Tier).ToLowerInvariant();
}

/// <summary>A base item row's identity, read on the UI thread for the search list.</summary>
public sealed record BaseItemRow(int Row, string SourceId, string Code, string Name, string NameStr, string Type, string Level, string NormCode, string UberCode, string UltraCode);

/// <summary>A unique or set item made on a base code.</summary>
public sealed record BaseItemUse(string Table, string Index, string Name);

public sealed record BaseItemBuilderCatalog(BaseItemEntry[] Entries, (string Code, string Name)[] ItemTypes, string[] Codes, string[] Missiles, string[] Sounds, ILookup<string, BaseItemUse> Uses, string[] Issues);

/// <summary>Worker-owned resolver for a weapons, armor or misc row. Reads authored data only; never changes records or starts a build.</summary>
public sealed class BaseItemPreviewResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();
    public static bool Supports(string? table) => table is "weapons" or "armor" or "misc";

    /// <summary>normal, exceptional or elite: which of the row's tier codes is its own code.</summary>
    public static string Tier(Func<string, string> cell) =>
        cell("code") is { Length: > 0 } code ? code == cell("ultracode") ? "Elite" : code == cell("ubercode") ? "Exceptional" : "Normal" : "";

    public BaseItemPreviewResult Resolve(ModProject project, string table, JsonObject record, string profile, string locale, CancellationToken token)
    {
        Require(Supports(table), "Select a row in weapons, armor or misc.");
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var item = data.Effective(table, record);
        string Cell(string column) => item.S(column);
        string Link(string text, params string[] columns) => data.Link(text, item, columns);
        int Int(string column) => int.TryParse(Cell(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        bool Has(string column) => Int(column) != 0;
        if (Cell("code").Length == 0) return new(Cell("name") is { Length: > 0 } header ? header : "Inactive row", "", [PreviewText.Parse("Inactive/header row · no code")], [], []);
        var name = Cell("namestr") is { Length: > 0 } key ? data.Localize(key) : Cell("name");
        var lines = new List<string> { Link(name, "namestr") };
        string Range(string low, string high) => Int(low) == Int(high) ? Link(Cell(low), low, high) : $"{Link(Cell(low), low)} to {Link(Cell(high), high)}";
        var type = data.Find("itemtypes", "Code", Cell("type"), false);

        if (table == "weapons")
        {
            bool twoHanded = Has("2handed"), either = Has("1or2handed");
            if (!twoHanded || either) { if (Has("maxdam")) lines.Add("One-Hand Damage: " + Range("mindam", "maxdam")); }
            if (twoHanded && Has("2handmaxdam")) lines.Add("Two-Hand Damage: " + Range("2handmindam", "2handmaxdam"));
            if (Has("maxmisdam")) lines.Add("Throw Damage: " + Range("minmisdam", "maxmisdam"));
        }
        if (table == "armor")
        {
            if (Has("maxac")) lines.Add("Defense: " + (Int("minac") == Int("maxac") ? Link(Cell("maxac"), "minac", "maxac") : $"{Link(Cell("minac"), "minac")}–{Link(Cell("maxac"), "maxac")}"));
            if (Has("block")) lines.Add($"Chance to Block: {Link(Cell("block"), "block")}%");
            if (Has("maxdam")) lines.Add((Cell("type") == "boot" ? "Kick Damage: " : "Smite Damage: ") + Range("mindam", "maxdam"));
        }
        if (Has("stackable") && Has("maxstack")) lines.Add("Quantity: " + (Int("minstack") == Int("maxstack") ? Link(Cell("maxstack"), "maxstack") : $"{Link(Cell("minstack"), "minstack")}–{Link(Cell("maxstack"), "maxstack")}"));
        if (!Has("nodurability") && Has("durability")) lines.Add($"Durability: {Link(Cell("durability"), "durability")} of {Link(Cell("durability"), "durability")}");
        // The game lists Dexterity above Strength, then the level.
        if (Has("reqdex")) lines.Add("Required Dexterity: " + Link(Cell("reqdex"), "reqdex"));
        if (Has("reqstr")) lines.Add("Required Strength: " + Link(Cell("reqstr"), "reqstr"));
        if (Has("levelreq")) lines.Add("Required Level: " + Link(Cell("levelreq"), "levelreq"));
        if (table == "weapons" && Cell("speed").Length > 0 && Int("speed") != 0) lines.Add($"Attack speed modifier: {Link(Cell("speed"), "speed")}");
        if (Cell("spelldescstr") is { Length: > 0 } use) lines.Add(Link(data.Localize(use, false), "spelldescstr"));
        if (Has("gemsockets")) lines.Add($"Up to {Link(Cell("gemsockets"), "gemsockets")} sockets");

        var facts = new List<(string, string)>();
        string tier = Tier(Cell);
        facts.Add(("Code", Link(Cell("code"), "code")));
        if (tier.Length > 0 && (Cell("ubercode").Length > 0 || Cell("ultracode").Length > 0))
            facts.Add(("Tier", $"{tier} · {Link(Cell("normcode"), "normcode")} / {Link(Cell("ubercode"), "ubercode")} / {Link(Cell("ultracode"), "ultracode")}"));
        if (Cell("type").Length > 0)
            facts.Add(("Type", Link((type != null ? type.S("ItemType") + " " : "") + $"({Cell("type")})", "type") + (Cell("type2").Length > 0 ? " · " + Link(Cell("type2"), "type2") : "")));
        if (type == null && Cell("type").Length > 0) issues.Add($"Unresolved itemtypes/Code: {Cell("type")}.");
        facts.Add(("Drop level", Link(Cell("level").Length > 0 ? Cell("level") : "0", "level") + (Cell("magic lvl").Length > 0 ? $" · magic level {Link(Cell("magic lvl"), "magic lvl")}" : "")));
        if (Cell("rarity").Length > 0) facts.Add(("Rarity", Link(Cell("rarity"), "rarity") + (Cell("spawnable") == "0" ? " · never drops (spawnable 0)" : "")));
        if (type != null && Has("gemsockets"))
        {
            // Sockets an item can roll are capped by both its own gemsockets and its type's limit for the item level.
            int own = Int("gemsockets");
            int Cap(string column) => Math.Min(own, int.TryParse(type.S(column), out var value) ? value : own);
            string threshold1 = type.S("MaxSocketsLevelThreshold1"), threshold2 = type.S("MaxSocketsLevelThreshold2");
            facts.Add(("Sockets", $"{Cap("MaxSockets1")} up to ilvl {threshold1}, {Cap("MaxSockets2")} up to {threshold2}, {Cap("MaxSockets3")} above"));
        }
        if (Cell("cost").Length > 0) facts.Add(("Cost", Link(Cell("cost"), "cost") + (Cell("gamble cost").Length > 0 ? $" · gamble {Link(Cell("gamble cost"), "gamble cost")}" : "")));
        if (table == "weapons" && (Has("StrBonus") || Has("DexBonus")))
            facts.Add(("Damage bonus", $"{Link(Cell("StrBonus").Length > 0 ? Cell("StrBonus") : "0", "StrBonus")}% per 100 Strength · {Link(Cell("DexBonus").Length > 0 ? Cell("DexBonus") : "0", "DexBonus")}% per 100 Dexterity"));
        if (Cell("invwidth").Length > 0) facts.Add(("Inventory size", $"{Link(Cell("invwidth"), "invwidth")} × {Link(Cell("invheight"), "invheight")}"));
        return new(name, tier, [.. lines.Select(PreviewText.Parse)], [.. facts.Select(f => (f.Item1, PreviewText.Parse(f.Item2)))], [.. issues.Distinct()]);
    }

    public BaseItemBuilderCatalog Catalog(ModProject project, string profile, string locale, IReadOnlyList<BaseItemRow> rows, CancellationToken token)
    {
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var types = data.Rows("itemtypes", false).Where(t => t.S("Code").Length > 0).Select(t => (t.S("Code"), t.S("ItemType"))).DistinctBy(t => t.Item1).ToArray();
        var typeNames = types.ToDictionary(t => t.Item1, t => t.Item2, StringComparer.Ordinal);
        var entries = rows.Select(r => new BaseItemEntry(r.Row, r.SourceId, r.Code, r.NameStr.Length > 0 ? data.Localize(r.NameStr, false) : r.Name,
            r.Type.Length == 0 ? "" : typeNames.GetValueOrDefault(r.Type, r.Type), r.Level,
            r.Code.Length == 0 ? "" : r.Code == r.UltraCode ? "Elite" : r.Code == r.UberCode ? "Exceptional" : r.UberCode.Length > 0 || r.UltraCode.Length > 0 ? "Normal" : "")).ToArray();
        token.ThrowIfCancellationRequested();
        var codes = new[] { "weapons", "armor", "misc" }.SelectMany(t => data.Rows(t, false)).Select(r => r.S("code")).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var uses = data.Rows("uniqueitems", false).Where(u => u.S("code").Length > 0).Select(u => (Code: u.S("code"), Use: new BaseItemUse("uniqueitems", u.S("index"), data.Localize(u.S("index"), false))))
            .Concat(data.Rows("setitems", false).Where(s => s.S("item").Length > 0).Select(s => (Code: s.S("item"), Use: new BaseItemUse("setitems", s.S("index"), data.Localize(s.S("index"), false)))))
            .ToLookup(u => u.Code, u => u.Use, StringComparer.Ordinal);
        var missiles = data.Rows("missiles", false).Select(m => m.S("Missile")).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var sounds = data.Rows("sounds", false).Select(s => s.S("Sound")).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return new(entries, types, codes, missiles, sounds, uses, [.. issues.Distinct()]);
    }

    public static BaseItemRow[] Rows(TableData table)
    {
        string Cell(int row, string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        return [.. Enumerable.Range(0, table.Records.Count).Select(i => new BaseItemRow(i, table.Records[i].S("sourceId"), Cell(i, "code"), Cell(i, "name"), Cell(i, "namestr"), Cell(i, "type"),
            Cell(i, "level"), Cell(i, "normcode"), Cell(i, "ubercode"), Cell(i, "ultracode")))];
    }
}
