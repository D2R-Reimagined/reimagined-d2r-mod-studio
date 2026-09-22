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
                "dsc2line1", "36", "dsc2texta1", "StrSkill3", "dsc2calca1", "usmc", "dsc2calcb1", "256",
                "dsc3line1", "40", "dsc3texta1", "Sksyn", "dsc3textb1", "skillname36", "dsc3calca1", "2",
                "dsc3line2", "76", "dsc3texta2", "Firedplev", "dsc3textb2", "skillname47", "dsc3calca2", "par8"),
            Row("plain", "skilldesc", "plain", "str name", "skillname99"));
        Write("source/strings/test.json", new JsonObject
        {
            ["schema"] = new JsonObject { ["category"] = "test" },
            ["records"] = new JsonArray(new[] { ("skillname36", "Fire Bolt"), ("skilllong36", "Hurls a bolt of fire."), ("StrSkill5", "Fire Damage: %d to %d"), ("StrSkill3", "Mana Cost: %d"), ("skillname99", "Plain Skill"),
                ("Sksyn", "%s Receives Bonuses From:"), ("Firedplev", "%s: %+d%% Fire Damage per Level"), ("skillname47", "Fire Ball") }
                .Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray())
        });
        // Vanilla Fire Bolt: 2.5 mana and 3–6 fire damage at level 1, growing to 45–60 at level 20.
        var bolt = Row("firebolt", "skill", "Fire Bolt", "charclass", "sor", "skilldesc", "fire bolt", "maxlvl", "20", "reqlevel", "1",
            "mana", "5", "lvlmana", "0", "manashift", "7", "minmana", "1", "HitShift", "7",
            "EType", "fire", "EMin", "6", "EMinLev1", "3", "EMinLev2", "4", "EMinLev3", "8", "EMinLev4", "18", "EMinLev5", "54",
            "EMax", "12", "EMaxLev1", "3", "EMaxLev2", "6", "EMaxLev3", "10", "EMaxLev4", "20", "EMaxLev5", "56",
            "Param8", "16", "EDmgSymPerCalc", "(skill('Fire Ball'.blvl))*par8", "srvdofunc", "2", "auralencalc", "ln12", "Param1", "25");
        Table("skills", bolt, Row("fireball", "skill", "Fire Ball", "maxlvl", "20"));
        var resolver = new SkillPreviewResolver();
        SkillPreviewResult Preview(JsonObject? row = null, string profile = "standard", string locale = "enUS", CalcPreviewOptions? options = null) => resolver.Resolve(project, row ?? bolt, profile, locale, default, options);
        string[] Section(SkillPreviewResult result, string title) => result.Sections.Single(s => s.Title == title).Lines;
        var result = Preview();
        check(result.Name == "Fire Bolt" && result.CharacterClass == "Sorceress" && result.MaxLevel == 20 && result.Levels.Length == 20,
            "Skill preview names the skill and covers exactly the levels its maxlvl allows");
        check(result.Levels[0].Mana == "2.5" && result.Levels[0].Elemental == "3–6 fire", "Level 1 mana and elemental damage match the shifted 256ths the game uses");
        check(result.Levels[19].Elemental == "45–60 fire", "Per-level damage follows the 2–8 / 9–16 / 17–22 tiers");
        // Level 8 closes the first tier (6+3×7 / 12+3×7) and level 16 the second (+4×8 / +6×8), each halved by HitShift 7.
        check(result.Levels[7].Elemental == "13–16 fire" && result.Levels[15].Elemental == "29–40 fire", "Damage tier boundaries land on levels 8 and 16");
        check(result.Levels.All(l => l.Physical == "" && l.Duration == "" && l.AttackRating == "" && l.Synergy == ""), "Fields the skill does not author stay empty instead of showing zeros");
        check(result.Lines.Contains("Hurls a bolt of fire.") && result.Lines.Any(l => l.Contains("Levels 1–20") && l.Contains("maxlvl 20")), "Header carries the localized description and the authored level cap");
        check(result.Lines.Any(l => l.StartsWith("Synergies: EDmgSymPerCalc") && l.Contains("set the other skills' levels")), "Synergies that add nothing yet say how to see them");
        var tooltip = Section(result, "Tooltip at level 1");
        check(tooltip.SequenceEqual(["Mana Cost: 2.5", "Current skill level: 1", "  Fire Damage: 3 to 6", "Next level: 2", "  Fire Damage: 4 to 7",
            "Fire Bolt Receives Bonuses From:", "Fire Ball: +16% Fire Damage per Level"]), "Tooltip lines are rendered with their calculations evaluated: " + string.Join(" | ", tooltip));
        check(result.Inputs.Single().Kind == "skill" && result.Inputs.Single().Name == "Fire Ball" && result.Inputs.Single().Value == 0, "The synergy skill is offered as an input");
        check(Section(result, "Calculations at level 1").Any(l => l.StartsWith("EDmgSymPerCalc = 0  (same at every level)")) && Section(result, "Calculations at level 1").Any(l => l.StartsWith("auralencalc = 25 (1 sec)")),
            "Calc columns are evaluated at the chosen level, frame counts also as seconds: " + string.Join(" | ", Section(result, "Calculations at level 1")));
        check(Section(result, "Functions")[0].StartsWith("srvdofunc 2 · DoApplyDamage — ") && Section(result, "Functions")[1] == "    reads auralencalc = ln12",
            "Functions are named from the data guide with the fields they read: " + string.Join(" | ", Section(result, "Functions")));
        var synergized = Preview(options: new(5, new CalcAssumptions { SkillLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Fire Ball"] = 10 } }));
        check(synergized.Level == 5 && Section(synergized, "Tooltip at level 5").Contains("  Fire Damage: 23 to 31") && Section(synergized, "Calculations at level 5").Any(l => l.StartsWith("EDmgSymPerCalc = 160")),
            "Assumed synergy levels feed the tooltip at the chosen level");
        check(synergized.Levels[0].Synergy == "+160%" && synergized.Levels[0].WithSynergy == "7–15 fire" && synergized.Levels[0].Elemental == "3–6 fire",
            $"The level table adds synergies next to the base damage: {synergized.Levels[0].Synergy} {synergized.Levels[0].WithSynergy}");
        check(Preview(options: new(80)).Level == 20, "The chosen level is clamped to the skill's range");
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
