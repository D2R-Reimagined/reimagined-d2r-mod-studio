using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// Base items (weapons, armor, misc) and item types over the preview tables: names, the Equiv1/Equiv2 inheritance of
/// types, class restrictions and how many sockets an item can have. Worker-owned; reads authored data only.
/// </summary>
public sealed class ItemCatalog
{
    private readonly PreviewTables.Session data;
    private readonly Dictionary<string, (string Table, JsonObject Row)> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> ancestors = new(StringComparer.Ordinal);
    public PreviewTables.Session Data => data;

    public ItemCatalog(PreviewTables.Session data)
    {
        this.data = data;
        foreach (var table in new[] { "weapons", "armor", "misc" })
            foreach (var row in data.Rows(table, false)) if (row.S("code") is { Length: > 0 } code) items.TryAdd(code, (table, row));
        foreach (var row in data.Rows("itemtypes", false)) if (row.S("Code") is { Length: > 0 } code) types.TryAdd(code, row);
    }

    public IEnumerable<(string Table, JsonObject Row)> Items => items.Values;
    public (string Table, JsonObject Row)? Item(string code) => items.TryGetValue(code, out var item) ? item : null;
    public JsonObject? Type(string code) => types.GetValueOrDefault(code);
    public bool HasType(string code) => types.ContainsKey(code);

    /// <summary>A type and every type it inherits through Equiv1/Equiv2, nearest first.</summary>
    public IReadOnlyList<string> Ancestors(string type)
    {
        if (ancestors.TryGetValue(type, out var cached)) return cached;
        var result = new List<string>(); var pending = new Queue<string>(); pending.Enqueue(type);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (current.Length == 0 || result.Contains(current)) continue;
            result.Add(current);
            if (types.TryGetValue(current, out var row)) { pending.Enqueue(row.S("Equiv1")); pending.Enqueue(row.S("Equiv2")); }
        }
        return ancestors[type] = [.. result];
    }
    public bool IsType(string type, string ancestor) => type.Length > 0 && Ancestors(type).Contains(ancestor);
    /// <summary>Whether a base item is of a type, through its type or type2.</summary>
    public bool ItemIs(JsonObject item, string type) => IsType(item.S("type"), type) || IsType(item.S("type2"), type);
    public IEnumerable<string> ItemTypes(JsonObject item) => Ancestors(item.S("type")).Concat(Ancestors(item.S("type2"))).Distinct();

    /// <summary>The class a base item belongs to (from its item types' Class), or empty for every class.</summary>
    public string ItemClass(JsonObject item) => ItemTypes(item).Select(t => types.GetValueOrDefault(t)?.S("Class") ?? "").FirstOrDefault(c => c.Length > 0) ?? "";

    /// <summary>
    /// The most sockets a base item can have at an item level: its gemsockets, capped by its type's MaxSockets for the
    /// level band (MaxSockets1 up to MaxSocketsLevelThreshold1, then MaxSockets2, then MaxSockets3).
    /// </summary>
    public int MaxSockets(JsonObject item, int itemLevel)
    {
        int own = DropCalculator.Int(item, "gemsockets");
        if (types.GetValueOrDefault(item.S("type")) is not { } type) return own;
        int first = DropCalculator.Int(type, "MaxSocketsLevelThreshold1"), second = DropCalculator.Int(type, "MaxSocketsLevelThreshold2");
        var column = first > 0 && itemLevel <= first ? "MaxSockets1" : second > 0 && itemLevel <= second ? "MaxSockets2" : "MaxSockets3";
        return type.S(column).Length > 0 ? Math.Min(own, DropCalculator.Int(type, column)) : own;
    }

    public string ItemName(string code)
    {
        if (Item(code) is not { } item) return code;
        return item.Row.S("namestr") is { Length: > 0 } key ? data.Localize(key, false) : item.Row.S("name") is { Length: > 0 } name ? name : code;
    }
    public string TypeName(string code) => types.GetValueOrDefault(code)?.S("ItemType") is { Length: > 0 } name ? name : code;
    /// <summary>"Shako (uap)" for an item code, "any Helm (helm)" for a type, the text itself otherwise.</summary>
    public string Describe(string code) => Item(code) != null ? $"{ItemName(code)} ({code})" : HasType(code) ? $"any {TypeName(code)} ({code})" : code;
}

/// <summary>A property (properties.txt code with param, min and max) as its tooltip line would read.</summary>
public static class PropertyText
{
    /// <summary>
    /// "+130–160% Enhanced Defense": the first stat the property sets, shown through its descfunc at the maximum with the
    /// range put in place of the number. Properties that do not simply set a stat to min–max are shown as authored.
    /// </summary>
    public static string Describe(PreviewTables.Session data, string code, string parameter, string min, string max, List<string>? issues = null)
    {
        var authored = $"{code}" + (parameter.Length > 0 ? $" {parameter}" : "") + (min.Length > 0 || max.Length > 0 ? $" {Range(min, max)}" : "");
        if (code.Length == 0) return "";
        var property = data.Find("properties", "code", code, false);
        if (property == null)
        {
            if (data.Find("propertygroups", "code", code, false) is { } group) return Group(data, group, issues);
            issues?.Add($"Unresolved properties/code: {code}."); return authored + "  (unknown property)";
        }
        // A comment template ("+#% Faster Hit Recovery") some mods keep in properties.txt, used when the stat cannot be read.
        string Template() => property.S("*Tooltip") is { Length: > 0 } template && template.Count(c => c == '#') == 1
            ? template.Replace("#", Range(min, max) is { Length: > 0 } range ? range : parameter).Replace("[Skill]", parameter.Length > 0 ? parameter : "[Skill]") : authored;
        // Functions 1–4 and 8 roll the stat between min and max; 7 is enhanced damage; 17 sets a per-level stat from the parameter.
        var stat = property.S("func1") switch { "1" or "2" or "3" or "4" or "8" or "17" => property.S("stat1"), "7" => "item_maxdamage_percent", _ => "" };
        if (stat.Length == 0) return Template();
        var cost = data.Find("itemstatcost", "Stat", stat, false);
        if (cost == null || cost.S("descfunc") is "" or "0") return Template();
        if (property.S("func1") == "17")
        {
            if (!decimal.TryParse(parameter, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)) return Template();
            var perLevel = cost.S("op").Trim() is "2" or "3" or "4" or "5" && cost.S("op base").Trim() is "" or "level";
            var value = perLevel ? StatPreviewResolver.AtLevel(cost, rate, 99) : rate;
            return StatPreviewResolver.Describe(data, cost, "desc", value, parameter, issues ?? []) + (perLevel ? "  (at character level 99)" : "");
        }
        if (!decimal.TryParse(max.Length > 0 ? max : min, NumberStyles.Number, CultureInfo.InvariantCulture, out var high)) return Template();
        decimal low = decimal.TryParse(min, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : high;
        var text = StatPreviewResolver.Describe(data, cost, "desc", high, parameter, issues ?? []);
        if (text.Contains("(approximate", StringComparison.Ordinal)) return Template();
        var number = Text(Math.Abs(high));
        int at = text.IndexOf(number, StringComparison.Ordinal);
        if (low != high && at >= 0) text = text[..at] + $"{Text(Math.Abs(low))}–{number}" + text[(at + number.Length)..];
        // Proc and group properties apply several stats; name the rest so the line is not taken for the whole effect.
        var others = Enumerable.Range(2, 6).Count(i => property.S("stat" + i).Length > 0);
        return others > 0 ? $"{text}  (+{others} more stat{(others == 1 ? "" : "s")})" : text;
    }
    /// <summary>A propertygroups.txt row: how it picks, then each property it can give with its share of the tickets.</summary>
    private static string Group(PreviewTables.Session data, JsonObject group, List<string>? issues)
    {
        var entries = Enumerable.Range(1, 8).Where(i => group.S("Prop" + i).Length > 0).Select(i => (Index: i, Tickets: DropCalculator.Int(group, "Chance" + i))).ToArray();
        double total = entries.Sum(e => (double)e.Tickets);
        var mode = group.S("PickMode") switch { "0" => "all of", "1" => "drawn like cards from", _ => "rolled from" };
        var parts = entries.Select(e =>
        {
            var parameter = Range(group.S("ParMin" + e.Index), group.S("ParMax" + e.Index));
            var text = Describe(data, group.S("Prop" + e.Index), parameter, group.S("ModMin" + e.Index), group.S("ModMax" + e.Index), issues);
            return group.S("PickMode") == "0" || total <= 0 ? text : $"{text} ({Percent(e.Tickets / total, 0)})";
        });
        return $"property group {group.S("code")}, {mode}: {string.Join(" | ", parts)}";
    }
    private static string Range(string min, string max) => min == max || max.Length == 0 ? min : min.Length == 0 ? max : $"{min}–{max}";
}
