using System.Text.RegularExpressions;

namespace ModStudio.Core;

/// <summary>
/// Explains one cell of a row without reading any other table: the function a function column's value selects and the
/// fields it reads, the functions that read this field, and what each code of a calc formula stands for. Cheap enough to
/// run on every selection change.
/// </summary>
public static class FieldInsight
{
    public static IReadOnlyList<string> Describe(string table, IReadOnlyList<string> columns, Func<string, string> cell, string column)
    {
        var lines = new List<string>();
        var value = cell(column).Trim();
        if (FunctionGuide.IsFunctionColumn(table, column) && value is not ("" or "0"))
        {
            var function = FunctionGuide.Describe(table, column, value);
            if (function == null) lines.Add($"{column} {value} is not in the data guide's function list.");
            else
            {
                lines.Add(function.Title + (function.Supplemental ? " (Studio note; not in the data guide)" : ""));
                if (function.Summary.Length > 0) lines.Add(function.Summary);
                var fields = FunctionGuide.Fields(table, column, function, columns);
                if (fields.Length > 0) lines.Add("Reads " + string.Join(" · ", fields.Select(f => cell(f).Length > 0 ? $"{f} = {cell(f)}" : $"{f} (empty)")));
            }
        }
        var readers = FunctionGuide.Readers(FunctionGuide.ForRow(table, columns, cell));
        if (readers.TryGetValue(column, out var uses)) lines.Add("Read by " + string.Join(", ", uses.Select(u => u.Function.Title)));
        if (value.Length > 0 && IsCalc(table, column) && !long.TryParse(value, out _))
        {
            var warnings = new List<string>();
            if (!Calc.TryParse(value, out var node, out var error, warnings)) lines.Add("⚠ Formula error: " + error);
            else
            {
                lines.AddRange(warnings.Select(w => "⚠ " + w));
                var glossary = Glossary(table, node!, cell).ToArray();
                if (glossary.Length > 0) { lines.Add("Formula codes:"); lines.AddRange(glossary.Select(g => "  " + g)); }
                if (table is "skills" or "skilldesc" or "missiles") lines.Add($"Evaluated values are in the {(table == "missiles" ? "Missile" : "Skill")} Preview tab.");
            }
        }
        return lines;
    }

    /// <summary>A short label for the Row Editor: the selected function's name, or the functions that read the field.</summary>
    public static string Hint(string table, string column, string value, IReadOnlyDictionary<string, List<FunctionUse>> readers)
    {
        if (FunctionGuide.Describe(table, column, value) is { } function) return "→ " + (function.Name.Length > 0 ? function.Name : function.Title);
        return readers.TryGetValue(column, out var uses) ? "read by " + string.Join(", ", uses.Select(u => u.Function.Column + " " + u.Function.Code)) : "";
    }

    public static bool IsCalc(string table, string column) => ColumnGuide.Find(table, column)?.Type == "parse" && table is "skills" or "skilldesc" or "missiles";

    private static IEnumerable<string> Glossary(string table, CalcNode node, Func<string, string> cell)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool missile = table == "missiles";
        foreach (var part in Calc.Walk(node))
        {
            switch (part)
            {
                case CalcCode code when seen.Add(code.Code):
                    var meaning = missile ? MissileCode(code.Code) : SkillCode(code.Code);
                    // In skills.txt the parameters are on this very row, so their values can be shown too.
                    var inputs = table is "skills" or "missiles" ? Regex.Matches(code.Code + " " + meaning, @"\b(par[1-9]|pa[0-9]{2}|cpa[1-5]|hpa[1-3]|chp[1-3]|dpa[1-2])\b").Select(m => m.Value).Distinct()
                        .Select(p => (Column: ParameterColumn(p, missile), p)).Where(x => x.Column.Length > 0).Select(x => $"{x.Column} = {(cell(x.Column) is { Length: > 0 } v ? v : "0")}").ToArray() : [];
                    yield return $"{code.Code} — {meaning ?? "not a known " + (missile ? "misscalc" : "skillcalc") + " code"}" + (inputs.Length > 0 ? $" ({string.Join(", ", inputs)})" : "");
                    break;
                case CalcReference reference when seen.Add(reference.ToString()):
                    yield return reference.ToString() + " — " + reference.Function switch
                    {
                        "skill" when reference.Path[0] is "blvl" or "lvl" => $"the character's {(reference.Path[0] == "blvl" ? "hard points" : "level")} in {reference.Name}; set it in the preview",
                        "skill" => $"{reference.Path[0]} of {reference.Name} at the character's level in it",
                        "sksrc" => $"{reference.Path[0]} of {reference.Name} on the unit that created this missile; set its level in the preview",
                        "sklvl" => $"{reference.Path[1]} of {reference.Name} evaluated at level {reference.Path[0]}",
                        "miss" => $"{reference.Path[0]} of missile {reference.Name} at this level",
                        "stat" => $"the unit's {reference.Name} stat ({reference.Path[0]})",
                        _ => "lookup"
                    };
                    break;
                case CalcCall call when seen.Add(call.Function + "()"):
                    yield return call.Function switch { "rand" => "rand(a, b) — a random number from a to b", "min" => "min(…) — the smallest argument", _ => "max(…) — the largest argument" };
                    break;
            }
        }
    }
    private static string ParameterColumn(string code, bool missile)
    {
        var match = Regex.Match(code, "^(par|pa|cpa|hpa|chp|dpa)([0-9]+)$");
        if (!match.Success) return "";
        return match.Groups[1].Value switch
        {
            "par" or "pa" => "Param" + int.Parse(match.Groups[2].Value),
            "cpa" when missile => "CltParam" + match.Groups[2].Value, "hpa" when missile => "sHitPar" + match.Groups[2].Value,
            "chp" when missile => "cHitPar" + match.Groups[2].Value, "dpa" when missile => "dParam" + match.Groups[2].Value, _ => ""
        };
    }

    private static readonly Dictionary<string, (string A, string B)> SkillPairs = new()
    {
        ["12"] = ("par1", "par2"), ["34"] = ("par3", "par4"), ["56"] = ("par5", "par6"), ["78"] = ("par7", "par8"), ["91"] = ("par9", "pa10"),
        ["21"] = ("pa11", "pa12"), ["43"] = ("pa13", "pa14"), ["65"] = ("pa15", "pa16"), ["87"] = ("pa17", "pa18"), ["92"] = ("pa19", "pa20")
    };
    /// <summary>skillcalc.txt, as the base game describes each code.</summary>
    public static string? SkillCode(string code)
    {
        var match = Regex.Match(code, "^(ln|dm)([0-9]{2})$");
        if (match.Success && SkillPairs.TryGetValue(match.Groups[2].Value, out var p))
            return match.Groups[1].Value == "ln" ? $"{p.A} + (lvl - 1) * {p.B}" : $"((110 * lvl) * ({p.B} - {p.A})) / (100 * (lvl + 6)) + {p.A}, diminishing from {p.A} toward {p.B}";
        match = Regex.Match(code, "^(?:par([1-9])|pa([0-9]{2}))$");
        if (match.Success) return $"the Param{(match.Groups[1].Success ? match.Groups[1].Value : int.Parse(match.Groups[2].Value).ToString())} field value";
        match = Regex.Match(code, "^m([123])(en|ex|el|rn|eo|ey|nm|xm)$|^me(3)([oy])$");
        if (match.Success)
        {
            var slot = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
            var part = match.Groups[2].Success ? match.Groups[2].Value : "e" + match.Groups[4].Value;
            return part switch
            {
                "en" => "elemental damage min", "ex" => "elemental damage max", "el" => "elemental length", "rn" => "range",
                "eo" => "elemental damage min (256ths)", "ey" => "elemental damage max (256ths)", "nm" => "elemental damage min with elemental mastery", _ => "elemental damage max with elemental mastery"
            } + $" of skilldesc descmissile{slot}";
        }
        match = Regex.Match(code, "^(?:clc([0-9])|ast([1-6])|pst([1-9])|ps(1[0-4]))$");
        if (match.Success)
            return match.Groups[1].Success ? $"the calc{(match.Groups[1].Value == "0" ? "10" : match.Groups[1].Value)} field value"
                : match.Groups[2].Success ? $"the aurastatcalc{match.Groups[2].Value} field value"
                : $"the passivecalc{(match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value)} field value";
        return code switch
        {
            "lvl" => "the current skill level (with bonuses)", "blvl" => "the base skill level (hard points)", "ulvl" => "the caster's character level",
            "edmn" => "elemental damage min", "edmx" => "elemental damage max", "edln" => "elemental length in frames",
            "edns" => "elemental damage min (256ths)", "edxs" => "elemental damage max (256ths)",
            "enma" => "elemental damage min with elemental mastery", "exma" => "elemental damage max with elemental mastery", "edma" => "elemental length with elemental mastery",
            "enms" => "elemental damage min with elemental mastery (256ths)", "exms" => "elemental damage max with elemental mastery (256ths)",
            "pnma" => "physical damage min (skill only)", "pxma" => "physical damage max (skill only)", "pnms" => "physical damage min (256ths)", "pxms" => "physical damage max (256ths)",
            "toht" => "attack rating bonus (ToHit)", "mana" => "mana cost", "usmc" => "mana cost (256ths)", "mps" => "mana cost per second",
            "math" => "attack rating mastery", "madm" => "physical damage mastery", "macr" => "critical hit mastery", "mael" => "elemental damage mastery",
            "mapi" => "throwing pierce mastery", "manc" => "throwing no-consume mastery", "mair" => "item requirements mastery", "sctk" => "skill channeling tick",
            "len" => "the auralencalc field value", "rng" => "the aurarangecalc field value", "pets" => "the petmax field value", "skpt" => "the skpoints field value",
            _ => null
        };
    }
    /// <summary>misscalc.txt, as the base game describes each code.</summary>
    public static string? MissileCode(string code)
    {
        var curves = new Dictionary<string, (string A, string B)>
        {
            ["sl12"] = ("par1", "par2"), ["sd12"] = ("par1", "par2"), ["sl34"] = ("par3", "par4"), ["sd34"] = ("par3", "par4"),
            ["cl12"] = ("cpa1", "cpa2"), ["cd12"] = ("cpa1", "cpa2"), ["cl34"] = ("cpa3", "cpa4"), ["cd34"] = ("cpa3", "cpa4"),
            ["shl1"] = ("hpa1", "hpa2"), ["shd1"] = ("hpa1", "hpa2"), ["chl1"] = ("chp1", "chp2"), ["chd1"] = ("chp1", "chp2"),
            ["dl12"] = ("dpa1", "dpa2"), ["dd12"] = ("dpa1", "dpa2")
        };
        if (curves.TryGetValue(code, out var p))
            return code[1] is 'l' || code[2] is 'l' ? $"{p.A} + (lvl - 1) * {p.B}" : $"((110 * lvl) * ({p.B} - {p.A})) / (100 * (lvl + 6)) + {p.A}, diminishing from {p.A} toward {p.B}";
        var match = Regex.Match(code, "^(par|cpa|hpa|chp|dpa)([1-5])$");
        if (match.Success)
            return $"the {match.Groups[1].Value switch { "par" => "Param", "cpa" => "CltParam", "hpa" => "sHitPar", "chp" => "cHitPar", _ => "dParam" }}{match.Groups[2].Value} field value";
        return code switch
        {
            "lvl" => "the current missile level (the level of the skill that fired it)",
            "edmn" => "elemental damage min", "edmx" => "elemental damage max", "edln" => "elemental length in frames",
            "edns" => "elemental damage min (256ths)", "edxs" => "elemental damage max (256ths)",
            "damn" => "physical damage min", "damx" => "physical damage max", "dmns" => "physical damage min (256ths)", "dmxs" => "physical damage max (256ths)",
            "rang" => "the Range field value", "rad" => "the Radius field value", "ccl1" => "the CltCalc1 field value", "scl1" => "the SrvCalc1 field value",
            "fnum" => "the missile's current frame number",
            _ => null
        };
    }
}
