using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>Sample values to show a stat with. Null takes the first authored use, or 10 to 20 when nothing uses it.</summary>
public sealed record StatPreviewOptions(decimal? Min = null, decimal? Max = null, string Parameter = "", int CharacterLevel = CalcAssumptions.DefaultCharacterLevel);

/// <param name="Sample">The min/max/parameter the tooltip lines were rendered with, and where they came from.</param>
public sealed record StatPreviewResult(string Name, string[] Lines, PreviewSection[] Sections, string Sample, string[] Issues);

/// <summary>One place a property code is authored: an item, affix, rune, set bonus, gem or cube output slot.</summary>
public sealed record PropertyUse(string Table, string Owner, string Slot, string Code, string Parameter, string Min, string Max)
{
    public string Values => Parameter.Length > 0 ? $"param {Parameter}" + (Min.Length > 0 || Max.Length > 0 ? $", {Range}" : "") : Range;
    private string Range => Min == Max || Max.Length == 0 ? Min : Min.Length == 0 ? Max : $"{Min}–{Max}";
    public override string ToString() => $"{Table} · {Owner} · {Slot} {Code} {Values}".TrimEnd();
}

/// <summary>
/// Worker-owned resolver that walks the stat pipeline for a properties.txt or itemstatcost.txt row: which functions a
/// property runs, which stats they write, how each stat is modified (<c>op</c>), saved (<c>Save Bits</c>) and shown in a
/// tooltip (<c>descfunc</c>), and where the property is used. Authored values a stat cannot save are reported, since the
/// game wraps them. Reads authored data only; never changes records or starts a build.
/// </summary>
public sealed class StatPreviewResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();
    /// <summary>Character levels the per-level table shows.</summary>
    public static readonly int[] SampleLevels = [1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 99];
    /// <summary>Property functions whose stat value is the rolled min–max; 17 stores its parameter instead.</summary>
    private static readonly HashSet<string> RangeFunctions = ["1", "2", "3", "4"];

    /// <summary>Where each table authors property codes: code, parameter, min and max column spellings.</summary>
    private static IEnumerable<(string Slot, string Code, string Param, string Min, string Max)> Slots(string table) => table switch
    {
        "uniqueitems" => Numbered(12, n => ($"prop{n}", $"par{n}", $"min{n}", $"max{n}")),
        "setitems" => Numbered(9, n => ($"prop{n}", $"par{n}", $"min{n}", $"max{n}"))
            .Concat(Lettered(1, 5, n => ($"aprop{n}", $"apar{n}", $"amin{n}", $"amax{n}"))),
        "sets" => Lettered(2, 5, n => ($"PCode{n}", $"PParam{n}", $"PMin{n}", $"PMax{n}")).Concat(Numbered(8, n => ($"FCode{n}", $"FParam{n}", $"FMin{n}", $"FMax{n}"))),
        "runes" => Numbered(7, n => ($"T1Code{n}", $"T1Param{n}", $"T1Min{n}", $"T1Max{n}")),
        "magicprefix" or "magicsuffix" or "automagic" or "qualityitems" => Numbered(3, n => ($"mod{n}code", $"mod{n}param", $"mod{n}min", $"mod{n}max")),
        "gems" => new[] { "weapon", "helm", "shield" }.SelectMany(part => Numbered(3, n => ($"{part}Mod{n}Code", $"{part}Mod{n}Param", $"{part}Mod{n}Min", $"{part}Mod{n}Max"))),
        "cubemain" => new[] { "", "b ", "c " }.SelectMany(p => Numbered(5, n => ($"{p}mod {n}", $"{p}mod {n} param", $"{p}mod {n} min", $"{p}mod {n} max"))),
        _ => []
    };
    public static readonly string[] UseTables = ["uniqueitems", "setitems", "sets", "runes", "magicprefix", "magicsuffix", "automagic", "qualityitems", "gems", "cubemain"];
    private static IEnumerable<(string, string, string, string, string)> Numbered(int count, Func<string, (string, string, string, string)> columns) =>
        Enumerable.Range(1, count).Select(i => columns(i.ToString(CultureInfo.InvariantCulture))).Select(c => (c.Item1, c.Item1, c.Item2, c.Item3, c.Item4));
    private static IEnumerable<(string, string, string, string, string)> Lettered(int from, int to, Func<string, (string, string, string, string)> columns) =>
        Enumerable.Range(from, to - from + 1).SelectMany(i => new[] { i + "a", i + "b" }).Select(columns).Select(c => (c.Item1, c.Item1, c.Item2, c.Item3, c.Item4));
    private static string Owner(string table, JsonObject row, int index) => table switch
    {
        "uniqueitems" or "setitems" or "sets" => row.S("index"),
        "runes" => row.S("*Rune Name") is { Length: > 0 } rune ? rune : row.S("Name"),
        "gems" => row.S("name"),
        "cubemain" => row.S("description"),
        "qualityitems" => "row " + index,
        _ => row.S("Name")
    } is { Length: > 0 } name ? name : "row " + index;

    /// <summary>Every authored use of a property code across the tables that grant properties.</summary>
    public static List<PropertyUse> Uses(PreviewTables.Session data, string code, CancellationToken token)
    {
        var uses = new List<PropertyUse>();
        foreach (var table in UseTables)
        {
            var rows = data.Rows(table, false);
            for (int i = 0; i < rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                foreach (var slot in Slots(table))
                    if (rows[i].S(slot.Code).Equals(code, StringComparison.OrdinalIgnoreCase))
                        uses.Add(new(table, Owner(table, rows[i], i), slot.Slot, rows[i].S(slot.Code), rows[i].S(slot.Param), rows[i].S(slot.Min), rows[i].S(slot.Max)));
            }
        }
        return uses;
    }

    public StatPreviewResult Resolve(ModProject project, string table, JsonObject record, string profile, string locale, CancellationToken token, StatPreviewOptions? options = null)
    {
        Require(table is "properties" or "itemstatcost", "Select a row in properties or itemstatcost.");
        options ??= new();
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var row = data.Effective(table, record);
        return table == "properties" ? Property(data, row, options, issues, token) : Stat(data, row, options, issues, token);
    }

    private StatPreviewResult Property(PreviewTables.Session data, JsonObject property, StatPreviewOptions options, List<string> issues, CancellationToken token)
    {
        var code = property.S("code");
        if (code.Length == 0) return new("Inactive row", ["Inactive/header row · no property code"], [], "", []);
        var lines = new List<string>(); var sections = new List<PreviewSection>();
        foreach (var comment in new[] { "*Tooltip", "*Desc", "*Parameter", "*Min", "*Max", "*Notes" })
            if (property.S(comment).Length > 0) lines.Add($"{comment.TrimStart('*')}: {property.S(comment)}");
        var uses = Uses(data, code, token);
        var sample = Sample(uses, options);
        lines.Add($"Shown with {sample.Label}");

        var functions = new List<string>(); var stats = new List<(string Stat, string Function)>();
        for (int slot = 1; slot <= 7; slot++)
        {
            var function = property.S("func" + slot).Trim();
            if (function is "" or "0") continue;
            var decoded = FunctionGuide.Describe("properties", "func" + slot, function);
            functions.Add(decoded != null ? $"{decoded.Title} — {Short(decoded.Summary)}" : $"func{slot} {function} · not in the data guide");
            var parts = new[] { "stat", "set", "val" }.Where(p => property.S(p + slot).Length > 0).Select(p => $"{p}{slot} = {property.S(p + slot)}").ToArray();
            if (parts.Length > 0) functions.Add("    " + string.Join(" · ", parts));
            if (property.S("stat" + slot) is { Length: > 0 } stat) stats.Add((stat, function));
        }
        if (functions.Count > 0) sections.Add(new("Functions", [.. functions]));
        else issues.Add("No func# is set, so this property does nothing.");

        foreach (var (stat, function) in stats.DistinctBy(s => s.Stat))
        {
            var cost = data.Find("itemstatcost", "Stat", stat, false);
            if (cost == null) { issues.Add($"Unresolved itemstatcost/Stat: {stat}."); continue; }
            var statLines = new List<string>();
            decimal value = function == "17" && decimal.TryParse(sample.Parameter, NumberStyles.Number, CultureInfo.InvariantCulture, out var perLevel) ? perLevel : sample.High;
            statLines.AddRange(TooltipLines(data, cost, sample with { Low = function == "17" ? value : sample.Low, High = value }, options.CharacterLevel, issues));
            statLines.AddRange(OpLines(cost, value, options.CharacterLevel));
            statLines.Add(StorageSummary(cost));
            sections.Add(new($"Stat {stat}", [.. statLines]));
            foreach (var use in uses) Overflow(use, cost, function, issues);
        }
        sections.Add(UsesSection(uses, $"property {code}"));
        return new(code, [.. lines], [.. sections], sample.Label, [.. issues.Distinct()]);
    }

    private StatPreviewResult Stat(PreviewTables.Session data, JsonObject cost, StatPreviewOptions options, List<string> issues, CancellationToken token)
    {
        var stat = cost.S("Stat");
        if (stat.Length == 0) return new("Inactive row", ["Inactive/header row · no stat name"], [], "", []);
        var lines = new List<string>(); var sections = new List<PreviewSection>();
        var rows = data.Rows("itemstatcost", false);
        int id = rows.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.S("Stat") == stat, (null!, -1)).Item2;
        lines.Add((id >= 0 ? $"Stat id {id} (row order)" : "Stat") + (cost.S("descpriority").Length > 0 ? $" · tooltip priority {cost.S("descpriority")} (higher shows first)" : ""));

        // Properties that write this stat, and where those properties are used.
        var setters = data.Rows("properties", false)
            .Select(p => (Property: p, Slot: Enumerable.Range(1, 7).FirstOrDefault(i => p.S("stat" + i).Equals(stat, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Slot > 0).ToArray();
        lines.Add(setters.Length > 0 ? "Set by properties: " + string.Join(", ", setters.Select(s => s.Property.S("code"))) : "No property writes this stat; only skills, states or the game itself can set it.");
        var uses = setters.SelectMany(s => Uses(data, s.Property.S("code"), token).Select(u => (Use: u, Function: s.Property.S("func" + s.Slot)))).ToList();
        var perLevel = uses.FirstOrDefault(u => u.Function == "17" && u.Use.Parameter.Length > 0);
        var sample = Sample(uses.Where(u => u.Function != "17").Select(u => u.Use).ToList(), options);
        if (options.Min == null && options.Max == null && sample.Source == "default" && perLevel.Use != null && decimal.TryParse(perLevel.Use.Parameter, NumberStyles.Number, CultureInfo.InvariantCulture, out var parameterValue))
            sample = new(parameterValue, parameterValue, perLevel.Use.Parameter, $"the parameter of {perLevel.Use.Owner} ({perLevel.Use.Code} {perLevel.Use.Parameter})", "use");
        lines.Add($"Shown with {sample.Label}");

        sections.Add(new("Tooltip", [.. TooltipLines(data, cost, sample, options.CharacterLevel, issues)]));
        var op = OpLines(cost, sample.High, options.CharacterLevel).ToArray();
        if (op.Length > 0) sections.Add(new("Value", op));
        sections.Add(new("Storage", [.. StorageLines(cost)]));
        foreach (var (use, function) in uses) Overflow(use, cost, function, issues);
        sections.Add(UsesSection(uses.Select(u => u.Use).ToList(), $"stat {stat}"));
        return new(stat, [.. lines], [.. sections], sample.Label, [.. issues.Distinct()]);
    }

    private sealed record SampleValues(decimal Low, decimal High, string Parameter, string Label, string Source);
    private static SampleValues Sample(IReadOnlyList<PropertyUse> uses, StatPreviewOptions options)
    {
        if (options.Min != null || options.Max != null)
        {
            decimal low = options.Min ?? options.Max!.Value, high = options.Max ?? low;
            return new(low, high, options.Parameter, $"min {Text(low)}, max {Text(high)}" + (options.Parameter.Length > 0 ? $", parameter {options.Parameter}" : "") + " (set above)", "options");
        }
        foreach (var use in uses)
            if (decimal.TryParse(use.Min, NumberStyles.Number, CultureInfo.InvariantCulture, out var min))
            {
                var max = decimal.TryParse(use.Max, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : min;
                var parameter = options.Parameter.Length > 0 ? options.Parameter : use.Parameter;
                return new(Math.Min(min, max), Math.Max(min, max), parameter, $"the values of {use.Owner} ({use.Slot} {use.Values})", "use");
            }
        return new(10, 20, options.Parameter, "sample values 10–20 (nothing authored uses it)", "default");
    }

    /// <summary>The stat's tooltip line at the sample's low and high ends, a negative value when it has its own string, and its group line.</summary>
    private static IEnumerable<string> TooltipLines(PreviewTables.Session data, JsonObject cost, SampleValues sample, int characterLevel, List<string> issues)
    {
        // A per-level stat stores a rate; the tooltip shows what that rate gives at the character's level.
        bool perLevel = PerLevel(cost);
        string Show(decimal value, string prefix = "desc") => perLevel
            ? $"{Text(value)} per level → {Describe(data, cost, prefix, AtLevel(cost, value, characterLevel), sample.Parameter, issues)}  (at character level {characterLevel})"
            : $"{Text(value)} → {Describe(data, cost, prefix, value, sample.Parameter, issues)}";
        var function = cost.S("descfunc").Trim();
        if (function is "" or "0") { yield return "Not shown in item tooltips (descfunc is empty)."; yield break; }
        var decoded = FunctionGuide.Describe("itemstatcost", "descfunc", function);
        yield return decoded != null ? decoded.Title : $"descfunc {function} · not in the data guide";
        var values = sample.Low == sample.High ? new[] { sample.High } : [sample.Low, sample.High];
        foreach (var value in values) yield return "  " + Show(value);
        if (cost.S("descstrneg").Length > 0 && cost.S("descstrneg") != cost.S("descstrpos") && sample.High > 0)
            yield return "  " + Show(-sample.High);
        if (cost.S("dgrp") is { Length: > 0 } group && group != "0")
        {
            var members = data.Rows("itemstatcost", false).Where(r => r.S("dgrp") == group).Select(r => r.S("Stat")).ToArray();
            yield return $"Group {group} ({string.Join(", ", members)}): when all are equal the tooltip shows one line instead";
            if (cost.S("dgrpfunc") is { Length: > 0 } && cost.S("dgrpfunc") != "0") yield return "  " + Show(sample.High, "dgrp");
        }
    }

    /// <summary>
    /// descfunc (or dgrpfunc) applied to one value. The number-and-string functions are rendered exactly; functions that
    /// need a skill, class, monster or charge count from the parameter show the string with what is known.
    /// </summary>
    public static string Describe(PreviewTables.Session data, JsonObject cost, string prefix, decimal value, string parameter, List<string> issues)
    {
        string function = cost.S(prefix + "func").Trim(), position = cost.S(prefix + "val").Trim();
        string Localized(string column) => cost.S(column) is { Length: > 0 } key ? data.Localize(key) : "";
        var text = Localized(value < 0 && cost.S(prefix + "strneg").Length > 0 ? prefix + "strneg" : prefix + "strpos");
        var second = Localized(prefix + "str2");
        string Signed(decimal v) => (v >= 0 ? "+" : "") + Text(v);
        string Place(string number) => position switch { "1" => $"{number} {text}", "2" => $"{text} {number}", _ => text };
        string WithSecond(string line) => second.Length > 0 ? $"{line} {second}" : line;
        string Sprintf(decimal v) => SkillTooltips.Format(text, [v]);
        string result = function switch
        {
            "1" => Place(Signed(value)),
            "2" => Place(Text(value) + "%"),
            "3" => Place(Text(value)),
            "4" => Place(Signed(value) + "%"),
            "5" => Place(Text(Math.Truncate(value * 100 / 128)) + "%"),
            "6" => WithSecond(Place(Signed(value))),
            "7" => WithSecond(Place(Text(value) + "%")),
            "8" => WithSecond(Place(Signed(value) + "%")),
            "9" => WithSecond(Place(Text(value))),
            "10" => WithSecond(Place(Text(Math.Truncate(value * 100 / 128)) + "%")),
            "11" => value == 0 ? "Repairs durability (value 0 never repairs)" : SkillTooltips.Format(data.Localize("ModStre9t"), [1m, Math.Ceiling(100 / Math.Abs(value))]),
            "12" => Place(value > 1 ? Signed(value) : Text(value)),
            "19" => WithSecond(Sprintf(value)),
            "20" => Place(Text(-value) + "%"),
            "21" => WithSecond(Place(Text(-value) + "%")),
            "29" => WithSecond(Sprintf(Math.Abs(value))),
            _ => ""
        };
        if (result.Length > 0) return result;
        var needs = function switch
        {
            "13" or "14" => "a class or skill tab from the parameter",
            "15" or "16" or "24" or "27" or "28" or "30" or "31" => "a skill from the parameter",
            "17" or "18" => "the time of day",
            "22" => "a monster type from the parameter",
            "23" => "a monster from the parameter",
            _ => "logic this preview does not model"
        };
        return $"{SkillTooltips.Format(text, [parameter.Length > 0 ? parameter : "?", value, value])}  (approximate: needs {needs})";
    }

    /// <summary>What the stat's op does to another stat, with the per-level numbers where the op scales with character level.</summary>
    private static IEnumerable<string> OpLines(JsonObject cost, decimal value, int characterLevel)
    {
        var op = cost.S("op").Trim();
        if (op is "" or "0") yield break;
        var decoded = FunctionGuide.Describe("itemstatcost", "op", op);
        var targets = Enumerable.Range(1, 3).Select(i => cost.S("op stat" + i)).Where(s => s.Length > 0).ToArray();
        yield return (decoded != null ? decoded.Title : $"op {op}") + (targets.Length > 0 ? " → " + string.Join(", ", targets) : "");
        if (op is "2" or "3" or "4" or "5")
        {
            var baseStat = cost.S("op base");
            int shift = (int)Number(cost, "op param");
            Require(shift is >= 0 and <= 30, $"op param {shift} is out of range.");
            decimal At(int level) => AtLevel(cost, value, level);
            var unit = op is "3" or "5" ? "%" : "";
            yield return $"  {Text(value)} × {(baseStat is "" or "level" ? "character level" : baseStat)} ÷ {1 << shift} (op param {shift})" + (baseStat is "" or "level" ? $" = {Text(At(characterLevel))}{unit} at level {characterLevel}" : "");
            if (baseStat is "" or "level") yield return "  " + string.Join(" · ", SampleLevels.Select(l => $"{l}: {Text(At(l))}{unit}"));
        }
        else if (op is "1" or "11" or "13") yield return $"  Adds {Text(value)}% of {(targets.Length > 0 ? string.Join(", ", targets) : "its op stat")}";
    }

    private static bool PerLevel(JsonObject cost) => cost.S("op").Trim() is "2" or "3" or "4" or "5" && cost.S("op base").Trim() is "" or "level";
    /// <summary>A per-level rate at a character level: value × level ÷ 2^op param, rounded down.</summary>
    public static decimal AtLevel(JsonObject cost, decimal value, int level)
    {
        int shift = (int)Number(cost, "op param");
        Require(shift is >= 0 and <= 30, $"op param {shift} is out of range.");
        return Math.Floor(value * level / (decimal)Math.Pow(2, shift));
    }
    private static string StorageSummary(JsonObject cost) =>
        SaveRange(cost) is { } range ? $"Saved on items as {Text(range.Min)} to {Text(range.Max)} (Save Bits {cost.S("Save Bits")}, Save Add {cost.S("Save Add", "0")})" : "Not saved on items (Save Bits is empty)";
    private static IEnumerable<string> StorageLines(JsonObject cost)
    {
        yield return StorageSummary(cost);
        if (Number(cost, "Save Param Bits") > 0) yield return $"Parameter saved in {cost.S("Save Param Bits")} bits: 0 to {Text(Max(Number(cost, "Save Param Bits")))}";
        if (Number(cost, "Send Bits") > 0) yield return $"Sent to the client in {cost.S("Send Bits")} bits" + (Flag(cost, "Signed") ? ", signed" : ", unsigned");
        if (Flag(cost, "Saved") || Number(cost, "CSvBits") > 0)
            yield return $"Saved on the character in {cost.S("CSvBits", "?")} bits" + (Flag(cost, "CSvSigned") ? ", signed" : ", unsigned") + (Number(cost, "CSvParam") > 0 ? $", parameter {cost.S("CSvParam")} bits" : "");
        if (Number(cost, "ValShift") > 0) yield return $"ValShift {cost.S("ValShift")}: the game keeps this stat × {1 << (int)Number(cost, "ValShift")} internally";
        if (Flag(cost, "fMin")) yield return $"Never goes below {cost.S("MinAccr", "0")} (fMin)";
    }
    /// <summary>The values an item can save: the stored number is value + Save Add in Save Bits unsigned bits.</summary>
    public static (decimal Min, decimal Max)? SaveRange(JsonObject cost)
    {
        var bits = Number(cost, "Save Bits");
        if (bits <= 0) return null;
        var add = Number(cost, "Save Add");
        return (add == 0 ? 0 : -add, Max(bits) - add);
    }
    private static decimal Max(decimal bits) => (decimal)(BigInteger.Pow(2, (int)Math.Min(bits, 64)) - 1);

    /// <summary>Reports an authored value the stat cannot save; the game wraps it around instead of capping it.</summary>
    private static void Overflow(PropertyUse use, JsonObject cost, string function, List<string> issues)
    {
        if (SaveRange(cost) is not { } range) return;
        var values = function == "17" ? new[] { use.Parameter } : RangeFunctions.Contains(function) ? [use.Min, use.Max] : [];
        foreach (var text in values)
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && (value < range.Min || value > range.Max))
                issues.Add($"{use.Table} · {use.Owner} · {use.Slot} {use.Code}: {(function == "17" ? "parameter" : "value")} {text} is outside the {Text(range.Min)} to {Text(range.Max)} that {cost.S("Stat")} can save (Save Bits {cost.S("Save Bits")}, Save Add {cost.S("Save Add", "0")}); the game wraps it.");
    }

    private static PreviewSection UsesSection(IReadOnlyList<PropertyUse> uses, string what)
    {
        const int Shown = 15;
        if (uses.Count == 0) return new("Used by", [$"Nothing in {string.Join(", ", UseTables)} uses this {what}."]);
        var counts = string.Join(" · ", uses.GroupBy(u => u.Table).Select(g => $"{g.Key} {g.Count()}"));
        var lines = new List<string> { $"{uses.Count} use{(uses.Count == 1 ? "" : "s")}: {counts}" };
        lines.AddRange(uses.Take(Shown).Select(u => "  " + u));
        if (uses.Count > Shown) lines.Add($"  … {uses.Count - Shown} more");
        return new("Used by", [.. lines]);
    }
    private static string Short(string text) => (text = Regex.Replace(text, "\\s+", " ").Trim()).Length > 180 ? text[..180].TrimEnd() + "…" : text;
}
