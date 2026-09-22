using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public sealed record RecipePreviewResult(string Name, string[] Lines, PreviewSection[] Sections, string[] Issues);

/// <summary>One cube input or output: the item, type or special name before the first comma, then its modifiers.</summary>
public sealed record CubeItem(string Head, IReadOnlyDictionary<string, string> Modifiers, int Quantity)
{
    public static CubeItem Parse(string text)
    {
        var parts = DropCalculator.Unquote(text).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1)) { var pair = part.Split('=', 2); modifiers[pair[0]] = pair.Length > 1 ? pair[1] : ""; }
        int quantity = modifiers.Remove("qty", out var qty) && int.TryParse(qty, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
        return new(parts.FirstOrDefault() ?? "", modifiers, quantity);
    }
}

/// <summary>
/// Worker-owned resolver for the Recipe Preview: a cubemain.txt row read as a recipe (inputs, conditions, outputs, and
/// earlier recipes that take the same items first), or a runes.txt row read as a runeword (runes, the bases that can
/// hold it, its properties). Reads authored data only; never changes records or starts a build.
/// </summary>
public sealed class RecipePreviewResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();
    private static readonly Dictionary<string, string> InputWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = "low quality", ["nor"] = "normal", ["hiq"] = "superior", ["mag"] = "magic", ["set"] = "set", ["rar"] = "rare", ["uni"] = "unique",
        ["crf"] = "crafted", ["tmp"] = "tempered", ["nos"] = "no sockets", ["noe"] = "not ethereal", ["eth"] = "ethereal", ["upg"] = "can be upgraded",
        ["bas"] = "normal tier", ["exc"] = "exceptional", ["eli"] = "elite", ["nru"] = "not a runeword", ["id"] = "identified"
    };
    private static readonly Dictionary<string, string> OutputWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = "low quality", ["nor"] = "normal", ["hiq"] = "superior", ["mag"] = "magic", ["set"] = "set", ["rar"] = "rare", ["uni"] = "unique",
        ["crf"] = "crafted", ["tmp"] = "tempered", ["eth"] = "ethereal", ["mod"] = "keeps input 1's modifiers", ["uns"] = "destroys what is socketed",
        ["rem"] = "removes what is socketed", ["reg"] = "rerolls the unique", ["exc"] = "upgraded to exceptional", ["eli"] = "upgraded to elite",
        ["rep"] = "repaired", ["rch"] = "charges refilled"
    };

    public RecipePreviewResult Resolve(ModProject project, string table, JsonObject record, string profile, string locale, CancellationToken token)
    {
        Require(table is "cubemain" or "runes", "Select a row in cubemain or runes.");
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var row = data.Effective(table, record);
        var catalog = new ItemCatalog(data);
        return table == "cubemain" ? Cube(catalog, row, issues, token) : Runeword(catalog, row, issues, token);
    }

    /// <summary>What a cube input or output head names, or null when nothing does.</summary>
    private static string? Thing(ItemCatalog catalog, string head)
    {
        if (head.Equals("any", StringComparison.OrdinalIgnoreCase)) return "any item";
        if (catalog.Item(head) != null || catalog.HasType(head)) return catalog.Describe(head);
        var data = catalog.Data;
        if (data.Find("uniqueitems", "index", head, false) is { } unique) return $"unique {data.Localize(head, false)} ({catalog.ItemName(unique.S("code"))})";
        if (data.Find("setitems", "index", head, false) is { } set) return $"set item {data.Localize(head, false)} ({catalog.ItemName(set.S("item"))})";
        return null;
    }

    private static RecipePreviewResult Cube(ItemCatalog catalog, JsonObject recipe, List<string> issues, CancellationToken token)
    {
        var data = catalog.Data;
        var name = recipe.S("description") is { Length: > 0 } description ? description : "Cube recipe";
        var inputs = Enumerable.Range(1, 7).Select(i => recipe.S("input " + i)).Where(t => t.Trim().Length > 0).Select(CubeItem.Parse).ToArray();
        if (inputs.Length == 0 && recipe.S("output").Length == 0) return new("Inactive row", ["Inactive/header row · no inputs or output"], [], []);
        var lines = new List<string>();
        lines.Add(string.Join(" · ", new[]
        {
            recipe.S("enabled") == "1" ? "Enabled" : "Disabled",
            recipe.S("version") is "100" ? "expansion" : "all versions",
            recipe.S("min diff") switch { "1" => "Nightmare and Hell only", "2" => "Hell only", _ => "" },
            recipe.S("class") is { Length: > 0 } cls ? cls + " only" : "",
            recipe.S("firstLadderSeason").Length > 0 ? $"ladder seasons {recipe.S("firstLadderSeason")}–{recipe.S("lastLadderSeason", "…")}" : ""
        }.Where(s => s.Length > 0)));
        if (recipe.S("enabled") != "1") issues.Add("enabled is not 1, so the cube never uses this recipe.");
        if (recipe.S("op") is { Length: > 0 } op && op != "0")
        {
            var function = FunctionGuide.Describe("cubemain", "op", op);
            string parameter = recipe.S("param"), value = recipe.S("value");
            // Stat ops take the stat's row number in itemstatcost.txt as their param.
            var stats = data.Rows("itemstatcost", false);
            var stat = int.TryParse(parameter, out var index) && index >= 0 && index < stats.Count ? stats[index].S("Stat") : "";
            lines.Add($"Only when (op {op}): " + (function != null ? function.Summary.Replace("\n", " ") : "not in the data guide") +
                (parameter.Length > 0 ? $" · param {parameter}" + (stat.Length > 0 && int.Parse(op, CultureInfo.InvariantCulture) is >= 3 and <= 26 ? $" = {stat}" : "") : "") + (value.Length > 0 ? $" · value {value}" : ""));
        }

        var sections = new List<PreviewSection>();
        var inputLines = new List<string>();
        foreach (var input in inputs)
        {
            var what = Thing(catalog, input.Head);
            if (what == null) issues.Add($"Unresolved cube input: {input.Head}.");
            inputLines.Add($"{input.Quantity} × {what ?? input.Head + " (unknown)"}" + Modifiers(input, InputWords, out var unknown));
            foreach (var word in unknown) issues.Add($"Unknown input modifier '{word}' on {input.Head}.");
        }
        int total = inputs.Sum(i => i.Quantity), declared = DropCalculator.Int(recipe, "numinputs");
        inputLines.Add($"{total} item{(total == 1 ? "" : "s")} in the cube" + (declared > 0 && declared != total ? $" · ⚠ numinputs says {declared}" : ""));
        if (declared > 0 && declared != total) issues.Add($"numinputs is {declared} but the inputs add up to {total} items, so the recipe never matches.");
        sections.Add(new("Inputs", [.. inputLines]));

        foreach (var (prefix, title) in new[] { ("", "Output"), ("b ", "Output B"), ("c ", "Output C") })
        {
            var column = prefix.Length == 0 ? "output" : "output " + prefix.Trim();
            if (recipe.S(column).Trim().Length == 0) continue;
            var output = CubeItem.Parse(recipe.S(column));
            var what = output.Head switch
            {
                "useitem" => "the input 1 item itself",
                "usetype" => "a new item of input 1's type",
                "Cow Portal" => "a portal to the Moo Moo Farm",
                "Pandemonium Portal" => "one of the three Pandemonium portals",
                "Pandemonium Finale Portal" => "the portal to Uber Tristram",
                "Red Portal" => "a red portal to level " + output.Modifiers.GetValueOrDefault("lvl", "?"),
                _ => Thing(catalog, output.Head)
            };
            if (what == null) issues.Add($"Unresolved cube output: {output.Head}.");
            var outputLines = new List<string> { $"{output.Quantity} × {what ?? output.Head + " (unknown)"}" + Modifiers(output, OutputWords, out var unknown) };
            foreach (var word in unknown) issues.Add($"Unknown output modifier '{word}' on {output.Head}.");
            foreach (var (key, table) in new[] { ("pre", "magicprefix"), ("suf", "magicsuffix") })
                if (output.Modifiers.TryGetValue(key, out var number))
                {
                    var rows = data.Rows(table, false);
                    if (int.TryParse(number, out var id) && id >= 0 && id < rows.Count)
                        outputLines.Add($"  {(key == "pre" ? "prefix" : "suffix")} #{id}: {AffixPreviewResolver.AffixName(data, rows[id])} — " +
                            string.Join(" · ", Enumerable.Range(1, 3).Where(i => rows[id].S($"mod{i}code").Length > 0).Select(i => PropertyText.Describe(data, rows[id].S($"mod{i}code"), rows[id].S($"mod{i}param"), rows[id].S($"mod{i}min"), rows[id].S($"mod{i}max"), issues))));
                    else issues.Add($"{key}={number} is not a row of {table}.");
                }
            if (recipe.S(prefix + "lvl") is { Length: > 0 } fixedLevel) outputLines.Add($"  item level {fixedLevel} (fixed)");
            else if (recipe.S(prefix + "plvl").Length > 0 || recipe.S(prefix + "ilvl").Length > 0)
                outputLines.Add($"  item level = {recipe.S(prefix + "plvl", "0")}% of the player's level + {recipe.S(prefix + "ilvl", "0")}% of input 1's item level");
            for (int i = 1; i <= 5; i++)
                if (recipe.S($"{prefix}mod {i}") is { Length: > 0 } mod)
                {
                    var chance = recipe.S($"{prefix}mod {i} chance");
                    outputLines.Add("  + " + PropertyText.Describe(data, mod, recipe.S($"{prefix}mod {i} param"), recipe.S($"{prefix}mod {i} min"), recipe.S($"{prefix}mod {i} max"), issues)
                        + (chance is "" or "0" ? "" : $" ({chance}% chance)"));
                }
            sections.Add(new(title, [.. outputLines]));
        }

        // The cube takes the first recipe whose inputs match, so an earlier recipe that accepts these items wins.
        var all = data.Rows("cubemain", false);
        int self = -1;
        for (int i = 0; i < all.Count; i++) if (JsonNode.DeepEquals(all[i], recipe)) { self = i; break; }
        var shadows = new List<string>(); var hidden = new List<string>();
        for (int i = 0; i < all.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i == self || all[i].S("enabled") != "1") continue;
            if (i < self && Shadows(catalog, all[i], recipe)) shadows.Add($"row {i}: {all[i].S("description", "(no description)")}");
            if (i > self && recipe.S("enabled") == "1" && Shadows(catalog, recipe, all[i])) hidden.Add($"row {i}: {all[i].S("description", "(no description)")}");
        }
        if (shadows.Count > 0) issues.Add($"An earlier recipe accepts the same items, so this one never runs: {shadows[0]}.");
        if (shadows.Count > 0) sections.Add(new("Taken first by", [.. shadows.Take(10)]));
        if (hidden.Count > 0) sections.Add(new("Takes items first from", [.. hidden.Take(10), .. hidden.Count > 10 ? [$"… {hidden.Count - 10} more"] : Array.Empty<string>()]));
        return new(name, [.. lines], [.. sections], [.. issues.Distinct()]);
    }

    private static string Modifiers(CubeItem item, Dictionary<string, string> words, out List<string> unknown)
    {
        var parts = new List<string>(); unknown = [];
        foreach (var (key, value) in item.Modifiers)
        {
            if (key.Equals("sock", StringComparison.OrdinalIgnoreCase)) parts.Add(value.Length > 0 ? $"{value} sockets" : "sockets");
            else if (key is "pre" or "suf" or "lvl") continue;
            else if (words.TryGetValue(key, out var word)) parts.Add(word);
            else unknown.Add(key);
        }
        return parts.Count > 0 ? " · " + string.Join(", ", parts) : "";
    }

    /// <summary>
    /// Whether recipe <paramref name="first"/> accepts every item combination <paramref name="later"/> needs: no stricter
    /// condition, class or difficulty, the same number of items, and each item of the later recipe matched by an item of
    /// the first whose name covers it (the same item, a type it belongs to, or any) and whose modifiers it also has.
    /// </summary>
    public static bool Shadows(ItemCatalog catalog, JsonObject first, JsonObject later)
    {
        if (first.S("op") is { Length: > 0 } op && op != "0" && (op != later.S("op") || first.S("param") != later.S("param") || first.S("value") != later.S("value"))) return false;
        if (first.S("class").Length > 0 && first.S("class") != later.S("class")) return false;
        if (DropCalculator.Int(first, "min diff") > DropCalculator.Int(later, "min diff")) return false;
        static List<CubeItem> Expand(JsonObject recipe) => Enumerable.Range(1, 7).Select(i => recipe.S("input " + i)).Where(t => t.Trim().Length > 0)
            .Select(CubeItem.Parse).SelectMany(i => Enumerable.Repeat(i, i.Quantity)).ToList();
        var a = Expand(first); var b = Expand(later);
        if (a.Count == 0 || a.Count != b.Count) return false;
        bool Covers(CubeItem wide, CubeItem narrow)
        {
            if (!wide.Modifiers.All(m => narrow.Modifiers.TryGetValue(m.Key, out var v) && v == m.Value)) return false;
            if (wide.Head == narrow.Head || wide.Head.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;
            // An item code names that item only, even when an item type shares the code ("axe" is both).
            if (catalog.Item(wide.Head) != null || !catalog.HasType(wide.Head)) return false;
            var code = catalog.Data.Find("uniqueitems", "index", narrow.Head, false)?.S("code") ?? narrow.Head;
            return catalog.Item(code) is { } item ? catalog.ItemIs(item.Row, wide.Head) : catalog.HasType(narrow.Head) && catalog.IsType(narrow.Head, wide.Head);
        }
        // Small bipartite match by backtracking: at most seven items on each side.
        var used = new bool[a.Count];
        bool Match(int index)
        {
            if (index == b.Count) return true;
            for (int i = 0; i < a.Count; i++)
                if (!used[i] && Covers(a[i], b[index])) { used[i] = true; if (Match(index + 1)) return true; used[i] = false; }
            return false;
        }
        return Match(0);
    }

    private static RecipePreviewResult Runeword(ItemCatalog catalog, JsonObject runeword, List<string> issues, CancellationToken token)
    {
        var data = catalog.Data;
        var key = runeword.S("Name");
        if (key.Length == 0) return new("Inactive row", ["Inactive/header row · no runeword name"], [], []);
        var name = data.Localize(key, false) is var localized && localized != key ? localized : runeword.S("*Rune Name") is { Length: > 0 } comment ? comment : key;
        var runes = Enumerable.Range(1, 6).Select(i => runeword.S("Rune" + i)).Where(r => r.Length > 0).ToArray();
        if (runes.Length == 0 && runeword.S("complete") != "1") return new(name, ["Placeholder row · no runes and not complete"], [], []);
        var lines = new List<string>();
        foreach (var rune in runes) if (catalog.Item(rune) == null) issues.Add($"Unresolved rune: {rune}.");
        int level = runes.Select(r => catalog.Item(r)?.Row is { } row ? DropCalculator.Int(row, "levelreq") : 0).DefaultIfEmpty(0).Max();
        lines.Add($"{string.Join(" + ", runes.Select(r => catalog.Item(r) != null ? catalog.ItemName(r) : r))} · {runes.Length} socket{(runes.Length == 1 ? "" : "s")} · required level {level} from its runes");
        lines.Add(string.Join(" · ", new[]
        {
            runeword.S("complete") == "1" ? "Can be made" : "",
            runeword.S("firstLadderSeason").Length > 0 ? $"ladder seasons {runeword.S("firstLadderSeason")}–{runeword.S("lastLadderSeason", "…")}" : "",
            Flag(runeword, "disallowCraftingInLadder") ? "not in ladder games" : "", Flag(runeword, "disallowCraftingInNonLadder") ? "not in non-ladder or offline games" : ""
        }.Where(s => s.Length > 0)));
        if (runeword.S("complete") != "1") issues.Add("complete is not 1, so this runeword cannot be made.");
        if (runes.Length == 0) issues.Add("No Rune# is set.");

        var itypes = Enumerable.Range(1, 6).Select(i => runeword.S("itype" + i)).Where(t => t.Length > 0).ToArray();
        var etypes = Enumerable.Range(1, 3).Select(i => runeword.S("etype" + i)).Where(t => t.Length > 0).ToArray();
        foreach (var type in itypes.Concat(etypes)) if (!catalog.HasType(type)) issues.Add($"Unresolved itemtypes/Code: {type}.");
        bool Allowed(JsonObject item, string[] allowed, string[] excluded) => allowed.Any(t => catalog.ItemIs(item, t)) && !excluded.Any(t => catalog.ItemIs(item, t));
        var fits = new List<(int Qlvl, string Line)>(); var tooFew = new List<string>();
        foreach (var (table, item) in catalog.Items)
        {
            token.ThrowIfCancellationRequested();
            if (table == "misc" || !Allowed(item, itypes, etypes)) continue;
            var from = Enumerable.Range(1, 99).FirstOrDefault(l => catalog.MaxSockets(item, l) >= runes.Length);
            var label = $"{catalog.ItemName(item.S("code"))} ({item.S("code")})";
            if (from == 0) { tooFew.Add($"{label} (max {catalog.MaxSockets(item, 99)})"); continue; }
            fits.Add((DropCalculator.Int(item, "level"), $"{label} · qlvl {item.S("level", "0")} · {runes.Length} sockets from item level {from}"));
        }
        var baseLines = new List<string> { "On " + string.Join(", ", itypes.Select(t => $"{catalog.TypeName(t)} ({t})")) + (etypes.Length > 0 ? "; not on " + string.Join(", ", etypes.Select(t => $"{catalog.TypeName(t)} ({t})")) : ""), $"{fits.Count} base items can hold it" };
        baseLines.AddRange(fits.OrderBy(f => f.Qlvl).Take(40).Select(f => "  " + f.Line));
        if (fits.Count > 40) baseLines.Add($"  … {fits.Count - 40} more");
        if (tooFew.Count > 0) baseLines.Add($"Never enough sockets: {string.Join(", ", tooFew.Take(12))}{(tooFew.Count > 12 ? $" +{tooFew.Count - 12} more" : "")}");
        if (fits.Count == 0 && runes.Length > 0) issues.Add("No base item of its types can have that many sockets, so it can never be made.");
        var sections = new List<PreviewSection>
        {
            new("Properties", [.. Enumerable.Range(1, 7).Where(i => runeword.S($"T1Code{i}").Length > 0)
                .Select(i => PropertyText.Describe(data, runeword.S($"T1Code{i}"), runeword.S($"T1Param{i}"), runeword.S($"T1Min{i}"), runeword.S($"T1Max{i}"), issues))]),
            new("Bases", [.. baseLines])
        };
        // The same runes in the same order on a shared base make only one of the runewords.
        var sequence = string.Join(",", runes);
        foreach (var other in data.Rows("runes", false))
        {
            if (other.S("Name") == key || other.S("complete") != "1" || runeword.S("complete") != "1") continue;
            if (string.Join(",", Enumerable.Range(1, 6).Select(i => other.S("Rune" + i)).Where(r => r.Length > 0)) != sequence) continue;
            var otherTypes = Enumerable.Range(1, 6).Select(i => other.S("itype" + i)).Where(t => t.Length > 0).ToArray();
            var otherExcluded = Enumerable.Range(1, 3).Select(i => other.S("etype" + i)).Where(t => t.Length > 0).ToArray();
            var shared = catalog.Items.Where(x => x.Table != "misc" && Allowed(x.Row, itypes, etypes) && Allowed(x.Row, otherTypes, otherExcluded)).Select(x => catalog.ItemName(x.Row.S("code"))).Take(3).ToArray();
            if (shared.Length > 0) issues.Add($"{data.Localize(other.S("Name"), false)} uses the same runes in the same order on shared bases ({string.Join(", ", shared)}…); only one of them can be made there.");
        }
        return new(name, [.. lines.Where(l => l.Length > 0)], [.. sections], [.. issues.Distinct()]);
    }
}
