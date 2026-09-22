using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <param name="ItemLevel">Null picks one: the TC's own level, else the level of the first monster that names it.</param>
/// <param name="Filter">Only list drops whose name or code contains this.</param>
public sealed record DropPreviewOptions(int Players = 1, int Party = 1, int MagicFind = 0, int? ItemLevel = null, string Filter = "");

/// <summary>One base item a TC drops: how often per kill, and how often as each quality.</summary>
public sealed record DropLine(string Item, string Code, string PerKill, string Unique, string Set, string Rare, string Magic);

public sealed record DropPreviewResult(string Name, string[] Lines, PreviewSection[] Sections, DropLine[] Drops, int ItemLevel, string[] Issues);

/// <summary>
/// Worker-owned resolver for the Drop Preview: for a treasureclassex.txt row, what one roll of it drops and how often;
/// for an item row (uniqueitems, setitems, weapons, armor, misc), which monsters drop it and at what odds.
/// Reads authored data only; never changes records or starts a build.
/// </summary>
public sealed class DropPreviewResolver
{
    public static readonly string[] ItemTables = ["uniqueitems", "setitems", "weapons", "armor", "misc"];
    private const int DefaultItemLevel = 85;
    private const int Shown = 60;
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();

    public DropPreviewResult Resolve(ModProject project, string table, JsonObject record, string profile, string locale, CancellationToken token, DropPreviewOptions? options = null)
    {
        Require(table == "treasureclassex" || ItemTables.Contains(table), "Select a row in treasureclassex or an item table.");
        options ??= new();
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var row = data.Effective(table, record);
        var calc = new DropCalculator(data, token);
        var result = table == "treasureclassex" ? TreasureClass(calc, row, options, issues, token) : Sources(calc, table, row, options, issues, token);
        return result with { Issues = [.. result.Issues.Concat(calc.Issues).Distinct()] };
    }

    private static DropPreviewResult TreasureClass(DropCalculator calc, JsonObject row, DropPreviewOptions options, List<string> issues, CancellationToken token)
    {
        var name = row.S("Treasure Class");
        if (name.Length == 0) return new("Inactive row", ["Inactive/header row · no treasure class name"], [], [], 0, []);
        var tc = calc.Find(name)!;
        var monsters = new MonsterData(calc.Data);
        var users = monsters.AllSources(calc).Where(s => s.Authored == name || s.TreasureClass == name).ToArray();
        var (itemLevel, levelSource) = options.ItemLevel is { } set ? (set, "set above")
            : tc.Level > 0 ? (tc.Level, "the TC's level column")
            : users.FirstOrDefault() is { } user ? (user.Level, $"{user.Name} ({user.DifficultyName} {user.Kind})")
            : (DefaultItemLevel, "a default; nothing names this TC with a level");
        var settings = new DropSettings(options.Players, options.Party, options.MagicFind, itemLevel);

        var lines = new List<string>();
        int picks = tc.Picks == 0 ? 1 : tc.Picks;
        int noDrop = DropCalculator.AdjustedNoDrop(tc.NoDrop, tc.Total, settings.NoDropPlayers);
        lines.Add(picks > 0
            ? $"Picks {picks}: {(picks == 1 ? "one roll" : picks + " rolls")} among the entries" + (tc.NoDrop > 0 ? $" and NoDrop {tc.NoDrop}" + (noDrop != tc.NoDrop ? $" (→ {noDrop} with {settings.NoDropPlayers} counted players)" : "") : ", never nothing")
            : $"Picks {picks}: each entry drops Prob times, in order, until {-picks} items have dropped; NoDrop does not apply");
        if (tc.Picks == 0) issues.Add("Picks is empty; the preview treats it as 1.");
        if (tc.Group > 0 || tc.Level > 0) lines.Add($"Group {tc.Group} · level {tc.Level}: monsters above level {tc.Level} move up to a higher TC of group {tc.Group}");
        if (tc.Factors != default) lines.Add($"Quality factors {tc.Factors}; TCs below it inherit the higher of theirs and these");
        if (row.S("ConditionCalc") is { Length: > 0 } condition) lines.Add($"Drops only when: {DropCalculator.Unquote(condition)}");
        if (row.S("firstLadderSeason").Length > 0) lines.Add($"Ladder seasons {row.S("firstLadderSeason")}–{row.S("lastLadderSeason", "…")} only");
        lines.Add($"Item level {itemLevel} (from {levelSource}) · /players {options.Players}, party {options.Party} · {options.MagicFind}% magic find");

        var sections = new List<PreviewSection>();
        var entries = new List<string>();
        double weight = tc.Total + noDrop;
        foreach (var (entry, probability) in tc.Entries)
        {
            var kind = calc.Kind(entry);
            var what = kind switch
            {
                "tc" => "treasure class",
                "item" => calc.ItemName(entry.Split(',')[0]),
                "auto" => $"automatic TC of {calc.Automatic(entry.Split(',')[0])!.Length} items",
                "unique" => "unique item", "set" => "set item",
                _ => "⚠ not a TC, item code, unique or set"
            };
            var share = picks > 0 ? $"{Percent(probability / weight)} per pick" : $"drops up to {probability}×";
            entries.Add($"{entry} · prob {probability} · {share} · {what}");
        }
        if (picks > 0 && noDrop > 0) entries.Add($"NoDrop · {noDrop} · {Percent(noDrop / weight)} per pick");
        sections.Add(new("Entries", [.. entries]));

        var leaves = calc.Expand(name, settings);
        var drops = Lines(calc, leaves, settings, options.Filter, out var totals);
        lines.Add($"Per kill: {totals.Items:0.##} items" + (totals.Gold > 0 ? $" ({totals.Gold:0.##} gold piles)" : "") + $" · any unique {DropCalculator.Odds(totals.Unique)} · any set {DropCalculator.Odds(totals.Set)}");
        var named = calc.Named(leaves, settings).Select(n => (Key: n.Key, Chance: n.Value, Label: NamedLabel(calc, n.Key.Table, n.Key.Index)))
            .Where(n => Matches(options.Filter, n.Label)).OrderByDescending(n => n.Chance).ToArray();
        if (named.Length > 0)
            sections.Add(new($"Uniques and sets ({named.Length})", [.. named.Take(Shown).Select(n => $"{DropCalculator.Odds(n.Chance),-14} {n.Label}"), .. named.Length > Shown ? [$"… {named.Length - Shown} more; type part of a name above to find one"] : Array.Empty<string>()]));

        var usedBy = users.Select(u => $"{u.Name} · {u.DifficultyName} {u.Kind}" + (u.Authored != name ? $" (upgraded from {u.Authored} at level {u.Level})" : ""))
            .Concat(calc.Classes.Where(c => c.Entries.Any(e => e.Entry.Split(',')[0] == name)).Select(c => "treasure class " + c.Name)).Distinct().ToArray();
        sections.Add(new("Used by", usedBy.Length == 0 ? ["Nothing names this TC: no monster, superunique or other TC."] : [.. usedBy.Take(25), .. usedBy.Length > 25 ? [$"… {usedBy.Length - 25} more"] : Array.Empty<string>()]));
        sections.Add(new("Assumptions", [
            "Odds are the expected number per kill, shown as 1 in N; the game also caps how many items one kill drops.",
            "Quality odds use itemratio.txt at the item level with the magic find above; an item with no unique or set it can become turns rare or magic instead.",
            .. tc.Entries.Where(e => calc.Find(e.Entry) is { } sub && sub.Row.S("ConditionCalc").Length > 0).Select(e => $"{e.Entry} only drops when {calc.Find(e.Entry)!.Row.S("ConditionCalc")}; it is counted as if it always does.")]));
        return new(name, [.. lines], [.. sections], drops, itemLevel, [.. issues]);
    }

    private sealed record Totals(double Items, double Gold, double Unique, double Set);
    /// <summary>The per-kill drop table: one line per base item, most frequent first, with its quality odds.</summary>
    private static DropLine[] Lines(DropCalculator calc, Dictionary<DropLeaf, double> leaves, DropSettings settings, string filter, out Totals totals)
    {
        var byCode = new Dictionary<string, (double Count, double Unique, double Set, double Rare, double Magic, bool Quality)>();
        double items = 0, gold = 0, unique = 0, set = 0;
        foreach (var (leaf, count) in leaves)
        {
            if (leaf.Code == "gld") gold += count; else items += count;
            var quality = leaf.Forced.Length > 0 ? QualityChances.None : calc.Quality(leaf.Code, leaf.Factors, settings.ItemLevel, settings.MagicFind);
            var key = leaf.Forced.Length > 0 ? leaf.ForcedTable + ":" + leaf.Forced : leaf.Code;
            var current = byCode.GetValueOrDefault(key);
            byCode[key] = (current.Count + count, current.Unique + count * quality.Unique, current.Set + count * quality.Set, current.Rare + count * quality.Rare, current.Magic + count * quality.Magic, current.Quality || quality.Applies);
            if (leaf.Forced.Length > 0) { if (leaf.ForcedTable == "setitems") set += count; else unique += count; }
            else
            {
                // Only uniques and sets the item level allows count as a unique or set drop.
                if (calc.Choices("uniqueitems", leaf.Code, settings.ItemLevel).Count > 0) unique += count * quality.Unique;
                if (calc.Choices("setitems", leaf.Code, settings.ItemLevel).Count > 0) set += count * quality.Set;
            }
        }
        totals = new(items, gold, unique, set);
        string Odds(double value, bool applies) => applies ? DropCalculator.Odds(value) : "";
        return byCode.Select(x =>
            {
                var forced = x.Key.Split(':');
                var label = forced.Length == 2 ? NamedLabel(calc, forced[0], forced[1]) : $"{calc.ItemName(x.Key)} ({x.Key})";
                return (x.Value.Count, Line: new DropLine(label, x.Key, DropCalculator.Odds(x.Value.Count), Odds(x.Value.Unique, x.Value.Quality), Odds(x.Value.Set, x.Value.Quality), Odds(x.Value.Rare, x.Value.Quality), Odds(x.Value.Magic, x.Value.Quality)));
            })
            .Where(x => Matches(filter, x.Line.Item)).OrderByDescending(x => x.Count).Take(Shown).Select(x => x.Line).ToArray();
    }

    private static string NamedLabel(DropCalculator calc, string table, string index)
    {
        var row = table == "setitems" ? calc.SetItem(index) : calc.Unique(index);
        var code = row?.S(table == "setitems" ? "item" : "code") ?? "";
        return $"{calc.Localize(index)} ({(table == "setitems" ? "set" : "unique")} {calc.ItemName(code)})";
    }
    private static bool Matches(string filter, string text) => filter.Trim().Length == 0 || text.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Which monsters drop an item and how often, best first; kills with the same TC, level and odds are grouped.</summary>
    private static DropPreviewResult Sources(DropCalculator calc, string table, JsonObject row, DropPreviewOptions options, List<string> issues, CancellationToken token)
    {
        string code, index = "", name;
        var lines = new List<string>();
        switch (table)
        {
            case "uniqueitems" or "setitems":
                index = row.S("index"); code = row.S(table == "setitems" ? "item" : "code");
                if (index.Length == 0 || code.Length == 0) return new("Inactive row", ["Inactive/header row · no item"], [], [], 0, []);
                name = calc.Localize(index);
                int level = DropCalculator.Int(row, "lvl"), rarity = DropCalculator.Int(row, "rarity");
                lines.Add($"{(table == "setitems" ? "Set" : "Unique")} {calc.ItemName(code)} ({code}) · needs item level {level} · rarity {rarity}");
                if (Flag(row, "disabled") || row.S("spawnable", "1") == "0") issues.Add("This item is disabled or not spawnable, so no monster drops it.");
                if (rarity <= 0) issues.Add("rarity is 0, so a unique or set roll never picks this item.");
                if (calc.Item(code) is null) issues.Add($"Unresolved base item: {code}.");
                break;
            default:
                code = row.S("code"); name = calc.ItemName(code);
                if (code.Length == 0) return new("Inactive row", ["Inactive/header row · no item code"], [], [], 0, []);
                lines.Add($"Base item {code} · level {row.S("level", "?")} · rarity {row.S("rarity", "0")}" + (row.S("spawnable") == "1" ? "" : " · not spawnable, so only TCs that name it directly drop it"));
                break;
        }
        lines.Add($"/players {options.Players}, party {options.Party} · {options.MagicFind}% magic find · each kill at its own monster level");

        var monsters = new MonsterData(calc.Data);
        var targets = new HashSet<string>(StringComparer.Ordinal) { code };
        var found = new List<(DropSource Source, double Chance)>();
        foreach (var source in monsters.AllSources(calc))
        {
            token.ThrowIfCancellationRequested();
            if (calc.Find(source.TreasureClass) == null) continue;
            var settings = new DropSettings(options.Players, options.Party, options.MagicFind, source.Level);
            double chance = 0;
            foreach (var (leaf, count) in calc.Expand(source.TreasureClass, settings, codes: targets))
            {
                if (index.Length == 0) { chance += count; continue; }
                if (leaf.Forced.Length > 0) { if (leaf.Forced == index && leaf.ForcedTable == table) chance += count; continue; }
                var quality = calc.Quality(leaf.Code, leaf.Factors, source.Level, options.MagicFind);
                var share = calc.Choices(table, leaf.Code, source.Level).FirstOrDefault(c => c.Row.S("index") == index).Share;
                chance += count * (table == "setitems" ? quality.Set : quality.Unique) * share;
            }
            if (chance > 0) found.Add((source, chance));
        }
        var groups = found.GroupBy(f => (f.Source.TreasureClass, f.Source.Level, f.Source.Difficulty, f.Source.Kind, Odds: DropCalculator.Odds(f.Chance)))
            .Select(g => (g.Key, Chance: g.First().Chance, Names: g.Select(f => f.Source.Name).Distinct().ToArray()))
            .OrderByDescending(g => g.Chance).ToArray();
        var sourceLines = groups.Take(30).Select(g =>
            $"{g.Key.Odds,-14} {Difficulty(g.Key.Difficulty)} {g.Key.Kind} · {string.Join(", ", g.Names.Take(3))}{(g.Names.Length > 3 ? $" +{g.Names.Length - 3} more" : "")} · level {g.Key.Level} · {g.Key.TreasureClass}").ToList();
        if (groups.Length > 30) sourceLines.Add($"… {groups.Length - 30} more groups of monsters");
        var sections = new List<PreviewSection>
        {
            new("Drops from", sourceLines.Count > 0 ? [.. sourceLines] : ["No monster's treasure class reaches this item at its level."], BeforeLevels: true)
        };
        var listing = calc.Classes.Where(c => c.Entries.Any(e =>
        {
            var entry = e.Entry.Split(',')[0];
            return entry == code || entry == index || calc.Automatic(entry)?.Any(m => m.Code == code) == true;
        })).Select(c => c.Name).ToArray();
        sections.Add(new("Listed in", listing.Length == 0 ? ["No treasure class lists this item or an automatic TC that holds it."] : [.. listing.Take(20), .. listing.Length > 20 ? [$"… {listing.Length - 20} more"] : Array.Empty<string>()]));
        sections.Add(new("Assumptions", [
            "Monster levels: Normal uses monstats Level; Nightmare and Hell ordinary monsters use the highest level of the areas they spawn in; champions +2, uniques and superuniques +3.",
            "Terror zone (desecrated) and herald drops are not included.",
            "Odds are the expected number per kill, shown as 1 in N."]));
        return new(name, [.. lines], [.. sections], [], 0, [.. issues]);
    }
    private static string Difficulty(int difficulty) => MonsterData.Difficulties[difficulty].Name;
}
