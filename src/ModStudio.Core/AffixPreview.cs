using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <param name="Rare">Only affixes a rare item may take (rare = 1).</param>
/// <param name="Filter">Only list affixes whose name or effect contains this.</param>
public sealed record AffixPreviewOptions(int ItemLevel = 85, bool Rare = false, string Filter = "");

/// <summary>
/// One affix in a pool: its levels, weight and share of the roll, and what it grants. <paramref name="Sources"/> holds the
/// affix row's cells behind each table column, by column header.
/// </summary>
public sealed record AffixLine(string Name, string Levels, string Required, string Frequency, string Chance, string Group, string Effect, IReadOnlyDictionary<string, CellLink[]>? Sources = null);

public sealed record AffixPreviewResult(string Name, string[] Lines, PreviewSection[] Sections, AffixLine[] Prefixes, AffixLine[] Suffixes, string[] Issues)
{
    public PreviewText[] Text { get; } = PreviewText.Parse(Lines);
    public string[] Lines { get; } = PreviewText.Plain(Lines);
}

/// <summary>
/// Worker-owned resolver for the Affix Preview. On a base item (weapons, armor, misc) it lists the prefixes and suffixes
/// that can roll at an item level, weighted by frequency; on an affix row (magicprefix, magicsuffix, automagic) it lists
/// the base items it can appear on and the item level each needs. Reads authored data only.
/// </summary>
/// <remarks>
/// Affixes are chosen by affix level, not item level: the item level (raised to the item's qlvl, at most 99) plus the
/// item's magic lvl when it has one; otherwise item level − qlvl ÷ 2, or 2 × item level − 99 once the item level reaches
/// 99 − qlvl ÷ 2. An affix fits when it is spawnable, has a frequency, its level is at most the affix level (and its
/// maxlevel, when set, at least), the item is one of its itypes and none of its etypes, and it is not for another class.
/// </remarks>
public sealed class AffixPreviewResolver
{
    public static readonly string[] BaseTables = ["weapons", "armor", "misc"];
    public static readonly string[] AffixTables = ["magicprefix", "magicsuffix", "automagic"];
    private const int Shown = 80;
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();

    /// <summary>The affix level a base item rolls its affixes at, for an item level.</summary>
    public static int AffixLevel(int itemLevel, int qualityLevel, int magicLevel)
    {
        int level = Math.Min(Math.Max(itemLevel, qualityLevel), 99);
        int affix = magicLevel > 0 ? level + magicLevel : level < 99 - qualityLevel / 2 ? level - qualityLevel / 2 : 2 * level - 99;
        return Math.Min(affix, 99);
    }

    public AffixPreviewResult Resolve(ModProject project, string table, JsonObject record, string profile, string locale, CancellationToken token, AffixPreviewOptions? options = null)
    {
        Require(BaseTables.Contains(table) || AffixTables.Contains(table), "Select a base item or an affix row.");
        options ??= new();
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var row = data.Effective(table, record);
        var catalog = new ItemCatalog(data);
        return BaseTables.Contains(table) ? Pool(catalog, row, options, issues, token) : Affix(catalog, table, row, issues, token);
    }

    /// <summary>Why an affix cannot roll on an item at an affix level, or null when it can.</summary>
    public static string? Misfit(ItemCatalog catalog, JsonObject affix, JsonObject item, int affixLevel, bool rare)
    {
        if (affix.S("spawnable") != "1") return "not spawnable";
        if (DropCalculator.Int(affix, "frequency") <= 0) return "frequency 0";
        if (rare && !Flag(affix, "rare")) return "not for rares";
        var itypes = Enumerable.Range(1, 7).Select(i => affix.S("itype" + i)).Where(t => t.Length > 0).ToArray();
        if (!itypes.Any(t => catalog.ItemIs(item, t))) return "type";
        if (Enumerable.Range(1, 5).Select(i => affix.S("etype" + i)).Any(t => t.Length > 0 && catalog.ItemIs(item, t))) return "excluded type";
        if (affix.S("classspecific") is { Length: > 0 } cls && catalog.ItemClass(item) != cls) return "other class";
        if (DropCalculator.Int(affix, "level") > affixLevel) return "level";
        if (DropCalculator.Int(affix, "maxlevel") is > 0 and var max && affixLevel > max) return "maxlevel";
        return null;
    }

    /// <summary>What each mod grants, linked to its code, parameter, min and max cells.</summary>
    private static string[] EffectLines(PreviewTables.Session data, JsonObject affix, List<string> issues) =>
        [.. Enumerable.Range(1, 3).Where(i => affix.S($"mod{i}code").Length > 0)
            .Select(i => data.Link(PropertyText.Describe(data, affix.S($"mod{i}code"), affix.S($"mod{i}param"), affix.S($"mod{i}min"), affix.S($"mod{i}max"), issues),
                affix, [.. new[] { "code", "param", "min", "max" }.Select(p => $"mod{i}{p}").Where(c => affix.S(c).Length > 0)]))];
    private static string Effects(PreviewTables.Session data, JsonObject affix, List<string> issues) => string.Join(" · ", EffectLines(data, affix, issues));
    private static CellLink[] Cells(PreviewTables.Session data, JsonObject row, params string[] columns) =>
        [.. columns.Where(c => row.S(c).Length > 0).Select(c => data.Cell(row, c)).OfType<CellLink>()];
    /// <summary>The affix's shown name; some mods point hidden affixes at a string that is empty or only a color code.</summary>
    internal static string AffixName(PreviewTables.Session data, JsonObject affix) => Name(data, affix);
    private static string Name(PreviewTables.Session data, JsonObject affix)
    {
        var key = affix.S("Name");
        if (key.Length == 0) return "(unnamed)";
        var text = data.Localize(key, false).Trim();
        return text.Length > 0 ? text : $"{key} (no visible name)";
    }
    private static string Levels(JsonObject affix) => affix.S("level", "0") + (DropCalculator.Int(affix, "maxlevel") > 0 ? "–" + affix.S("maxlevel") : "+");

    private static AffixPreviewResult Pool(ItemCatalog catalog, JsonObject item, AffixPreviewOptions options, List<string> issues, CancellationToken token)
    {
        var code = item.S("code");
        if (code.Length == 0) return new("Inactive row", ["Inactive/header row · no item code"], [], [], [], []);
        var data = catalog.Data;
        int qlvl = DropCalculator.Int(item, "level"), magicLevel = DropCalculator.Int(item, "magic lvl");
        int itemLevel = Math.Clamp(options.ItemLevel, 1, 99);
        int affixLevel = AffixLevel(itemLevel, qlvl, magicLevel);
        string Link(string text, params string[] columns) => data.Link(text, item, columns);
        var lines = new List<string>
        {
            $"{Link($"{catalog.ItemName(code)} ({code})", "code")} · {Link(string.Join(" → ", catalog.ItemTypes(item).Select(catalog.TypeName)), "type", "type2")}",
            Link($"qlvl {qlvl}", "level") + (magicLevel > 0 ? $" · {Link($"magic lvl {magicLevel}", "magic lvl")}" : "") + (catalog.ItemClass(item) is { Length: > 0 } cls ? $" · {cls} only" : ""),
            magicLevel > 0 ? $"Item level {itemLevel} + magic lvl {magicLevel} → affix level {affixLevel}"
                : Math.Max(itemLevel, qlvl) < 99 - qlvl / 2 ? $"Item level {Math.Max(itemLevel, qlvl)} − qlvl {qlvl} ÷ 2 → affix level {affixLevel}"
                : $"2 × item level {Math.Max(itemLevel, qlvl)} − 99 → affix level {affixLevel}",
            options.Rare ? "Rare items: affixes marked rare, 3 to 6 of them, never two from the same group" : "Magic items: a prefix, a suffix or both; chances are for one prefix (or suffix) roll"
        };
        if (itemLevel < qlvl) lines.Add($"Item level {itemLevel} is below qlvl {qlvl}, so the affix level counts it as {qlvl}.");
        var sections = new List<PreviewSection>();
        AffixLine[] List(string table, out string locked)
        {
            var rows = data.Rows(table, false);
            var fits = new List<JsonObject>(); var later = new List<JsonObject>();
            foreach (var affix in rows)
            {
                token.ThrowIfCancellationRequested();
                var why = Misfit(catalog, affix, item, affixLevel, options.Rare);
                if (why == null) fits.Add(affix); else if (why == "level") later.Add(affix);
            }
            double total = fits.Sum(a => (double)DropCalculator.Int(a, "frequency"));
            var next = later.GroupBy(a => DropCalculator.Int(a, "level")).OrderBy(g => g.Key).FirstOrDefault();
            locked = later.Count == 0 ? "" : $"{later.Count} more need a higher affix level; next at {next!.Key}: {string.Join(", ", next.GroupBy(a => Name(data, a)).Take(5).Select(g => data.Link(g.Key, g.First(), "level")))}";
            return fits.Select(a =>
                {
                    var effects = EffectLines(data, a, issues);
                    var sources = new Dictionary<string, CellLink[]>
                    {
                        ["Name"] = Cells(data, a, "Name"), ["Affix lvl"] = Cells(data, a, "level", "maxlevel"), ["Req"] = Cells(data, a, "levelreq"),
                        ["Chance"] = Cells(data, a, "frequency"), ["Group"] = Cells(data, a, "group"),
                        ["Effect"] = Cells(data, a, [.. Enumerable.Range(1, 3).SelectMany(i => new[] { "code", "param", "min", "max" }.Select(p => $"mod{i}{p}"))]),
                    };
                    return (Row: a, Line: new AffixLine(Name(data, a), Levels(a), a.S("levelreq"), a.S("frequency"),
                        Percent(DropCalculator.Int(a, "frequency") / total), a.S("group"), string.Join(" · ", effects.Select(PreviewText.Plain)), sources));
                })
                .Where(x => options.Filter.Trim().Length == 0 || x.Line.Name.Contains(options.Filter.Trim(), StringComparison.OrdinalIgnoreCase) || x.Line.Effect.Contains(options.Filter.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => DropCalculator.Int(x.Row, "frequency")).ThenBy(x => x.Line.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Line).ToArray();
        }
        var prefixes = List("magicprefix", out var lockedPrefixes);
        var suffixes = List("magicsuffix", out var lockedSuffixes);
        lines.Add($"{prefixes.Length} prefixes and {suffixes.Length} suffixes can roll");
        var locked = new[] { ("Prefixes", lockedPrefixes), ("Suffixes", lockedSuffixes) }.Where(l => l.Item2.Length > 0).Select(l => $"{l.Item1}: {l.Item2}").ToArray();
        if (locked.Length > 0) sections.Add(new("Locked at this level", locked));
        if (item.S("auto prefix") is { Length: > 0 } group && group != "0")
        {
            var auto = data.Rows("automagic", false).Where(a => a.S("group") == group && Misfit(catalog, a, item, affixLevel, false) == null).ToArray();
            double total = auto.Sum(a => (double)DropCalculator.Int(a, "frequency"));
            sections.Add(new($"Automagic group {group} (added to every copy)", auto.Length == 0 ? ["No automagic row of this group fits at this affix level."]
                : [.. auto.Select(a => $"{data.Link($"{Percent(DropCalculator.Int(a, "frequency") / total),6}", a, "frequency")}  {Effects(data, a, issues)}  ({data.Link($"level {Levels(a)}", a, "level", "maxlevel")})")]));
        }
        if (prefixes.Length + suffixes.Length == 0) issues.Add("No affix fits this item at this level; it can only spawn normal or superior.");
        sections.Add(new("Assumptions", ["Affixes of every version are allowed, as in the expansion game.", "Shares are among the affixes that fit; the tables are limited to the most frequent " + Shown + "."]));
        return new(catalog.ItemName(code), [.. lines], [.. sections], [.. prefixes.Take(Shown)], [.. suffixes.Take(Shown)], [.. issues.Distinct()]);
    }

    private static AffixPreviewResult Affix(ItemCatalog catalog, string table, JsonObject affix, List<string> issues, CancellationToken token)
    {
        var data = catalog.Data;
        if (affix.S("Name").Length == 0 && affix.S("mod1code").Length == 0) return new("Inactive row", ["Inactive/header row · no affix"], [], [], [], []);
        var rows = data.Rows(table, false);
        int id = -1;
        for (int i = 0; i < rows.Count; i++) if (JsonNode.DeepEquals(rows[i], affix) || ReferenceEquals(rows[i], affix)) { id = i; break; }
        var kind = table switch { "magicprefix" => "Prefix", "magicsuffix" => "Suffix", _ => "Automagic" };
        string Link(string text, params string[] columns) => data.Link(text, affix, columns);
        var lines = new List<string>
        {
            $"{kind}" + (id >= 0 ? $" #{id} (cube pre=/suf= number)" : "") + $" · {Link($"affix level {Levels(affix)}", "level", "maxlevel")} · {Link($"required level {affix.S("levelreq", "0")}", "levelreq")}" +
                (affix.S("classspecific") is { Length: > 0 } cls ? $" · {Link($"{cls} items only", "classspecific")}" : "")
                + (affix.S("class") is { Length: > 0 } levelClass ? $" · {Link($"{levelClass} needs level {affix.S("classlevelreq", "?")}", "class", "classlevelreq")}" : ""),
            $"{Link($"Frequency {affix.S("frequency", "0")}", "frequency")} · {Link($"group {affix.S("group", "none")}", "group")} · " + Link(Flag(affix, "rare") ? "magic and rare items" : "magic items only", "rare")
                + (affix.S("spawnable") == "1" ? "" : $" · {Link("not spawnable", "spawnable")}")
        };
        if (affix.S("spawnable") != "1" || DropCalculator.Int(affix, "frequency") <= 0)
            issues.Add(affix.S("spawnable") != "1" ? "spawnable is not 1, so this affix never rolls on dropped items." : "frequency is 0, so this affix never rolls; cube recipes can still force it.");
        var sections = new List<PreviewSection> { new("Effects", EffectLines(data, affix, issues)) };
        var itypes = Enumerable.Range(1, 7).Select(i => affix.S("itype" + i)).Where(t => t.Length > 0).ToArray();
        var etypes = Enumerable.Range(1, 5).Select(i => affix.S("etype" + i)).Where(t => t.Length > 0).ToArray();
        foreach (var type in itypes.Concat(etypes)) if (!catalog.HasType(type)) issues.Add($"Unresolved itemtypes/Code: {type}.");
        if (itypes.Length == 0) issues.Add("No itype is set, so this affix fits no item.");
        string Types(string prefix, int count) => string.Join(", ", Enumerable.Range(1, count).Where(i => affix.S(prefix + i).Length > 0)
            .Select(i => Link($"{catalog.TypeName(affix.S(prefix + i))} ({affix.S(prefix + i)})", prefix + i)));
        var allowed = "On " + Types("itype", 7) + (etypes.Length > 0 ? "; not on " + Types("etype", 5) : "");

        // Each base item it fits, with the item levels that reach its affix level.
        var bases = new List<(int Qlvl, string Line)>();
        foreach (var (_, item) in catalog.Items)
        {
            token.ThrowIfCancellationRequested();
            if (item.S("spawnable") == "0") continue;
            var reach = Enumerable.Range(1, 99).Where(level => Misfit(catalog, affix, item, AffixLevel(level, DropCalculator.Int(item, "level"), DropCalculator.Int(item, "magic lvl")), false) is null or "not spawnable" or "frequency 0").ToArray();
            if (reach.Length == 0) continue;
            var range = reach[^1] == 99 ? $"item level {reach[0]}+" : $"item levels {reach[0]}–{reach[^1]}";
            bases.Add((DropCalculator.Int(item, "level"), $"{data.Link($"{catalog.ItemName(item.S("code"))} ({item.S("code")})", item, "code")} · {data.Link($"qlvl {item.S("level", "0")}", item, "level")} · {range}"));
        }
        var baseLines = new List<string> { allowed, $"{bases.Count} base items" };
        baseLines.AddRange(bases.OrderBy(b => b.Qlvl).Take(40).Select(b => "  " + b.Line));
        if (bases.Count > 40) baseLines.Add($"  … {bases.Count - 40} more");
        if (bases.Count == 0 && itypes.Length > 0) issues.Add("No base item fits its types at any item level.");
        sections.Add(new("Fits", [.. baseLines]));
        if (affix.S("group") is { Length: > 0 } group)
        {
            var peers = rows.Where(r => r.S("group") == group && !ReferenceEquals(r, affix) && !JsonNode.DeepEquals(r, affix))
                .GroupBy(r => $"{Name(data, r)} ({Levels(r)})").Select(g => data.Link(g.Key, g.First(), "group")).ToArray();
            if (peers.Length > 0) sections.Add(new($"Group {group}: a rare never has two of these", [string.Join(", ", peers.Take(30)) + (peers.Length > 30 ? $" … +{peers.Length - 30}" : "")]));
        }
        return new(Name(data, affix), [.. lines], [.. sections], [], [], [.. issues.Distinct()]);
    }
}
