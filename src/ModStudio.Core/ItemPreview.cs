using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record ItemPreviewResult(string Name, bool IsSet, string[] Lines, string[] Issues);

/// <summary>Worker-owned resolver. Reads authored data only; never changes records or starts a build.</summary>
public sealed class ItemPreviewResolver
{
    private sealed record Cached(long Length, DateTime Modified, JsonNode Data) { public Dictionary<string, Dictionary<string, string>> Locales { get; } = new(); }
    private readonly Dictionary<string, Cached> cache = new();
    private long retained;
    private JsonNode ReadCached(string file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); NoLinks(file); var info = new FileInfo(file);
        Require(info.Exists, "Missing data: " + file);
        Require(info.Length <= 32 * 1024 * 1024, "Preview input exceeds 32 MiB: " + file);
        if (cache.TryGetValue(file, out var old) && old.Length == info.Length && old.Modified == info.LastWriteTimeUtc) return old.Data;
        if (old != null) { retained -= old.Length; cache.Remove(file); }
        if (retained + info.Length > 64 * 1024 * 1024) { cache.Clear(); retained = 0; }
        var data = Read(file); cache[file] = new(info.Length, info.LastWriteTimeUtc, data); retained += info.Length; return data;
    }
    public void Clear() { cache.Clear(); retained = 0; }
    public ItemPreviewResult Resolve(ModProject project, string table, JsonObject record, string profile, int level, string locale, CancellationToken token)
    {
        Require(table is "uniqueitems" or "setitems", "Select a Unique or Set item.");
        Require(level is >= 1 and <= 99, "Character level must be 1–99.");
        var issues = new List<string>(); var lines = new List<string>();
        var rules = new List<JsonNode>();
        var profilePath = Inside(project.Root, $"compatibility/{profile}/profile.json");
        bool standard = File.Exists(profilePath) ? ReadCached(profilePath, token).S("stringMode", "standard") == "standard" : profile == "standard";
        if (File.Exists(profilePath))
            foreach (var path in (JsonArray?)ReadCached(profilePath, token)["tableOverrides"] ?? [])
                rules.Add(ReadCached(Inside(Path.GetDirectoryName(profilePath)!, path!.GetValue<string>()), token));
        JsonObject Effective(string name, JsonObject row)
        {
            var fields = (JsonObject)row["fields"]!.DeepClone(); var occupied = new HashSet<string>();
            foreach (var rule in rules.Where(r => r.S("table") == name && r.S("record") == row.S("sourceId")))
            {
                Require(rule["targets"] == null, $"{name}: bank-specific overrides need a bank selection; preview is unavailable.");
                foreach (var change in rule["changes"]!.AsObject())
                {
                    Require(occupied.Add(change.Key), $"{name}/{change.Key}: competing overrides.");
                    Require(fields.S(change.Key) == change.Value.S("expect"), $"{name}/{change.Key}: stale override. Save or reconcile the shared value.");
                    fields[change.Key] = change.Value.S("value");
                }
            }
            return fields;
        }
        var lookups = new Dictionary<(string Table, string Column), ILookup<string, JsonObject>>();
        JsonObject? Find(string name, string column, string value, bool required = true)
        {
            token.ThrowIfCancellationRequested(); var file = Inside(project.Root, $"source/tables/{name}/records.json");
            if (!File.Exists(file)) { if (required) issues.Add($"Missing table: {name}."); return null; }
            if (!lookups.TryGetValue((name, column), out var lookup))
            {
                var rows = new List<JsonObject>();
                foreach (var row in ReadCached(file, token).AsArray().OfType<JsonObject>())
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(rules.Any(r => r.S("table") == name && r.S("record") == row.S("sourceId")) ? Effective(name, row) : row["fields"]!.AsObject());
                }
                lookup = rows.ToLookup(r => r.S(column), StringComparer.Ordinal); lookups[(name, column)] = lookup;
            }
            var matches = lookup[value].Take(2).ToArray(); Require(matches.Length <= 1, $"Ambiguous {name}/{column}: {value}.");
            var found = matches.FirstOrDefault();
            if (found == null && required) issues.Add($"Unresolved {name}/{column}: {value}."); return found;
        }
        var catalogs = new List<Dictionary<string, string>>();
        foreach (var file in Files(Inside(project.Root, "source/strings")).Where(f => Path.GetFileName(f) == "records.json"))
        {
            var data = ReadCached(file, token); var cached = cache[file];
            if (!cached.Locales.TryGetValue(locale + standard, out var translations))
            {
                translations = new(StringComparer.Ordinal);
                foreach (var row in data.AsArray())
                {
                    token.ThrowIfCancellationRequested(); var key = row.S("Key");
                    var full = row?["translations"]?.S(locale) ?? "";
                    var compact = standard ? row?["standardTranslations"]?[locale] : null;
                    if (compact != null) Require(row?["standardReviewedAgainst"].S(locale) == Hash(full), "Compact localization needs review: " + key);
                    var translation = compact?.GetValue<string>() ?? full;
                    if (translation.Length > 0) translations.TryAdd(key, Regex.Replace(translation, "ÿc.", ""));
                }
                if (cached.Locales.Count >= 4) cached.Locales.Clear();
                cached.Locales[locale + standard] = translations;
            }
            catalogs.Add(translations);
        }
        string Localize(string key) { foreach (var strings in catalogs) if (strings.TryGetValue(key, out var text)) return text; issues.Add($"Missing {locale} localization: {key}."); return key; }
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
        string FillPropertyTemplate(string template, IEnumerable<string> values, string parameter, string suffix = "")
        {
            var skill = SkillName(parameter, out var characterClass);
            template = template.Replace("[Skill Tab]", parameter.Length == 0 ? "Skill Tab" : "Skill Tab " + parameter)
                .Replace("[Skill]", skill).Replace("[Class]", characterClass);
            using var replacements = values.GetEnumerator();
            template = Regex.Replace(template, "#", _ => replacements.MoveNext() ? replacements.Current : "#");
            return template + suffix;
        }
        var item = Effective(table, record); bool set = table == "setitems";
        var rawName = item.S("index"); var code = item.S(set ? "item" : "code");
        if (code.Length == 0) return new(rawName.Length == 0 ? "Inactive row" : rawName, set, ["Inactive/header row · no base item code"], []);
        var name = Localize(rawName);
        var bases = new[] { "weapons", "armor", "misc" }.Select(t => (Table: t, Row: Find(t, "code", code, false))).Where(x => x.Row != null).ToArray();
        Require(bases.Length <= 1, "Ambiguous base item code: " + code);
        JsonObject? baseItem = bases.FirstOrDefault().Row;
        if (baseItem == null) issues.Add("Missing base item: " + code + " (weapons/armor/misc).");
        else
        {
            lines.Add(Localize(baseItem.S("namestr", code)));
            lines.Add("Required level: " + Math.Max(Number(item, "lvl req"), Number(baseItem, "levelreq")));
            lines.Add($"Base requirements: STR {Number(baseItem, "reqstr")} · DEX {Number(baseItem, "reqdex")}");
        }
        lines.Add($"{profile} · {locale} · character level {level} · roll ranges");
        var totals = new Dictionary<string, (decimal Low, decimal High)>(); bool calculable = true;
        string Range(decimal low, decimal high) => low == high ? low.ToString(CultureInfo.InvariantCulture) : $"({low.ToString(CultureInfo.InvariantCulture)}–{high.ToString(CultureInfo.InvariantCulture)})";
        string InlineRange(decimal low, decimal high) => low == high ? low.ToString(CultureInfo.InvariantCulture) : $"{low.ToString(CultureInfo.InvariantCulture)}–{high.ToString(CultureInfo.InvariantCulture)}";
        void Property(string prop, string min, string max, string parameter, bool main)
        {
            if (prop.Length == 0) return;
            var definition = Find("properties", "code", prop, false);
            void Unsupported(string why) { lines.Add($"{prop}: min={min}, max={max}, param={parameter}"); issues.Add($"{prop}: {why}"); if (main) calculable = false; }
            if (definition == null) { Unsupported("property definition unavailable."); return; }
            try
            {
                decimal authoredMin = min.Length == 0 ? 0 : decimal.Parse(min, CultureInfo.InvariantCulture), authoredMax = max.Length == 0 ? authoredMin : decimal.Parse(max, CultureInfo.InvariantCulture);
                decimal low = Math.Min(authoredMin, authoredMax), high = Math.Max(authoredMin, authoredMax);
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
                        ? [authoredMin == 0 ? "5" : InlineRange(authoredMin, authoredMin), InlineRange(authoredMax, authoredMax)]
                        : functionIds.Contains("19") ? [InlineRange(authoredMax, authoredMax), InlineRange(authoredMin, authoredMin), InlineRange(authoredMin, authoredMin)]
                        : functionIds.Contains("15") || functionIds.Contains("16") ? [InlineRange(low, low), InlineRange(high, high)]
                        : [InlineRange(low, high)];
                    var suffix = functionIds.Contains("18") ? " (varies with in-game time)" : "";
                    lines.Add(FillPropertyTemplate(authoredTooltip, replacements, parameter, suffix));
                    return;
                }
                if (functionIds.All(displayFunctions.Contains))
                {
                    lines.Add(parameter.Length > 0 ? $"{prop}: {parameter}" : $"{prop}: {InlineRange(low, high)}");
                    return;
                }
                decimal tooltipLow = low, tooltipHigh = high;
                foreach (var entry in functions.SelectMany(slot => definition.S("func" + slot) == "7" ? new[] { (Slot: slot, Minimum: true), (Slot: slot, Minimum: false) } : new[] { (Slot: slot, Minimum: false) }))
                {
                    int slot = entry.Slot;
                    var function = definition.S("func" + slot); string stat = definition.S("stat" + slot);
                    if (displayFunctions.Contains(function)) continue; // A second ordinary slot supplies the visible stat.
                    Require(definition.S("set" + slot) is "" or "0" && definition.S("val" + slot) is "" or "0", "Property set/constant-value behavior needs a specialized handler.");
                    decimal a = low, b = high;
                    if (function == "17")
                    {
                        if (parameter.Length == 0)
                        {
                            lines.Add(authoredTooltip.Length > 0 ? FillPropertyTemplate(authoredTooltip, [InlineRange(low, high)], parameter) : $"{prop}: {InlineRange(low, high)}");
                            return;
                        }
                        var scaling = Find("itemstatcost", "Stat", stat);
                        Require(scaling != null, "Missing level-scaling stat metadata.");
                        a = b = decimal.Parse(parameter, CultureInfo.InvariantCulture);
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
                        string value = Range(a, b), plus = a >= 0 ? "+" : "";
                        if (desc == "19")
                        {
                            var placeholders = Regex.Matches(label, @"%[+]?d").Count;
                            Require(placeholders <= 1, "Expected no more than one numeric localization placeholder.");
                            if (b < 0) label = label.Replace("-%d", "%d");
                            if (placeholders == 1) label = Regex.Replace(label, @"%[+]?d", m => (m.Value.Contains('+') ? plus : "") + value).Replace("%%", "%");
                        }
                        else if (desc is "1" or "2" or "3" or "4" or "5")
                        {
                            string number = desc switch { "1" => plus + value, "2" => value + "%", "4" => plus + value + "%", "5" => Range(a * 100 / 128, b * 100 / 128) + "%", _ => value };
                            label = cost.S("descval") switch { "1" => number + " " + label, "2" => label + " " + number, _ => label };
                        }
                        else label = stat + ": " + value;
                        lines.Add(label + (function == "17" ? $" (at level {level})" : ""));
                    }
                }
                if (authoredTooltip.Length > 0) lines.Add(FillPropertyTemplate(authoredTooltip, [InlineRange(tooltipLow, tooltipHigh)], parameter, functionIds.Contains("17") ? $" (at level {level})" : ""));
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or InvalidDataException or OverflowException) { Unsupported(e.Message); }
        }
        for (int i = 1; i <= 12; i++) Property(item.S("prop" + i), item.S("min" + i), item.S("max" + i), item.S("par" + i), true);
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
                    lines.Add("Required " + requirement.Item1 + ": " + Range(Adjust(requirements.Low), Adjust(requirements.High)));
                }
                var ed = totals.GetValueOrDefault("item_maxdamage_percent");
                var minEd = totals.GetValueOrDefault("item_mindamage_percent");
                var minAdd = totals.GetValueOrDefault("mindamage"); var maxAdd = totals.GetValueOrDefault("maxdamage");
                foreach (var damage in new[] { ("One-hand", "mindam", "maxdam"), ("Two-hand", "2handmindam", "2handmaxdam"), ("Throw", "minmisdam", "maxmisdam") })
                    if (baseItem.S(damage.Item3).Length > 0)
                    {
                        decimal Base(string key) => Math.Floor(Number(baseItem, key) * eth);
                        lines.Add($"{damage.Item1} physical damage: min {Range(Math.Floor(Base(damage.Item2) * (100 + minEd.Low) / 100) + minAdd.Low, Math.Floor(Base(damage.Item2) * (100 + minEd.High) / 100) + minAdd.High)} / max {Range(Math.Floor(Base(damage.Item3) * (100 + ed.Low) / 100) + maxAdd.Low, Math.Floor(Base(damage.Item3) * (100 + ed.High) / 100) + maxAdd.High)}");
                    }
                if (baseItem.S("maxac").Length > 0)
                {
                    var defense = totals.GetValueOrDefault("item_armor_percent"); var flat = totals.GetValueOrDefault("armorclass");
                    decimal low = Number(baseItem, "minac"), high = Number(baseItem, "maxac");
                    if (defense.Low > 0) low = high + 1; if (defense.High > 0) high++;
                    lines.Add("Defense: " + Range(Math.Floor(Math.Floor(low * eth) * (100 + defense.Low) / 100) + flat.Low, Math.Floor(Math.Floor(high * eth) * (100 + defense.High) / 100) + flat.High));
                }
                lines.Add("Listed-property subtotals; excludes character bonuses, automods/staffmods, sockets, upgrades and set activation.");
            }
        }
        if (set)
        {
            lines.Add("— " + Localize(item.S("set")) + " —");
            for (int i = 1; i <= 5; i++) foreach (var suffix in new[] { "a", "b" })
            {
                string n = i + suffix;
                if (item.S("aprop" + n).Length == 0) continue;
                lines.Add(item.S("add func") == "2" ? $"Item bonus with {i + 1} set pieces:" : $"Conditional item bonus {n} (add func {item.S("add func")}):");
                Property(item.S("aprop" + n), item.S("amin" + n), item.S("amax" + n), item.S("apar" + n), false);
            }
            var setRow = Find("sets", "index", item.S("set"));
            if (setRow != null)
            {
                for (int i = 2; i <= 5; i++) foreach (var suffix in new[] { "a", "b" })
                {
                    string n = i + suffix; if (setRow.S("PCode" + n).Length == 0) continue;
                    lines.Add($"Set bonus with {i} pieces:"); Property(setRow.S("PCode" + n), setRow.S("PMin" + n), setRow.S("PMax" + n), setRow.S("PParam" + n), false);
                }
                lines.Add("Full set bonuses:");
                for (int i = 1; i <= 8; i++) Property(setRow.S("FCode" + i), setRow.S("FMin" + i), setRow.S("FMax" + i), setRow.S("FParam" + i), false);
            }
        }
        if (issues.Count > 0) lines.Add("Requirements shown are authored item/base values; unsupported effects may change them.");
        return new(name, set, lines.ToArray(), issues.Distinct().ToArray());
    }
    private static decimal Number(JsonObject row, string field) => row.S(field).Length == 0 ? 0 : decimal.Parse(row.S(field), CultureInfo.InvariantCulture);
}
