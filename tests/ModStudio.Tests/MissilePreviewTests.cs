using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class MissilePreviewTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "missile-previews"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Fields(params string[] values) { var obj = new JsonObject(); for (int i = 0; i < values.Length; i += 2) obj[values[i]] = values[i + 1]; return obj; }
        JsonObject Row(string id, params string[] fields) => new() { ["sourceId"] = id, ["fields"] = Fields(fields) };
        void Write(string relative, JsonNode value) { var file = Path.Combine(project.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value.ToJsonString(Pretty)); }
        void Table(string name, params JsonObject[] rows) => Write("source/tables/" + name + ".json", new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) });
        Write("source/strings/test.json", new JsonObject { ["schema"] = new JsonObject { ["category"] = "test" }, ["records"] = new JsonArray() });
        // Fire Ball fires "fireball", which explodes into "fireballexp"; the explosion spawns a sub-missile that loops back.
        Table("skills",
            Row("fireball", "skill", "Fire Ball", "maxlvl", "25", "srvmissile", "fireball", "cltmissile", "fireball",
                "HitShift", "8", "MinDam", "10", "MaxDam", "20", "EType", "fire", "EMin", "30", "EMax", "50", "EMinLev1", "5", "EMaxLev1", "5", "Param8", "10"),
            Row("firebolt", "skill", "Fire Bolt"),
            Row("meteor", "skill", "Meteor", "maxlvl", "20", "srvmissilea", "fireball"),
            Row("other", "skill", "Other", "maxlvl", "30", "srvmissile", "elsewhere"));
        Table("missiles",
            Row("fireball", "Missile", "fireball", "HitShift", "7", "Vel", "18", "VelLev", "2", "MaxVel", "24", "Range", "100",
                "EType", "fire", "EMin", "12", "MinELev1", "13", "EMax", "28", "MaxELev1", "15", "ELen", "50", "ELevLen1", "25",
                "MinDamage", "4", "MaxDamage", "8", "CollideType", "3", "CollideKill", "1", "Pierce", "1", "KnockBack", "25",
                "SrcDamage", "64", "Half2HSrc", "1", "ExplosionMissile", "fireballexp", "CelFile", "fireball", "AnimLen", "8", "LoopAnim", "1",
                "Light", "5", "Red", "255", "Green", "128", "Blue", "0", "TravelSound", "fireball_travel",
                "pSrvDoFunc", "13", "pSrvHitFunc", "1", "sHitPar1", "3", "Param1", "40", "*param1 desc", "explosion radius", "SrvCalc1", "par1*2",
                "EDmgSymPerCalc", "skill('Fire Bolt'.blvl)*skill('Fire Ball'.par8)"),
            Row("fireballexp", "Missile", "fireballexp", "HitShift", "8", "Explosion", "1", "SubMissile1", "fireball"),
            Row("orphan", "Missile", "orphan", "HitShift", "8", "SubMissile1", "ghost"));
        var resolver = new MissilePreviewResolver();
        MissilePreviewResult Preview(string id = "fireball", string profile = "standard", string locale = "enUS", CalcPreviewOptions? options = null)
        {
            var record = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(project.Root, "source/tables/missiles.json")))!["records"]!.AsArray().First(r => r.S("sourceId") == id)!.DeepClone();
            return resolver.Resolve(project, record, profile, locale, default, options);
        }
        var result = Preview();
        check(result.Name == "fireball" && result.MaxLevel == 25 && result.Levels.Length == 25,
            $"Missile levels follow the highest maxlvl among the skills that fire it: {result.MaxLevel}");
        check(result.Levels[0].Elemental == "6–14 fire" && result.Levels[0].Physical == "2–4" && result.Levels[0].Duration == "2 sec",
            $"Level 1 damage and length use the missile's HitShift: {result.Levels[0].Elemental} / {result.Levels[0].Physical} / {result.Levels[0].Duration}");
        check(result.Levels[0].Velocity == "18" && result.Levels[3].Velocity == "24" && result.Levels[24].Velocity == "24", "Velocity grows per level and stops at MaxVel");
        var usedBy = result.Sections.Single(s => s.Title == "Used by").Lines;
        check(usedBy.Any(l => l.StartsWith("Fire Ball") && l.Contains("srvmissile, cltmissile") && l.Contains("maxlvl 25"))
            && usedBy.Any(l => l.StartsWith("Meteor") && l.Contains("srvmissilea"))
            && usedBy.Any(l => l.Contains("missile fireballexp") && l.Contains("SubMissile1")) && usedBy.Length == 3,
            "Used by lists every skill and missile that references this one, with the referencing columns");
        var spawns = result.Sections.Single(s => s.Title == "Spawns").Lines;
        check(spawns[0] == "ExplosionMissile → fireballexp" && spawns[1] == "  SubMissile1 → fireball (already shown)" && spawns.Length == 2,
            "The spawn chain is followed into sub-missiles and stops on a cycle: " + string.Join(" | ", spawns));
        var motion = result.Sections.Single(s => s.Title == "Motion").Lines;
        check(motion.Any(l => l == "Velocity: 18 px/frame, +2 per caster level, capped at 24") && motion.Any(l => l.Contains("Lifetime: 100 frames (4 sec)")) && motion.Any(l => l.Contains("about 1800 px")),
            "Motion reports velocity, lifetime in seconds and a constant-velocity reach");
        var behavior = result.Sections.Single(s => s.Title == "Behavior").Lines;
        check(behavior.Contains("Destroyed when it collides") && behavior.Contains("Pierce can apply") && behavior.Contains("Knockback chance: 25%")
            && behavior.Contains("Adds 50% of the caster's damage, halved with a two-handed weapon") && !behavior.Contains("Treated as an explosion"),
            "Behavior names the enabled flags only, and converts 128ths source damage to a percentage");
        var visuals = result.Sections.Single(s => s.Title == "Visuals and sound").Lines;
        check(visuals.Any(l => l.Contains("Graphics: fireball")) && visuals.Any(l => l.Contains("Animation: 8 frames") && l.Contains("looping")) && visuals.Any(l => l.Contains("RGB 255,128,0")),
            "Visual section reports the cel file, animation and light");
        var functions = result.Sections.Single(s => s.Title == "Functions and parameters").Lines;
        check(functions[0].StartsWith("pSrvDoFunc 13 · MissileBoneWallMaker — "), "Missile functions are named from the data guide: " + functions[0]);
        check(functions[1].StartsWith("pSrvHitFunc 1 · RadialFireDamage — ") && functions[2] == "    reads sHitPar1 = 3" && functions[3] == "Other parameters: Param1 = 40 (explosion radius)",
            "Each function lists the parameters it reads; the rest keep their authored descriptions: " + string.Join(" | ", functions));
        var calculations = result.Sections.Single(s => s.Title == "Calculations at level 1").Lines;
        check(calculations.Any(l => l.StartsWith("SrvCalc1 = 80  (same at every level) · par1*2")) && calculations.Any(l => l.StartsWith("EDmgSymPerCalc = 0")),
            "Missile calculations are evaluated: " + string.Join(" | ", calculations));
        check(result.Lines.Any(l => l.StartsWith("Synergies: EDmgSymPerCalc")) && result.Inputs.Single().Name == "Fire Bolt", "The missile's synergy skill is offered as an input");
        var synergized = Preview(options: new(1, new CalcAssumptions { SkillLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Fire Bolt"] = 5 } }));
        check(synergized.Levels[0].Synergy == "+50%" && synergized.Levels[0].WithSynergy == "9–21 fire" && synergized.Levels[0].Elemental == "6–14 fire",
            $"Missile synergies read the firing skill's parameters: {synergized.Levels[0].Synergy} {synergized.Levels[0].WithSynergy}");
        check(result.Sections.Single(s => s.Title == "Assumptions").Lines[0].Contains("level of Fire Ball"), "The missile says which skill's level its calculations run at");
        check(result.Issues.Length == 0, "A complete missile row previews without incomplete warnings: " + string.Join(" ", result.Issues));

        var explosion = Preview("fireballexp");
        check(explosion.MaxLevel == MissilePreviewResolver.DefaultMaxLevel && explosion.Lines.Any(l => l.Contains("no skill fires this missile directly")),
            "A missile only spawned by another previews the usual 20 levels and says why");
        check(explosion.Levels.All(l => l.Physical == "" && l.Elemental == "" && l.Velocity == ""), "A missile without damage or velocity shows no numbers for them");
        var orphan = Preview("orphan");
        check(orphan.Issues.Any(i => i.Contains("Nothing references this missile")) && orphan.Issues.Any(i => i.Contains("Unresolved missiles/Missile: ghost")),
            "Unreferenced missiles and missing spawn targets are reported: " + string.Join(" ", orphan.Issues));

        Write("compatibility/test/profile.json", new JsonObject { ["tableOverrides"] = new JsonArray("missile.json") });
        Write("compatibility/test/missile.json", new JsonObject { ["table"] = "missiles", ["record"] = "fireball", ["changes"] = new JsonObject { ["Vel"] = new JsonObject { ["expect"] = "18", ["value"] = "8" } } });
        check(Preview(profile: "test").Levels[0].Velocity == "8", "Missile preview applies selected profile overrides");
        // Skill = Fire Ball means the game ignores this missile's own 5–9 and uses the skill's 10–20 / 30–50 fire.
        var skillDamage = Row("borrowed", "Missile", "borrowed", "HitShift", "0", "MissileSkill", "1", "Skill", "Fire Ball", "MinDamage", "5", "MaxDamage", "9");
        var borrowed = resolver.Resolve(project, skillDamage, "standard", "enUS", default);
        check(borrowed.Lines.Any(l => l.Contains("MissileSkill = 1")) && borrowed.Lines.Any(l => l.Contains("Skill = Fire Ball") && l.Contains("not the missile's own fields")),
            "A missile that borrows a skill's damage says where the numbers come from");
        check(borrowed.Levels[0].Physical == "10–20" && borrowed.Levels[0].Elemental == "30–50 fire" && borrowed.Levels[1].Elemental == "35–55 fire",
            $"Borrowed damage follows the skill's own curve and shift: {borrowed.Levels[0].Physical} / {borrowed.Levels[0].Elemental}");
        check(borrowed.MaxLevel == 25 && borrowed.Lines.Any(l => l.Contains("from the maxlvl of Fire Ball")), $"A borrowed skill sets the level range when nothing fires the missile: {borrowed.MaxLevel}");
        var missingSkill = Row("bad", "Missile", "bad", "HitShift", "8", "Skill", "Nope");
        check(resolver.Resolve(project, missingSkill, "standard", "enUS", default).Issues.Any(i => i.Contains("Unresolved skills/skill: Nope")), "A missile pointing at a missing skill is reported");
        // Range and Radius are calculations in D2R data ("100+(lvl*25)"), read at the chosen level.
        var growing = resolver.Resolve(project, Row("growing", "Missile", "growing", "Range", "100+(lvl*25)", "Radius", "par1", "Param1", "4", "Vel", "10"), "standard", "enUS", default, new(3));
        var growingMotion = growing.Sections.Single(s => s.Title == "Motion").Lines;
        check(growingMotion.Contains("Lifetime: 175 frames (7 sec) at level 3") && growingMotion.Contains("Reach at level 3: about 1750 px, if it never collides")
            && growingMotion.Contains("Search/VFX radius: 4 sub-tiles at level 3"), "Calculated Range and Radius are evaluated at the chosen level: " + string.Join(" | ", growingMotion));
        var header = Row("blank", "Missile", "");
        check(resolver.Resolve(project, header, "standard", "enUS", default).Lines.Contains("Inactive/header row · no missile id"), "Inactive separator rows do not report incomplete missile previews");
        var brokenShift = Row("broken", "Missile", "broken", "HitShift", "40");
        throws(() => resolver.Resolve(project, brokenShift, "standard", "enUS", default), "An out-of-range shift produces a useful preview error");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        throws(() => resolver.Resolve(project, skillDamage, "standard", "enUS", cancel.Token), "Canceled missile resolution stops before producing results");
    }
}
