using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class SkillPreviewTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "skill-previews"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Fields(params string[] values) { var obj = new JsonObject(); for (int i = 0; i < values.Length; i += 2) obj[values[i]] = values[i + 1]; return obj; }
        JsonObject Row(string id, params string[] fields) => new() { ["sourceId"] = id, ["fields"] = Fields(fields) };
        void Write(string relative, JsonNode value) { var file = Path.Combine(project.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value.ToJsonString(Pretty)); }
        void Table(string name, params JsonObject[] rows) => Write("source/tables/" + name + ".json", new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) });
        Table("skilldesc",
            Row("bolt", "skilldesc", "fire bolt", "str name", "skillname36", "str long", "skilllong36", "SkillPage", "1", "SkillRow", "1", "SkillColumn", "2",
                "descline1", "75", "desctexta1", "StrSkill5", "desccalca1", "enma", "desccalcb1", "exma",
                "dsc2line1", "36", "dsc2texta1", "StrSkill3", "dsc2calca1", "usmc"),
            Row("plain", "skilldesc", "plain", "str name", "skillname99"));
        Write("source/strings/test.json", new JsonObject
        {
            ["schema"] = new JsonObject { ["category"] = "test" },
            ["records"] = new JsonArray(new[] { ("skillname36", "Fire Bolt"), ("skilllong36", "Hurls a bolt of fire."), ("StrSkill5", "Fire Damage: %d to %d"), ("StrSkill3", "Mana Cost: %d"), ("skillname99", "Plain Skill") }
                .Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray())
        });
        // Vanilla Fire Bolt: 2.5 mana and 3–6 fire damage at level 1, growing to 45–60 at level 20.
        var bolt = Row("firebolt", "skill", "Fire Bolt", "charclass", "sor", "skilldesc", "fire bolt", "maxlvl", "20", "reqlevel", "1",
            "mana", "5", "lvlmana", "0", "manashift", "7", "minmana", "1", "HitShift", "7",
            "EType", "fire", "EMin", "6", "EMinLev1", "3", "EMinLev2", "4", "EMinLev3", "8", "EMinLev4", "18", "EMinLev5", "54",
            "EMax", "12", "EMaxLev1", "3", "EMaxLev2", "6", "EMaxLev3", "10", "EMaxLev4", "20", "EMaxLev5", "56",
            "EDmgSymPerCalc", "(skill('Fire Ball'.blvl))*par8");
        var resolver = new SkillPreviewResolver();
        SkillPreviewResult Preview(JsonObject? row = null, string profile = "standard", string locale = "enUS") => resolver.Resolve(project, row ?? bolt, profile, locale, default);
        var result = Preview();
        check(result.Name == "Fire Bolt" && result.CharacterClass == "Sorceress" && result.MaxLevel == 20 && result.Levels.Length == 20,
            "Skill preview names the skill and covers exactly the levels its maxlvl allows");
        check(result.Levels[0].Mana == "2.5" && result.Levels[0].Elemental == "3–6 fire", "Level 1 mana and elemental damage match the shifted 256ths the game uses");
        check(result.Levels[19].Elemental == "45–60 fire", "Per-level damage follows the 2–8 / 9–16 / 17–22 tiers");
        // Level 8 closes the first tier (6+3×7 / 12+3×7) and level 16 the second (+4×8 / +6×8), each halved by HitShift 7.
        check(result.Levels[7].Elemental == "13–16 fire" && result.Levels[15].Elemental == "29–40 fire", "Damage tier boundaries land on levels 8 and 16");
        check(result.Levels.All(l => l.Physical == "" && l.Duration == "" && l.AttackRating == ""), "Fields the skill does not author stay empty instead of showing zeros");
        check(result.Lines.Contains("Hurls a bolt of fire.") && result.Lines.Any(l => l.Contains("Levels 1–20") && l.Contains("maxlvl 20")), "Header carries the localized description and the authored level cap");
        check(result.Lines.Any(l => l.StartsWith("Excludes synergy scaling") && l.Contains("EDmgSymPerCalc")), "Synergy calculations are named as excluded rather than silently ignored");
        check(result.Descriptions.Contains("descline") && result.Descriptions.Any(d => d.Contains("StrSkill5") && d.Contains("Fire Damage: %d to %d") && d.Contains("calc enma / exma")),
            "Tooltip lines list their localized text and authored calculations");
        check(result.Issues.Length == 0, "A complete skill row previews without incomplete warnings: " + string.Join(" ", result.Issues));

        var mana = Row("mana", "skill", "Mana", "skilldesc", "plain", "maxlvl", "5", "mana", "20", "lvlmana", "-4", "manashift", "8", "minmana", "6");
        check(Preview(mana).Levels.Select(l => l.Mana).SequenceEqual(["20", "16", "12", "8", "6"]), "Falling mana costs stop at minmana");
        var physical = Row("phys", "skill", "Bash", "charclass", "bar", "skilldesc", "plain", "maxlvl", "3", "HitShift", "8",
            "MinDam", "10", "MinLevDam1", "2", "MaxDam", "20", "MaxLevDam1", "4", "ToHit", "50", "LevToHit", "10");
        var hit = Preview(physical);
        check(hit.Levels[2].Physical == "14–28" && hit.Levels[2].AttackRating == "+70%" && hit.CharacterClass == "Barbarian", "Physical damage and attack rating follow their per-level fields");
        var cold = Row("cold", "skill", "Ice Bolt", "skilldesc", "plain", "maxlvl", "2", "HitShift", "8", "EType", "cold", "EMin", "6", "EMax", "10", "ELen", "150", "ELevLen1", "35");
        check(Preview(cold).Levels[1].Duration == "7.4 sec" && Preview(cold).Levels[1].Elemental == "6–10 cold", "Elemental length converts frames to seconds on its own tiers");

        var noMax = Row("nomax", "skill", "Attack", "skilldesc", "plain");
        var fallback = Preview(noMax);
        check(fallback.MaxLevel == SkillPreviewResolver.DefaultMaxLevel && fallback.Lines.Any(l => l.Contains("maxlvl is not set")), "A row without maxlvl previews the usual 20 levels and says so");
        var header = Row("header", "skill", "");
        check(Preview(header).Levels.Length == 0 && Preview(header).Lines.Contains("Inactive/header row · no skill id"), "Inactive separator rows do not report incomplete skill previews");
        var strayElement = Row("stray", "skill", "Stray", "skilldesc", "plain", "maxlvl", "1", "EMin", "5", "EMax", "5");
        check(Preview(strayElement).Issues.Any(i => i.Contains("without an EType")), "Elemental damage the game would ignore is reported");

        var warm = Preview(); check(warm.Levels.Select(l => l.Elemental).SequenceEqual(result.Levels.Select(l => l.Elemental)), "Cached skill resolution preserves preview output");
        Write("compatibility/test/profile.json", new JsonObject { ["tableOverrides"] = new JsonArray("skill.json") });
        Write("compatibility/test/skill.json", new JsonObject { ["table"] = "skills", ["record"] = "firebolt", ["changes"] = new JsonObject { ["mana"] = new JsonObject { ["expect"] = "5", ["value"] = "10" } } });
        check(Preview(profile: "test").Levels[0].Mana == "5", "Skill preview applies selected profile overrides");
        check(bolt["fields"].S("mana") == "5", "Preview leaves selected source unchanged");
        check(Preview(locale: "missing").Issues.Any(i => i.Contains("localization")), "Missing localization is explicit instead of silently showing another locale");
        var broken = (JsonObject)bolt.DeepClone(); broken["fields"]!["manashift"] = "40";
        throws(() => Preview(broken), "An out-of-range shift produces a useful preview error");
        broken = (JsonObject)bolt.DeepClone(); broken["fields"]!["mana"] = "lots";
        throws(() => Preview(broken), "A non-numeric skill field produces a useful preview error");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        throws(() => resolver.Resolve(project, bolt, "standard", "enUS", cancel.Token), "Canceled skill resolution stops before producing results");
    }
}
