using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>The calc language, what its codes mean in skill and missile rows, and the numbered-function guide.</summary>
internal static class CalcTests
{
    private sealed class Constants(Dictionary<string, long> codes) : ICalcScope
    {
        public long Code(string code) => codes.TryGetValue(code, out var value) ? value : throw new InvalidDataException("Unknown " + code);
        public long Reference(CalcReference reference) => reference.Path[^1] == "blvl" ? 7 : 0;
        public long Random(long low, long high) => low;
    }

    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        Language(check, throws);
        Scopes(root, check, throws);
        Functions(check);
        Insight(check);
        VanillaSweep(check);
    }

    private static void Language(Action<bool, string> check, Action<Action, string> throws)
    {
        var scope = new Constants(new() { ["lvl"] = 5, ["par8"] = 16 });
        long Eval(string text) => Calc.Evaluate(Calc.Parse(text), scope);
        check(Eval("1 + 2 * 3") == 7 && Eval("(1 + 2) * 3") == 9 && Eval("10 - 4 - 3") == 3, "Calc arithmetic follows precedence and left associativity");
        check(Eval("7 / 2") == 3 && Eval("-7 / 2") == -3 && Eval("5 / 0") == 0, "Calc division is integer, truncates toward zero and gives 0 for division by zero");
        check(Eval("lvl > 4 ? 10 : 20") == 10 && Eval("0 ? 1 : 0 ? 2 : 3") == 3 && Eval("(lvl >= 5) + (lvl == 5) + (lvl != 5) + (lvl <= 4)") == 2,
            "Calc comparisons give 1 or 0 and conditionals nest to the right");
        check(Eval("min(5, 3, 9)") == 3 && Eval("max(1, lvl)") == 5 && Eval("rand(2, 9)") == 2, "Calc min, max and rand take their arguments");
        check(Eval("(skill('Fire Ball'.blvl)+skill('Meteor'.blvl))*par8") == 224, "Calc lookups and codes combine the way vanilla synergy formulas do");
        var reference = (CalcReference)Calc.Parse("sklvl('Holy Fire'.ln56.edmn)");
        check(reference.Function == "sklvl" && reference.Name == "Holy Fire" && reference.Path.SequenceEqual(["ln56", "edmn"]), "sklvl() takes a level and a code after the quoted skill name");
        check(Calc.Walk(Calc.Parse("min(ln12, skill('X'.blvl)) + 1")).OfType<CalcCode>().Single().Code == "ln12", "Walk visits codes inside function arguments");
        foreach (var (text, why) in new[] {
            ("1 +", "a dangling operator"), ("(1 2)", "two values without an operator"), ("min(1 2)", "arguments without a comma"), ("foo(1)", "an unknown function"), ("skill(Fire.blvl)", "an unquoted lookup name"),
            ("skill('Fire Ball')", "a lookup without a code"), ("rand(1)", "rand with one argument"), ("", "an empty calculation") })
            throws(() => Calc.Parse(text), "Calc parser rejects " + why);
        check(!Calc.TryParse("lvl +* 2", out _, out var error) && error.Contains("column"), "Calc parse errors say where they happened: " + error);
        // Vanilla D2R ships formulas like these, and the game runs them.
        var warnings = new List<string>();
        check(Calc.Evaluate(Calc.Parse("((1 + 2) * (3", warnings), scope) == 9 && warnings.Single().StartsWith("2 closing parentheses are missing"), "Closing parentheses missing at the end are supplied with a warning");
        warnings.Clear();
        check(Calc.Evaluate(Calc.Parse("-6.25", warnings), scope) == -6 && warnings.Single().Contains("6.25 counts as 6"), "A decimal counts as its whole part with a warning");
        check(Eval("\"min(lvl,24)\"") == 5, "A spreadsheet-quoted formula reads as the formula inside");
    }

    private static void Scopes(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "calc-scopes"), "test", "Test"); Directory.CreateDirectory(project.Root);
        JsonObject Row(string id, params string[] values) { var fields = new JsonObject(); for (int i = 0; i < values.Length; i += 2) fields[values[i]] = values[i + 1]; return new() { ["sourceId"] = id, ["fields"] = fields }; }
        void Write(string relative, JsonNode value) { var file = Path.Combine(project.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value.ToJsonString(Pretty)); }
        void Table(string name, params JsonObject[] rows) => Write("source/tables/" + name + ".json", new JsonObject { ["schema"] = new JsonObject { ["name"] = name }, ["records"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()) });
        Write("source/strings/test.json", new JsonObject { ["schema"] = new JsonObject { ["category"] = "test" }, ["records"] = new JsonArray() });
        Table("skills",
            Row("main", "skill", "Main", "skilldesc", "main", "Param1", "10", "Param2", "20", "Param3", "3", "Param8", "16", "Param12", "4",
                "HitShift", "8", "EType", "fire", "EMin", "10", "EMinLev1", "2", "EMax", "20", "EMaxLev1", "4", "ELen", "50", "ELevLen1", "25",
                "mana", "8", "lvlmana", "2", "manashift", "8", "ToHit", "20", "LevToHit", "5",
                "calc1", "ln12 + clc2", "calc2", "par3 * 2", "calc3", "clc4", "calc4", "clc3", "auralencalc", "ln34 * 25",
                "EDmgSymPerCalc", "skill('Other'.blvl)*par8"),
            Row("other", "skill", "Other", "Param1", "1", "Param2", "2"));
        Table("skilldesc", Row("main", "skilldesc", "main", "descmissile1", "bolt"));
        Table("missiles", Row("bolt", "Missile", "bolt", "HitShift", "8", "EMin", "5", "MinELev1", "1", "EMax", "9", "MaxELev1", "1", "Range", "40", "Param1", "3", "Param2", "2", "SrvCalc1", "sl12 * lvl"));
        var issues = new List<string>();
        var data = new PreviewTables().Open(project, "standard", "enUS", issues, default);
        JsonObject Fields(string table, int index) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(project.Root, $"source/tables/{table}.json")))!["records"]![index]!["fields"]!;
        var main = Fields("skills", 0); var bolt = Fields("missiles", 0);
        CalcContext Context(int other = 0, long strength = 0) => new(data, new CalcAssumptions
        {
            SkillLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Other"] = other },
            Stats = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["strength"] = strength }
        }, default);
        var calc = Context();
        var at5 = calc.Skill(main, 5);
        check(at5.Code("ln12") == 90 && at5.Code("dm12") == 15 && at5.Code("par8") == 16 && at5.Code("pa12") == 4, "Skill codes read the row's parameters with the skillcalc ln/dm formulas");
        check(at5.Field("calc1") == 96 && at5.Code("clc1") == 96 && at5.Code("len") == 75, "Calc fields read other calc fields through clc# and len");
        check(at5.Code("edmn") == 18 && at5.Code("edmx") == 36 && at5.Code("edns") == 18 * 256 && at5.Code("edln") == 150, "Elemental codes follow the tiered curve and HitShift");
        check(at5.Code("mana") == 16 && at5.Code("usmc") == 16 * 256 && at5.Code("toht") == 40, "Mana and attack rating codes follow their per-level fields");
        check(at5.Code("m1en") == 9 && at5.Code("m1rn") == 40, "descmissile codes read the linked missile at the skill's level");
        check(calc.Inputs.Single(i => i.Kind == "skill").Name == "Other", "Reading a synergy reports the other skill as an input: " + string.Join(", ", calc.Inputs.Select(i => i.Label)));
        var synergy = Context(other: 3).Skill(main, 5);
        check(synergy.Field("EDmgSymPerCalc") == 48 && synergy.Code("edmn") == 26 && synergy.Code("edmx") == 53, "Synergy percentages raise elemental damage at the assumed skill level");
        check(synergy.Evaluate("skill('Other'.ln12)") == 5 && Context().Skill(main, 5).Evaluate("skill('Other'.ln12)") == 0,
            "Another skill's codes run at its assumed level, and read 0 while it is unlearned");
        check(synergy.Evaluate("skill('Main'.blvl) + sklvl('Other'.3.ln12) + sklvl('Main'.lvl.par1)") == 5 + 5 + 10, "skill() of the previewed skill uses its own level; sklvl() evaluates at the given level");
        check(synergy.Evaluate("miss('bolt'.edmn) + miss('bolt'.rang)") == 9 + 40 && Context(strength: 50).Skill(main, 1).Evaluate("stat('strength'.accr) + ulvl") == 50 + CalcAssumptions.DefaultCharacterLevel,
            "miss(), stat() and ulvl read missiles, assumed stats and the assumed character level");
        var missileScope = calc.Missile(bolt, 4, calc.Skill(main, 4));
        check(missileScope.Code("sl12") == 9 && missileScope.Code("sd12") == 3 + (110 * 4 * -1) / (100 * 10) && missileScope.Field("SrvCalc1") == 36 && missileScope.Code("scl1") == 36,
            "Missile codes read the missile's own parameters at the firing skill's level");
        throws(() => calc.Skill(main, 5).Field("calc3"), "A calc that reads itself is refused instead of looping");
        throws(() => calc.Skill(main, 5).Evaluate("skill('Nobody'.blvl)"), "A lookup of a missing skill is an error");
        throws(() => calc.Skill(main, 5).Code("zzzz"), "An unknown skillcalc code is an error");
        throws(() => missileScope.Code("par8"), "Missile scope refuses skill-only codes");
        calc.Skill(main, 1).Evaluate("rand(3, 9) + madm");
        check(calc.Notes.Any(n => n.Contains("rand")) && calc.Notes.Any(n => n.Contains("Masteries")), "Values the preview has to pick are named in its notes");
    }

    private static void Functions(Action<bool, string> check)
    {
        var attack = FunctionGuide.Describe("skills", "srvdofunc", "1");
        check(attack?.Name == "DoAttack (Server)" && attack.Title == "srvdofunc 1 · DoAttack (Server)", "Function codes decode through the data guide: " + attack?.Title);
        var skillColumns = new[] { "skill", "srvdofunc", "srvprgfunc1", "srvoverlay", "aurastate", "auratargetstate", "auralencalc", "Param1" };
        var damage = FunctionGuide.Describe("skills", "srvdofunc", "2")!;
        check(FunctionGuide.Fields("skills", "srvdofunc", damage, skillColumns).SequenceEqual(["srvoverlay", "aurastate", "auratargetstate", "auralencalc"]), "A function's parameters map to the row's columns");
        check(FunctionGuide.IsFunctionColumn("skills", "srvprgfunc1") && FunctionGuide.Describe("skills", "srvprgfunc1", "2")?.Name == "DoApplyDamage", "Progressive functions borrow the Server Do function list");
        check(!FunctionGuide.IsFunctionColumn("skills", "Param1") && FunctionGuide.Describe("skills", "srvdofunc", "99999") == null && FunctionGuide.Describe("skills", "srvdofunc", "") == null,
            "Ordinary columns, unknown codes and empty cells decode to nothing");
        var descColumns = new[] { "dsc2line1", "dsc2texta1", "dsc2textb1", "dsc2calca1", "dsc2calcb1", "descline2", "desctexta2", "desctextb2" };
        var ratio = FunctionGuide.Describe("skilldesc", "dsc2line1", "36")!;
        check(FunctionGuide.Fields("skilldesc", "dsc2line1", ratio, descColumns).SequenceEqual(["dsc2texta1", "dsc2textb1", "dsc2calca1", "dsc2calcb1"]),
            "dsc2line functions read their own dsc2 text and calc columns");
        var textInText = FunctionGuide.Describe("skilldesc", "descline2", "78")!;
        check(textInText.Supplemental && FunctionGuide.Fields("skilldesc", "descline2", textInText, descColumns).SequenceEqual(["desctexta2", "desctextb2"]),
            "descline functions missing from the guide are filled in and marked as Studio notes");
        check(FunctionGuide.Describe("itemstatcost", "descfunc", "1")?.Name == "Plus or Minus" && FunctionGuide.Describe("itemstatcost", "op", "2")?.Name == "By Level Operator",
            "Guide pages without a Name column take the name from the description");
        var op = FunctionGuide.Describe("itemstatcost", "op", "2")!;
        check(FunctionGuide.Fields("itemstatcost", "op", op, ["Stat", "op", "op param", "op base", "op stat1", "op stat2", "op stat3"]).SequenceEqual(["op param", "op base", "op stat1", "op stat2", "op stat3"]),
            "An unnumbered function column reads every numbered parameter column");
        var func = FunctionGuide.Describe("properties", "func2", "1")!;
        check(FunctionGuide.Fields("properties", "func2", func, ["func1", "stat1", "set1", "func2", "stat2", "set2"]).SequenceEqual(["stat2", "set2"]), "A numbered function column reads its own numbered parameters");
        check(FunctionGuide.Describe("itemstatcost", "dgrpfunc", "19") is { } group && FunctionGuide.Fields("itemstatcost", "dgrpfunc", group, ["dgrpstrpos", "dgrpstrneg", "dgrpstr2", "descstrpos"]).SequenceEqual(["dgrpstrpos", "dgrpstrneg", "dgrpstr2"]),
            "Group descriptions use the descfunc list with their own dgrp columns");
        var row = new Dictionary<string, string> { ["srvdofunc"] = "2", ["auralencalc"] = "ln12", ["aurastate"] = "burning", ["Param1"] = "4", ["skill"] = "Test" };
        var uses = FunctionGuide.ForRow("skills", [.. row.Keys], c => row.GetValueOrDefault(c, ""));
        var readers = FunctionGuide.Readers(uses);
        check(uses.Count == 1 && readers["auralencalc"].Single().Function.Code == "2" && !readers.ContainsKey("Param1"), "Readers map each parameter to the functions that read it");
        check(FunctionGuide.PossibleHints("skills", [.. row.Keys]) is var hints && hints.IsSupersetOf(["srvdofunc", "auralencalc", "aurastate"]) && !hints.Contains("skill"), "Possible hints cover function columns and every parameter a function can read");
    }

    private static void Insight(Action<bool, string> check)
    {
        var row = new Dictionary<string, string> { ["skill"] = "Test", ["srvdofunc"] = "2", ["auralencalc"] = "ln12 * 25 + skill('Other'.blvl)", ["aurastate"] = "burning", ["srvoverlay"] = "", ["Param1"] = "10", ["Param2"] = "2", ["calc1"] = "ln12 +" };
        string[] columns = [.. row.Keys];
        string Cell(string c) => row.GetValueOrDefault(c, "");
        var function = FieldInsight.Describe("skills", columns, Cell, "srvdofunc");
        check(function[0] == "srvdofunc 2 · DoApplyDamage" && function.Any(l => l.StartsWith("Reads ") && l.Contains("aurastate = burning") && l.Contains("srvoverlay (empty)")),
            "Field insight names the selected function and the fields it reads: " + string.Join(" | ", function));
        var calc = FieldInsight.Describe("skills", columns, Cell, "auralencalc");
        check(calc.Contains("Read by srvdofunc 2 · DoApplyDamage") && calc.Any(l => l.Contains("ln12 — par1 + (lvl - 1) * par2 (Param1 = 10, Param2 = 2)"))
            && calc.Any(l => l.Contains("skill('Other'.blvl) — the character's hard points in Other")),
            "Field insight explains a formula's codes with the row's values: " + string.Join(" | ", calc));
        check(FieldInsight.Describe("skills", columns, Cell, "calc1").Any(l => l.StartsWith("⚠ Formula error")), "Field insight reports formula errors");
        check(FieldInsight.Describe("skills", columns, Cell, "skill").Count == 0, "Ordinary fields have nothing to add");
        var readers = FunctionGuide.Readers(FunctionGuide.ForRow("skills", columns, Cell));
        check(FieldInsight.Hint("skills", "srvdofunc", "2", readers) == "→ DoApplyDamage" && FieldInsight.Hint("skills", "aurastate", "burning", readers) == "read by srvdofunc 2",
            "Row Editor hints name the selected function or the functions that read a field");
        check(FieldInsight.Describe("missiles", ["SrvCalc1", "Param1"], c => c == "SrvCalc1" ? "sl12 * 2" : "4", "SrvCalc1").Any(l => l.Contains("sl12 — par1 + (lvl - 1) * par2 (Param1 = 4")),
            "Missile formulas are explained with misscalc codes");
    }

    /// <summary>With MODSTUDIO_BASE_EXCEL pointing at extracted global/excel files, every vanilla calc must parse and every code must be known.</summary>
    private static void VanillaSweep(Action<bool, string> check)
    {
        var folder = Environment.GetEnvironmentVariable("MODSTUDIO_BASE_EXCEL");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var failures = new List<string>(); var warnings = new List<string>(); int parsed = 0;
        foreach (var table in new[] { "skills", "skilldesc", "missiles" })
        {
            var lines = File.ReadAllLines(Path.Combine(folder, table + ".txt"));
            var header = lines[0].Split('\t');
            var calcColumns = Enumerable.Range(0, header.Length).Where(i => FieldInsight.IsCalc(table, header[i])).ToArray();
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Split('\t');
                foreach (var i in calcColumns)
                {
                    if (i >= cells.Length || cells[i].Trim() is not { Length: > 0 } text || long.TryParse(text, out _)) continue;
                    var before = warnings.Count;
                    if (!Calc.TryParse(text, out var node, out var error, warnings)) { failures.Add($"{table}/{header[i]}: {text}: {error}"); continue; }
                    for (int w = before; w < warnings.Count; w++) warnings[w] = $"{table}/{header[i]}: {text}: {warnings[w]}";
                    parsed++;
                    foreach (var code in Calc.Walk(node!).OfType<CalcCode>())
                        if ((table == "missiles" ? FieldInsight.MissileCode(code.Code) : FieldInsight.SkillCode(code.Code)) == null) failures.Add($"{table}/{header[i]}: unknown code {code.Code} in {text}");
                }
            }
        }
        check(failures.Count == 0, $"Every vanilla calc parses ({parsed}, {warnings.Count} with a game-tolerated slip) and uses known codes: " + string.Join(" | ", failures.Take(10)));
        foreach (var warning in warnings) Console.WriteLine("  vanilla calc warning: " + warning);
    }
}
