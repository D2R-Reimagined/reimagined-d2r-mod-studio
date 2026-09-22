using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// Who is in the game and how the drop is rolled. <paramref name="Players"/> is the game's player count (<c>/players</c>
/// in single player) and <paramref name="Party"/> how many of them are in the killer's party and nearby: together they
/// decide how much NoDrop shrinks. <paramref name="ItemLevel"/> is the monster level, which gates uniques and sets.
/// </summary>
public sealed record DropSettings(int Players = 1, int Party = 1, int MagicFind = 0, int ItemLevel = 85)
{
    /// <summary>Party members nearby count fully, the other players in the game count half, rounded down.</summary>
    public int NoDropPlayers => Math.Clamp(Party, 1, 8) + Math.Max(0, Math.Clamp(Players, 1, 8) - Math.Clamp(Party, 1, 8)) / 2;
}

/// <summary>The Unique/Set/Rare/Magic quality factors a drop carries down its TC chain; each is the highest seen on the way.</summary>
public readonly record struct QualityFactors(int Unique, int Set, int Rare, int Magic)
{
    public QualityFactors Max(QualityFactors other) => new(Math.Max(Unique, other.Unique), Math.Max(Set, other.Set), Math.Max(Rare, other.Rare), Math.Max(Magic, other.Magic));
    public override string ToString() => $"unique {Unique} · set {Set} · rare {Rare} · magic {Magic}";
}

/// <summary>
/// One thing a kill can drop: a base item code, or a named unique or set item when a TC lists it directly
/// (<paramref name="Forced"/> is its index), with the quality factors it arrived with.
/// </summary>
public readonly record struct DropLeaf(string Code, string Forced, string ForcedTable, QualityFactors Factors, string Modifier = "");

/// <summary>Chances that one dropped base item rolls each quality, in the game's order: unique, set, rare, magic.</summary>
public sealed record QualityChances(double Unique, double Set, double Rare, double Magic, bool Applies)
{
    public static readonly QualityChances None = new(0, 0, 0, 0, false);
}

/// <summary>A treasureclassex.txt row as the calculator reads it.</summary>
public sealed record TreasureClass(string Name, int Group, int Level, int Picks, int NoDrop, QualityFactors Factors, (string Entry, int Probability)[] Entries, JsonObject Row)
{
    public int Total => Entries.Sum(e => e.Probability);
}

/// <summary>
/// Treasure class drop calculator over the preview tables. Expands a TC into the expected number of each item one roll
/// of it drops, the way the game picks: positive Picks roll that many times among the entries and NoDrop (NoDrop shrinks
/// with the player count); negative Picks drop each entry Prob times, in order, until the picks are used up. Entries are
/// other TCs, item codes, automatic TCs such as <c>weap75</c> (every spawnable weapon of levels 73–75, weighted by
/// rarity), or unique and set items named directly. Quality odds follow itemratio.txt with magic find and each TC's
/// quality factors. Worker-owned; reads authored data only.
/// </summary>
public sealed class DropCalculator
{
    private readonly PreviewTables.Session data;
    private readonly CancellationToken token;
    private readonly Dictionary<string, TreasureClass> classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Table, JsonObject Row)> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> uniquesByIndex = new(StringComparer.Ordinal), setsByIndex = new(StringComparer.Ordinal);
    private readonly ILookup<string, JsonObject> uniquesByCode, setsByCode;
    private readonly Dictionary<string, (string Code, int Weight)[]?> automatic = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, QualityFactors, int, string), Dictionary<DropLeaf, double>> memo = [];
    private readonly HashSet<string> expanding = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<JsonObject> ratios;
    public List<string> Issues { get; } = [];
    public PreviewTables.Session Data => data;

    public DropCalculator(PreviewTables.Session data, CancellationToken token)
    {
        this.data = data; this.token = token;
        foreach (var row in data.Rows("treasureclassex", false))
        {
            var name = row.S("Treasure Class");
            if (name.Length == 0) continue;
            // Spreadsheet exports quote entries with a comma ("gld,mul=2048"); the game reads the text inside.
            var entries = Enumerable.Range(1, 10).Select(i => (Entry: Unquote(row.S("Item" + i)), Probability: Int(row, "Prob" + i)))
                .Where(e => e.Entry.Length > 0 && e.Probability > 0).ToArray();
            classes.TryAdd(name, new(name, Int(row, "group"), Int(row, "level"), Int(row, "Picks"), Int(row, "NoDrop"),
                new(Int(row, "Unique"), Int(row, "Set"), Int(row, "Rare"), Int(row, "Magic")), entries, row));
        }
        foreach (var table in new[] { "weapons", "armor", "misc" })
            foreach (var row in data.Rows(table, false)) if (row.S("code") is { Length: > 0 } code) items.TryAdd(code, (table, row));
        foreach (var row in data.Rows("itemtypes", false)) if (row.S("Code") is { Length: > 0 } code) types.TryAdd(code, row);
        var uniques = data.Rows("uniqueitems", false).Where(r => r.S("code").Length > 0).ToArray();
        var sets = data.Rows("setitems", false).Where(r => r.S("item").Length > 0).ToArray();
        foreach (var row in uniques) uniquesByIndex.TryAdd(row.S("index"), row);
        foreach (var row in sets) setsByIndex.TryAdd(row.S("index"), row);
        uniquesByCode = uniques.ToLookup(r => r.S("code"), StringComparer.Ordinal);
        setsByCode = sets.ToLookup(r => r.S("item"), StringComparer.Ordinal);
        ratios = data.Rows("itemratio", false);
    }

    public TreasureClass? Find(string name) => classes.GetValueOrDefault(name);
    public IEnumerable<TreasureClass> Classes => classes.Values;
    public (string Table, JsonObject Row)? Item(string code) => items.TryGetValue(code, out var item) ? item : null;
    public JsonObject? Unique(string index) => uniquesByIndex.GetValueOrDefault(index);
    public JsonObject? SetItem(string index) => setsByIndex.GetValueOrDefault(index);

    /// <summary>NoDrop after the player count: the chance of rolling nothing is raised to the power of the counted players.</summary>
    public static int AdjustedNoDrop(int noDrop, int total, int players)
    {
        if (noDrop <= 0 || total <= 0 || players <= 1) return Math.Max(noDrop, 0);
        double single = (double)noDrop / (noDrop + total), all = Math.Pow(single, players);
        return (int)Math.Floor(total / (1 / all - 1));
    }

    /// <summary>
    /// Monsters above a TC's level use the highest TC of the same group whose level they reach: an "Act 1 H2H A" monster
    /// at level 85 drops from its group's level-85 TC instead.
    /// </summary>
    public string Upgrade(string name, int monsterLevel)
    {
        if (upgrades.TryGetValue((name, monsterLevel), out var cached)) return cached;
        if (Find(name) is not { Group: > 0 } tc) return upgrades[(name, monsterLevel)] = name;
        return upgrades[(name, monsterLevel)] = classes.Values.Where(c => c.Group == tc.Group && c.Level <= monsterLevel && c.Level >= tc.Level)
            .OrderByDescending(c => c.Level).Select(c => c.Name).FirstOrDefault() ?? name;
    }
    private readonly Dictionary<(string, int), string> upgrades = [];

    /// <summary>What an entry names: a TC, an item code, an automatic TC, a unique or set item by index, or nothing.</summary>
    public string Kind(string entry)
    {
        var name = entry.Split(',')[0].Trim();
        if (classes.ContainsKey(name)) return "tc";
        if (items.ContainsKey(name)) return "item";
        if (Automatic(name) != null) return "auto";
        if (uniquesByIndex.ContainsKey(name)) return "unique";
        if (setsByIndex.ContainsKey(name)) return "set";
        return "missing";
    }

    /// <summary>
    /// Expected count of each leaf one roll of a TC drops. <paramref name="codes"/> restricts the count to those base codes
    /// (a reverse lookup needs only its target, which keeps it fast).
    /// </summary>
    public Dictionary<DropLeaf, double> Expand(string name, DropSettings settings, QualityFactors factors = default, IReadOnlySet<string>? codes = null)
    {
        int players = settings.NoDropPlayers;
        var key = (name, factors, players, codes == null ? "" : string.Join(",", codes.Order(StringComparer.Ordinal)));
        if (memo.TryGetValue(key, out var cached)) return cached;
        token.ThrowIfCancellationRequested();
        var result = new Dictionary<DropLeaf, double>();
        if (Find(name) is not { } tc) { Issues.Add($"Unresolved treasure class: {name}."); return result; }
        if (!expanding.Add(name)) { Issues.Add($"Treasure class {name} reaches itself; the loop is cut there."); return result; }
        try
        {
            var carried = factors.Max(tc.Factors);
            void Add(Dictionary<DropLeaf, double> from, double scale) { foreach (var (leaf, count) in from) result[leaf] = result.GetValueOrDefault(leaf) + count * scale; }
            int picks = tc.Picks == 0 ? 1 : tc.Picks;
            if (picks > 0)
            {
                double weight = tc.Total + AdjustedNoDrop(tc.NoDrop, tc.Total, players);
                if (weight > 0) foreach (var (entry, probability) in tc.Entries) Add(Entry(entry, settings, carried, codes), picks * probability / weight);
            }
            else
            {
                int remaining = -picks;
                foreach (var (entry, probability) in tc.Entries)
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(probability, remaining); remaining -= take;
                    Add(Entry(entry, settings, carried, codes), take);
                }
            }
        }
        finally { expanding.Remove(name); }
        return memo[key] = result;
    }

    private Dictionary<DropLeaf, double> Entry(string entry, DropSettings settings, QualityFactors factors, IReadOnlySet<string>? codes)
    {
        var parts = entry.Split(','); var name = parts[0].Trim(); var modifier = string.Join(",", parts.Skip(1)).Trim();
        if (classes.ContainsKey(name)) return Expand(name, settings, factors, codes);
        Dictionary<DropLeaf, double> Leaf(string code, string forced = "", string table = "") =>
            codes != null && !codes.Contains(code) ? [] : new() { [new(code, forced, table, factors, modifier)] = 1 };
        if (items.ContainsKey(name)) return Leaf(name);
        if (Automatic(name) is { } members)
        {
            double total = members.Sum(m => m.Weight);
            var result = new Dictionary<DropLeaf, double>();
            foreach (var (code, weight) in members) if (codes == null || codes.Contains(code)) result[new(code, "", "", factors, modifier)] = weight / total;
            return result;
        }
        if (uniquesByIndex.TryGetValue(name, out var unique)) return Leaf(unique.S("code"), name, "uniqueitems");
        if (setsByIndex.TryGetValue(name, out var set)) return Leaf(set.S("item"), name, "setitems");
        Issues.Add($"Unresolved treasure class entry: {name}.");
        return [];
    }

    /// <summary>
    /// The members of an automatic TC: <c>weap75</c> is every spawnable item whose type is (or inherits) an item type with
    /// TreasureClass = 1 coded <c>weap</c>, with a level from 73 to 75, weighted by its rarity.
    /// </summary>
    public (string Code, int Weight)[]? Automatic(string name)
    {
        if (automatic.TryGetValue(name, out var cached)) return cached;
        var match = Regex.Match(name, "^([a-z]+)([0-9]+)$");
        (string, int)[]? members = null;
        if (match.Success && types.TryGetValue(match.Groups[1].Value, out var type) && Flag(type, "TreasureClass"))
        {
            int top = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            members = items.Values
                .Where(i => i.Row.S("spawnable") == "1" && Int(i.Row, "rarity") > 0 && Int(i.Row, "level") is var level && level > top - 3 && level <= top
                    && (IsType(i.Row.S("type"), match.Groups[1].Value) || IsType(i.Row.S("type2"), match.Groups[1].Value)))
                .Select(i => (i.Row.S("code"), Int(i.Row, "rarity"))).ToArray();
        }
        return automatic[name] = members;
    }

    /// <summary>Whether an item type is, or inherits through Equiv1/Equiv2, another type.</summary>
    public bool IsType(string type, string ancestor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(type);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.Length == 0 || !seen.Add(current)) continue;
            if (current == ancestor) return true;
            if (types.TryGetValue(current, out var row)) { pending.Push(row.S("Equiv1")); pending.Push(row.S("Equiv2")); }
        }
        return false;
    }

    /// <summary>
    /// Quality odds for one dropped base item: for each quality in turn, (ratio − (item level − qlvl) ÷ divisor) × 128,
    /// divided by magic find (with diminishing returns for unique, set and rare), raised to the itemratio minimum, lowered
    /// by the TC quality factor ÷ 1024; the roll succeeds 128 times in that many.
    /// </summary>
    public QualityChances Quality(string code, QualityFactors factors, int itemLevel, int magicFind)
    {
        if (Item(code) is not { } item) return QualityChances.None;
        // Only equipment, and jewelry or charms that have uniques or sets, roll qualities worth showing.
        bool equipment = item.Table is "weapons" or "armor";
        if (!equipment && !uniquesByCode[code].Any() && !setsByCode[code].Any()) return QualityChances.None;
        var row = item.Row;
        bool uber = row.S("normcode").Length > 0 && row.S("normcode") != code;
        bool classSpecific = Types(row.S("type")).Any(t => types.TryGetValue(t, out var r) && r.S("Class").Length > 0);
        var ratio = ratios.FirstOrDefault(r => r.S("Version") == "1" && Flag(r, "Uber") == uber && Flag(r, "Class Specific") == classSpecific)
            ?? ratios.FirstOrDefault(r => r.S("Version") == "1") ?? ratios.FirstOrDefault();
        if (ratio == null) return QualityChances.None;
        int qlvl = Int(row, "level");
        double Chance(string column, int factor, int mf)
        {
            long chance = (Int(ratio, column) - (itemLevel - qlvl) / Math.Max(1, Int(ratio, column + "Divisor"))) * 128L;
            chance = chance * 100 / (100 + mf);
            chance = Math.Max(chance, Int(ratio, column + "Min"));
            chance -= chance * factor / 1024;
            return chance <= 128 ? 1 : 128.0 / chance;
        }
        int Diminish(int limit) => magicFind * limit / (magicFind + limit);
        double unique = Chance("Unique", factors.Unique, Diminish(250)), set = Chance("Set", factors.Set, Diminish(500));
        double rare = Chance("Rare", factors.Rare, Diminish(600)), magic = Chance("Magic", factors.Magic, magicFind);
        double left = 1;
        double Take(double p) { var taken = left * p; left -= taken; return taken; }
        return new(Take(unique), Take(set), Take(rare), Take(magic), true);
    }
    private IEnumerable<string> Types(string type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(type);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.Length == 0 || !seen.Add(current)) continue;
            yield return current;
            if (types.TryGetValue(current, out var row)) { pending.Push(row.S("Equiv1")); pending.Push(row.S("Equiv2")); }
        }
    }

    /// <summary>
    /// The uniques (or sets) a unique (or set) roll of a base item picks from at an item level, with each one's share:
    /// enabled, spawnable rows of that code whose lvl the item level reaches, weighted by rarity.
    /// </summary>
    public IReadOnlyList<(JsonObject Row, double Share)> Choices(string table, string code, int itemLevel)
    {
        var rows = (table == "setitems" ? setsByCode[code] : uniquesByCode[code])
            .Where(r => !Flag(r, "disabled") && r.S("spawnable", "1") != "0" && Int(r, "lvl") <= itemLevel && Int(r, "rarity") > 0).ToArray();
        double total = rows.Sum(r => (double)Int(r, "rarity"));
        return total <= 0 ? [] : rows.Select(r => (r, Int(r, "rarity") / total)).ToArray();
    }

    /// <summary>How likely each named unique and set item is from the leaves of an expansion, per roll of the TC.</summary>
    public Dictionary<(string Table, string Index), double> Named(Dictionary<DropLeaf, double> leaves, DropSettings settings)
    {
        var named = new Dictionary<(string, string), double>();
        foreach (var (leaf, count) in leaves)
        {
            if (leaf.Forced.Length > 0) { named[(leaf.ForcedTable, leaf.Forced)] = named.GetValueOrDefault((leaf.ForcedTable, leaf.Forced)) + count; continue; }
            var quality = Quality(leaf.Code, leaf.Factors, settings.ItemLevel, settings.MagicFind);
            if (!quality.Applies) continue;
            foreach (var (table, chance) in new[] { ("uniqueitems", quality.Unique), ("setitems", quality.Set) })
                foreach (var (row, share) in Choices(table, leaf.Code, settings.ItemLevel))
                    named[(table, row.S("index"))] = named.GetValueOrDefault((table, row.S("index"))) + count * chance * share;
        }
        return named;
    }

    /// <summary>Localized base item name, falling back to its code.</summary>
    public string ItemName(string code) => Item(code) is { } item && item.Row.S("namestr") is { Length: > 0 } key ? data.Localize(key, false) : code;
    public string Localize(string key) => key.Length == 0 ? "" : data.Localize(key, false);

    /// <summary>"1 in 1,234" for a per-kill expectation below one, "×2.4" above it.</summary>
    public static string Odds(double expected) => expected <= 0 ? "—"
        : expected >= 0.9995 ? "×" + expected.ToString("0.##", CultureInfo.InvariantCulture)
        : "1 in " + Math.Round(1 / expected).ToString("N0", CultureInfo.InvariantCulture);

    internal static string Unquote(string text)
    {
        text = text.Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1].Replace("\"\"", "\"").Trim() : text;
    }
    internal static int Int(JsonObject row, string column)
    {
        var text = row.S(column).Trim();
        if (text.Length == 0) return 0;
        Require(int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value), $"{column} is not a whole number: {text}.");
        return value;
    }
}
