using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>The rendered item. <see cref="Text"/> keeps the cells each value was read from; <see cref="Lines"/> is the plain text.</summary>
public record ItemPreviewResult(string Name, bool IsSet, string[] Lines, string[] Issues)
{
    public PreviewText[] Text { get; } = PreviewText.Parse(Lines);
    public string[] Lines { get; } = PreviewText.Plain(Lines);
    /// <summary>The same item laid out as the game's tooltip; null when it could not be resolved at all.</summary>
    public ItemTooltip? Tooltip { get; init; }
}

/// <summary>
/// An item as the game's tooltip shows it, top to bottom: the base name, the derived stat lines (defense, damage,
/// requirements), the item's own properties and, for set items, the set section. Every line keeps its cell links.
/// </summary>
public sealed record ItemTooltip(PreviewText? BaseName, PreviewText[] Stats, PreviewText[] Properties, PreviewText[] SetLines)
{
    public static ItemTooltip From(string? baseName, IEnumerable<string> stats, IEnumerable<string> properties, IEnumerable<string> setLines) =>
        new(baseName == null ? null : PreviewText.Parse(baseName), [.. stats.Select(PreviewText.Parse)], Collapse(properties), Collapse(setLines));
    /// <summary>
    /// One line per repeated description: a property that sets two stats with one text (dmg% raises minimum and maximum
    /// damage percent, both "+X% Enhanced Damage") shows once in game.
    /// </summary>
    private static PreviewText[] Collapse(IEnumerable<string> lines)
    {
        var parsed = new List<PreviewText>();
        foreach (var line in lines.Select(PreviewText.Parse))
            if (parsed.Count == 0 || parsed[^1].Text != line.Text) parsed.Add(line);
            else parsed[^1] = parsed[^1] with { Links = [.. parsed[^1].Links.Zip(line.Links, (a, b) => a with { Targets = [.. a.Targets.Union(b.Targets)] })] };
        return [.. parsed];
    }
}

/// <summary>Worker-owned resolver. Reads authored data only; never changes records or starts a build.</summary>
public sealed class ItemPreviewResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();
    public ItemPreviewResult Resolve(ModProject project, string table, JsonObject record, string profile, int level, string locale, CancellationToken token)
    {
        Require(table is "uniqueitems" or "setitems", "Select a Unique or Set item.");
        Require(level is >= 1 and <= 99, "Character level must be 1–99.");
        var issues = new List<string>(); var lines = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        JsonObject Effective(string name, JsonObject row) => data.Effective(name, row);
        JsonObject? Find(string name, string column, string value, bool required = true) => data.Find(name, column, value, required);
        string Localize(string key) => data.Localize(key);
        CellLink? Cell(JsonObject? row, string column) => data.Cell(row, column);
        static string Mark(string text, params CellLink?[] cells) => PreviewText.Mark(text, cells);
        string SkillName(string parameter, out string characterClass)
        {
            characterClass = "Class";
            if (parameter.Length == 0) return "Skill";
            var skill = Find("skills", "skill", parameter, false);
            if (skill == null) return parameter;
            characterClass = skill.S("charclass") switch { "ama" => "Amazon", "sor" => "Sorceress", "nec" => "Necromancer", "pal" => "Paladin", "bar" => "Barbarian", "dru" => "Druid", "ass" => "Assassin", _ => "Class" };
            var description = Find("skilldesc", "skilldesc", skill.S("skilldesc"), false);
            var key = description?.S("str name") ?? "";
            return key.Length == 0 ? parameter : Localize(key);
        }
        string FillPropertyTemplate(string template, IEnumerable<string> values, string parameter, CellLink? parameterCell, string suffix = "")
        {
            // Values first: a marked skill name carries its cell's sourceId, which must not be read as a "#" placeholder.
            using var replacements = values.GetEnumerator();
            template = Regex.Replace(template, "#", _ => replacements.MoveNext() ? replacements.Current : "#");
            var skill = SkillName(parameter, out var characterClass);
            template = template.Replace("[Skill Tab]", parameter.Length == 0 ? "Skill Tab" : "Skill Tab " + Mark(parameter, parameterCell))
                .Replace("[Skill]", parameter.Length == 0 ? skill : Mark(skill, parameterCell)).Replace("[Class]", characterClass);
            return template + suffix;
        }
        var item = Effective(table, record); bool set = table == "setitems";
        var rawName = item.S("index"); var code = item.S(set ? "item" : "code");
        if (code.Length == 0) return new(rawName.Length == 0 ? "Inactive row" : rawName, set, ["Inactive/header row · no base item code"], []);
        var name = Localize(rawName);
        string? tooltipBase = null, requiredLevel = null; List<string> stats = [], requirementStats = [];
        var bases = new[] { "weapons", "armor", "misc" }.Select(t => (Table: t, Row: Find(t, "code", code, false))).Where(x => x.Row != null).ToArray();
        Require(bases.Length <= 1, "Ambiguous base item code: " + code);
        JsonObject? baseItem = bases.FirstOrDefault().Row;
        if (baseItem == null) issues.Add("Missing base item: " + code + " (weapons/armor/misc).");
        else
        {
            lines.Add(tooltipBase = Mark(Localize(baseItem.S("namestr", code)), Cell(item, set ? "item" : "code"), Cell(baseItem, "namestr")));
            decimal itemLevel = Number(item, "lvl req"), baseLevel = Number(baseItem, "levelreq");
            var levelText = Mark(N(Math.Max(itemLevel, baseLevel)), itemLevel >= baseLevel ? Cell(item, "lvl req") : null, baseLevel >= itemLevel ? Cell(baseItem, "levelreq") : null);
            lines.Add("Required level: " + levelText); requiredLevel = "Required Level: " + levelText;
            lines.Add($"Base requirements: STR {Mark(N(Number(baseItem, "reqstr")), Cell(baseItem, "reqstr"))} · DEX {Mark(N(Number(baseItem, "reqdex")), Cell(baseItem, "reqdex"))}");
        }
        lines.Add($"{profile} · {locale} · character level {level} · roll ranges");
        var totals = new Dictionary<string, (decimal Low, decimal High)>(); bool calculable = true;
        // Each end of a range links the cell it was read from; a single value links both.
        string Range(decimal low, decimal high, CellLink? lowCell = null, CellLink? highCell = null) => low == high ? Mark(N(low), lowCell, highCell) : $"({Mark(N(low), lowCell)}–{Mark(N(high), highCell)})";
        string InlineRange(decimal low, decimal high, CellLink? lowCell = null, CellLink? highCell = null) => low == high ? Mark(N(low), lowCell, highCell) : $"{Mark(N(low), lowCell)}–{Mark(N(high), highCell)}";
        // Reads a property from the named columns of a row. Its values link to their cells; a line with no value links the property code.
        void Property(JsonObject row, string propColumn, string minColumn, string maxColumn, string parameterColumn, bool main)
        {
            var prop = row.S(propColumn); if (prop.Length == 0) return;
            int first = lines.Count;
            PropertyLines(prop, row.S(minColumn), row.S(maxColumn), row.S(parameterColumn), Cell(row, minColumn), row.S(maxColumn).Length == 0 ? Cell(row, minColumn) : Cell(row, maxColumn), Cell(row, parameterColumn), main);
            for (int i = first; i < lines.Count; i++) if (PreviewText.Plain(lines[i]) == lines[i]) lines[i] = Mark(lines[i], Cell(row, propColumn));
        }
        void PropertyLines(string prop, string min, string max, string parameter, CellLink? minCell, CellLink? maxCell, CellLink? parameterCell, bool main)
        {
            var definition = Find("properties", "code", prop, false);
            void Unsupported(string why) { lines.Add($"{prop}: min={Mark(min, minCell)}, max={Mark(max, maxCell)}, param={Mark(parameter, parameterCell)}"); issues.Add($"{prop}: {why}"); if (main) calculable = false; }
            if (definition == null) { Unsupported("property definition unavailable."); return; }
            try
            {
                decimal authoredMin = min.Length == 0 ? 0 : decimal.Parse(min, CultureInfo.InvariantCulture), authoredMax = max.Length == 0 ? authoredMin : decimal.Parse(max, CultureInfo.InvariantCulture);
                decimal low = Math.Min(authoredMin, authoredMax), high = Math.Max(authoredMin, authoredMax);
                CellLink? lowCell = authoredMin <= authoredMax ? minCell : maxCell, highCell = authoredMin <= authoredMax ? maxCell : minCell;
                var functions = Enumerable.Range(1, 7).Where(i => definition.S("func" + i).Length > 0 && definition.S("func" + i) != "0").ToArray();
                Require(functions.Length > 0, "Property has no supported function.");
                var functionIds = functions.Select(i => definition.S("func" + i)).ToArray();
                var authoredTooltip = definition.S("*Tooltip");
                // These functions need special generation-time encoding, but their authored tooltip
                // is deterministic from min/max/param and can be previewed without claiming a stat total.
                var displayFunctions = new HashSet<string> { "8", "9", "10", "11", "12", "13", "14", "15", "16", "18", "19", "20", "21", "22", "24", "25", "36" };
                Require(functionIds.Any(displayFunctions.Contains) || authoredMin <= authoredMax || authoredMin < 0 && authoredMax < 0, "Minimum exceeds maximum.");
                if (functionIds.Any(displayFunctions.Contains) && authoredTooltip.Length > 0)
                {
                    IEnumerable<string> replacements = functionIds.Contains("11")
                        ? [authoredMin == 0 ? "5" : Mark(N(authoredMin), minCell), Mark(N(authoredMax), maxCell)]
                        : functionIds.Contains("19") ? [Mark(N(authoredMax), maxCell), Mark(N(authoredMin), minCell), Mark(N(authoredMin), minCell)]
                        : functionIds.Contains("15") || functionIds.Contains("16") ? [Mark(N(low), lowCell), Mark(N(high), highCell)]
                        : [InlineRange(low, high, lowCell, highCell)];
                    var suffix = functionIds.Contains("18") ? " (varies with in-game time)" : "";
                    lines.Add(FillPropertyTemplate(authoredTooltip, replacements, parameter, parameterCell, suffix));
                    return;
                }
                if (functionIds.All(displayFunctions.Contains))
                {
                    lines.Add(parameter.Length > 0 ? $"{prop}: {Mark(parameter, parameterCell)}" : $"{prop}: {InlineRange(low, high, lowCell, highCell)}");
                    return;
                }
                decimal tooltipLow = low, tooltipHigh = high; CellLink? tooltipLowCell = lowCell, tooltipHighCell = highCell;
                foreach (var entry in functions.SelectMany(slot => definition.S("func" + slot) == "7" ? new[] { (Slot: slot, Minimum: true), (Slot: slot, Minimum: false) } : new[] { (Slot: slot, Minimum: false) }))
                {
                    int slot = entry.Slot;
                    var function = definition.S("func" + slot); string stat = definition.S("stat" + slot);
                    if (displayFunctions.Contains(function)) continue; // A second ordinary slot supplies the visible stat.
                    Require(definition.S("set" + slot) is "" or "0" && definition.S("val" + slot) is "" or "0", "Property set/constant-value behavior needs a specialized handler.");
                    decimal a = low, b = high; CellLink? aCell = lowCell, bCell = highCell;
                    if (function == "17")
                    {
                        if (parameter.Length == 0)
                        {
                            lines.Add(authoredTooltip.Length > 0 ? FillPropertyTemplate(authoredTooltip, [InlineRange(low, high, lowCell, highCell)], parameter, parameterCell) : $"{prop}: {InlineRange(low, high, lowCell, highCell)}");
                            return;
                        }
                        var scaling = Find("itemstatcost", "Stat", stat);
                        Require(scaling != null, "Missing level-scaling stat metadata.");
                        a = b = decimal.Parse(parameter, CultureInfo.InvariantCulture); aCell = bCell = tooltipLowCell = tooltipHighCell = parameterCell;
                        if (scaling!.S("op") is "2" or "4" or "5" && scaling.S("op base") == "level")
                        {
                            var shift = scaling.S("op param").Length > 0 ? Number(scaling!, "op param") : 0;
                            Require(shift is >= 0 and <= 16, "Unsupported level-scaling divisor.");
                            a = b = Math.Floor(a * level / (decimal)Math.Pow(2, (double)shift));
                        }
                        tooltipLow = a; tooltipHigh = b;
                        if (scaling.S("descfunc") == "11" && a != 0) tooltipLow = tooltipHigh = Math.Ceiling(100 / a);
                    }
                    else Require(function is "1" or "2" or "3" or "5" or "6" or "7" or "23", "Unsupported property function " + function + ".");
                    if (function == "23") { Require(prop == "ethereal", "Unsupported special property."); lines.Add("Ethereal"); if (main) totals["ethereal"] = (1, 1); continue; }
                    if (function is "5" or "6" or "7") stat = function switch { "5" => "mindamage", "6" => "maxdamage", _ => entry.Minimum ? "item_mindamage_percent" : "item_maxdamage_percent" };
                    var cost = Find("itemstatcost", "Stat", stat);
                    Require(cost != null, "Missing stat metadata: " + stat);
                    if (main)
                    {
                        var targetStat = function == "17" && cost!.S("op stat1").Length > 0 ? cost.S("op stat1") : stat;
                        if (function == "17" && cost!.S("op") == "5")
                        {
                            if (stat == "item_armorpercent_perlevel") targetStat = "item_armor_percent";
                            else if (stat == "item_maxdamage_percent_perlevel")
                            {
                                foreach (var damageStat in new[] { "item_mindamage_percent", "item_maxdamage_percent" })
                                { var damageTotal = totals.GetValueOrDefault(damageStat); totals[damageStat] = (damageTotal.Low + a, damageTotal.High + b); }
                                targetStat = "";
                            }
                            else { calculable = false; issues.Add($"{prop}: unsupported percentage target for {stat}."); targetStat = ""; }
                        }
                        if (targetStat.Length > 0) { var total = totals.GetValueOrDefault(targetStat); totals[targetStat] = (total.Low + a, total.High + b); }
                    }
                    if (authoredTooltip.Length == 0)
                    {
                        var desc = cost!.S("descfunc");
                        Require(!(a < 0 && b > 0), "Mixed-sign ranges need separate positive and negative descriptions.");
                        var labelKey = cost.S(b < 0 ? "descstrneg" : "descstrpos");
                        string label = labelKey.Length == 0 ? stat : Localize(labelKey);
                        string value = Range(a, b, aCell, bCell), plus = a >= 0 ? "+" : "";
                        if (desc == "19")
                        {
                            var placeholders = Regex.Matches(label, @"%[+]?d").Count;
                            Require(placeholders <= 1, "Expected no more than one numeric localization placeholder.");
                            if (b < 0) label = label.Replace("-%d", "%d");
                            if (placeholders == 1) label = Regex.Replace(label, @"%[+]?d", m => (m.Value.Contains('+') ? plus : "") + value).Replace("%%", "%");
                        }
                        else if (desc is "1" or "2" or "3" or "4" or "5")
                        {
                            string number = desc switch { "1" => plus + value, "2" => value + "%", "4" => plus + value + "%", "5" => Range(a * 100 / 128, b * 100 / 128, aCell, bCell) + "%", _ => value };
                            label = cost.S("descval") switch { "1" => number + " " + label, "2" => label + " " + number, _ => label };
                        }
                        else label = stat + ": " + value;
                        lines.Add(label + (function == "17" ? $" (at level {level})" : ""));
                    }
                }
                if (authoredTooltip.Length > 0) lines.Add(FillPropertyTemplate(authoredTooltip, [InlineRange(tooltipLow, tooltipHigh, tooltipLowCell, tooltipHighCell)], parameter, parameterCell, functionIds.Contains("17") ? $" (at level {level})" : ""));
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or InvalidDataException or OverflowException) { Unsupported(e.Message); }
        }
        int propertiesStart = lines.Count;
        // Vanilla's twelve slots, plus any further propN a modded table fills.
        var slots = item.Select(f => f.Key).Where(k => k.StartsWith("prop", StringComparison.Ordinal) && int.TryParse(k.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(k => int.Parse(k.AsSpan(4), CultureInfo.InvariantCulture)).Concat(Enumerable.Range(1, 12)).Distinct().Order();
        foreach (var i in slots) Property(item, "prop" + i, "min" + i, "max" + i, "par" + i, true);
        int propertiesEnd = lines.Count;
        if (baseItem != null)
        {
            lines.Add("— Calculations —");
            if (!calculable) lines.Add("Derived totals unavailable: resolve unsupported properties above.");
            else
            {
                var eth = totals.ContainsKey("ethereal") ? 1.5m : 1m;
                var requirements = totals.GetValueOrDefault("item_req_percent");
                foreach (var requirement in new[] { ("Strength", "reqstr"), ("Dexterity", "reqdex") })
                {
                    decimal value = Number(baseItem, requirement.Item2);
                    decimal Adjust(decimal percent) => Math.Max(0, value + Math.Truncate(value * percent / 100) - (eth > 1 ? 10 : 0));
                    var baseCell = Cell(baseItem, requirement.Item2);
                    var requirementText = Range(Adjust(requirements.Low), Adjust(requirements.High), baseCell, baseCell);
                    lines.Add("Required " + requirement.Item1 + ": " + requirementText);
                    // The game shows Required Dexterity above Required Strength.
                    if (value > 0) requirementStats.Insert(requirement.Item1 == "Dexterity" ? 0 : requirementStats.Count, "Required " + requirement.Item1 + ": " + requirementText);
                }
                var ed = totals.GetValueOrDefault("item_maxdamage_percent");
                var minEd = totals.GetValueOrDefault("item_mindamage_percent");
                var minAdd = totals.GetValueOrDefault("mindamage"); var maxAdd = totals.GetValueOrDefault("maxdamage");
                foreach (var damage in new[] { ("One-hand", "mindam", "maxdam"), ("Two-hand", "2handmindam", "2handmaxdam"), ("Throw", "minmisdam", "maxmisdam") })
                    if (baseItem.S(damage.Item3).Length > 0)
                    {
                        decimal Base(string key) => Math.Floor(Number(baseItem, key) * eth);
                        CellLink? minCell = Cell(baseItem, damage.Item2), maxCell = Cell(baseItem, damage.Item3);
                        string low = Range(Math.Floor(Base(damage.Item2) * (100 + minEd.Low) / 100) + minAdd.Low, Math.Floor(Base(damage.Item2) * (100 + minEd.High) / 100) + minAdd.High, minCell, minCell);
                        string high = Range(Math.Floor(Base(damage.Item3) * (100 + ed.Low) / 100) + maxAdd.Low, Math.Floor(Base(damage.Item3) * (100 + ed.High) / 100) + maxAdd.High, maxCell, maxCell);
                        lines.Add($"{damage.Item1} physical damage: min {low} / max {high}");
                        stats.Add($"{damage.Item1 switch { "One-hand" => "One-Hand", "Two-hand" => "Two-Hand", _ => "Throw" }} Damage: {low} to {high}");
                    }
                if (baseItem.S("maxac").Length > 0)
                {
                    var defense = totals.GetValueOrDefault("item_armor_percent"); var flat = totals.GetValueOrDefault("armorclass");
                    decimal low = Number(baseItem, "minac"), high = Number(baseItem, "maxac");
                    if (defense.Low > 0) low = high + 1; if (defense.High > 0) high++;
                    CellLink? maxCell = Cell(baseItem, "maxac"), lowCell = defense.Low > 0 ? maxCell : Cell(baseItem, "minac");
                    lines.Add("Defense: " + Range(Math.Floor(Math.Floor(low * eth) * (100 + defense.Low) / 100) + flat.Low, Math.Floor(Math.Floor(high * eth) * (100 + defense.High) / 100) + flat.High, lowCell, maxCell));
                    stats.Insert(0, lines[^1]);
                }
                lines.Add("Listed-property subtotals; excludes character bonuses, automods/staffmods, sockets, upgrades and set activation.");
            }
        }
        int setStart = lines.Count;
        if (set)
        {
            lines.Add("— " + Mark(Localize(item.S("set")), Cell(item, "set")) + " —");
            for (int i = 1; i <= 5; i++) foreach (var suffix in new[] { "a", "b" })
            {
                string n = i + suffix;
                if (item.S("aprop" + n).Length == 0) continue;
                lines.Add(item.S("add func") == "2" ? $"Item bonus with {i + 1} set pieces:" : $"Conditional item bonus {n} (add func {item.S("add func")}):");
                Property(item, "aprop" + n, "amin" + n, "amax" + n, "apar" + n, false);
            }
            var setRow = Find("sets", "index", item.S("set"));
            if (setRow != null)
            {
                for (int i = 2; i <= 5; i++) foreach (var suffix in new[] { "a", "b" })
                {
                    string n = i + suffix; if (setRow.S("PCode" + n).Length == 0) continue;
                    lines.Add($"Set bonus with {i} pieces:"); Property(setRow, "PCode" + n, "PMin" + n, "PMax" + n, "PParam" + n, false);
                }
                lines.Add("Full set bonuses:");
                for (int i = 1; i <= 8; i++) Property(setRow, "FCode" + i, "FMin" + i, "FMax" + i, "FParam" + i, false);
            }
        }
        int setEnd = lines.Count;
        if (issues.Count > 0) lines.Add("Requirements shown are authored item/base values; unsupported effects may change them.");
        // The game lists defense and damage, then requirements. The derived ones need every property understood; the required level is always known.
        stats.AddRange(requirementStats);
        if (requiredLevel != null) stats.Add(requiredLevel);
        return new(name, set, lines.ToArray(), issues.Distinct().ToArray())
        {
            Tooltip = ItemTooltip.From(tooltipBase, stats, lines.Skip(propertiesStart).Take(propertiesEnd - propertiesStart), lines.Skip(setStart).Take(setEnd - setStart))
        };
    }
    private static string N(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal Number(JsonObject row, string field) => row.S(field).Length == 0 ? 0 : decimal.Parse(row.S(field), CultureInfo.InvariantCulture);
}
