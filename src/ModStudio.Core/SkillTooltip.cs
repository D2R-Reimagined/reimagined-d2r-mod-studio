using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// One rendered tooltip line. <paramref name="Approximate"/> marks a function the preview cannot fully evaluate;
/// <paramref name="Cells"/> are the skilldesc cells it was built from.
/// </summary>
public sealed record TooltipLine(string Text, string Function, bool Approximate = false, CellLink[]? Cells = null);

/// <summary>A skill tooltip as the skill tree shows it: pinned lines, the current and next level, then synergies.</summary>
public sealed record SkillTooltip(int Level, TooltipLine[] Pinned, TooltipLine[] Current, TooltipLine[] Next, TooltipLine[] Synergies)
{
    public IEnumerable<string> Lines(string currentHeading, string nextHeading)
    {
        static string Show(TooltipLine l) => PreviewText.Mark(l.Text, l.Cells ?? []) + (l.Approximate ? "  (approximate)" : "");
        foreach (var line in Pinned) yield return Show(line);
        if (Current.Length > 0) { yield return currentHeading; foreach (var line in Current) yield return "  " + Show(line); }
        if (Next.Length > 0) { yield return nextHeading; foreach (var line in Next) yield return "  " + Show(line); }
        foreach (var line in Synergies) yield return Show(line);
    }
}

/// <summary>
/// Renders skilldesc.txt tooltip lines: each descline/dsc2line/dsc3line function formats its strings with its evaluated
/// calculations. The strings are printf formats (<c>%d</c>, <c>%+d%%</c>, <c>%s</c>), filled in the order each function
/// passes its values.
/// </summary>
public static class SkillTooltips
{
    public static SkillTooltip Render(CalcContext calc, JsonObject skill, JsonObject description, int level, int maxLevel, List<string> issues)
    {
        var scope = calc.Skill(skill, level);
        TooltipLine[] Group(string prefix, int count, SkillCalcScope at) =>
            Enumerable.Range(1, count).Select(i => Line(calc, description, prefix, i, at, issues) is { } line ? line with { Cells = Cells(calc, description, prefix, i) } : null).OfType<TooltipLine>().ToArray();
        return new(level,
            Group("dsc2", 5, scope),
            Group("desc", 6, scope),
            level < maxLevel ? Group("desc", 6, calc.Skill(skill, level + 1)) : [],
            Group("dsc3", 7, scope));
    }

    private static TooltipLine? Line(CalcContext calc, JsonObject description, string prefix, int index, SkillCalcScope scope, List<string> issues)
    {
        var function = description.S($"{prefix}line{index}").Trim();
        if (function is "" or "0") return null;
        var label = $"{prefix}line{index} {function}";
        string Str(string part) => description.S($"{prefix}text{part}{index}") is { Length: > 0 } key ? calc.Data.Localize(key) : "";
        // The formulas live on the skilldesc row but run as the skill's own: lvl, par8 and edmn are the skill's.
        decimal Value(string part) => description.S($"{prefix}calc{part}{index}").Trim() is { Length: > 0 } text ? scope.Evaluate(text) : 0;
        try
        {
            string a = Str("a"), b = Str("b");
            decimal Ratio(decimal divisor = 1) { var bottom = Value("b"); return Value("a") / divisor / (bottom == 0 ? 1 : bottom); }
            string Plural(decimal value) => Format(value == 1 || b.Length == 0 ? a : b, [value]);
            TooltipLine Approximate(string text, string why) { calc.Note($"{prefix}line function {function}: {why}"); return new(text, label, true); }
            return function switch
            {
                "12" => new($"Duration: {Text(Round((decimal)scope.Code("len") / 25))} seconds", label),
                "13" => Approximate(Format(a, ["?"]), "the summoned monster's life comes from monstats and monlvl, which the skill preview does not read."),
                "34" => Approximate(Format(a, ["?", "?"]), "the summoned monster's damage comes from monstats and monlvl, which the skill preview does not read."),
                "56" => Approximate(Format(a, ["?"]), "the quantity is the book's or scroll's current charges."),
                "18" => new(a, label),
                "31" => new(Plural(Ratio(CurseDivisor(calc))), label),
                "36" => new(Plural(Ratio()), label),
                "40" => new(Format(a, [b]), label),
                "41" or "75" => new(Format(a, [Value("a"), Value("b")]), label),
                "74" => new(Format(a, [Value("a")]), label),
                "76" => new(Format(a, [b, Value("a")]), label),
                "77" => new(Format(a, [b, Value("a"), Value("b")]), label),
                "78" => new(Format(a, [b]), label),
                "79" => new(Format(a, [b, Ratio()]), label),
                _ => Approximate(Format(a, [.. (a.Contains("%s") ? new object[] { b } : Array.Empty<object>()), Value("a"), Value("b")]), "this function is not modeled; its strings and calculations are inserted in order.")
            };
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            issues.Add($"{prefix}line{index}: {e.Message}");
            return new($"⚠ {e.Message}", label, true);
        }
    }
    /// <summary>The skilldesc cells a tooltip line is built from: its function, strings and calculations.</summary>
    private static CellLink[] Cells(CalcContext calc, JsonObject description, string prefix, int index) =>
        [.. new[] { "line", "texta", "textb", "calca", "calcb" }.Select(part => $"{prefix}{part}{index}")
            .Where(c => description.S(c).Length > 0).Select(c => calc.Data.Cell(description, c)).OfType<CellLink>()];

    /// <summary>Function 31 divides by the difficulty's AiCurseDivisor; the preview shows Normal difficulty.</summary>
    private static decimal CurseDivisor(CalcContext calc)
    {
        calc.Note("Curse durations use Normal difficulty's AiCurseDivisor.");
        var normal = calc.Data.Rows("difficultylevels", false).FirstOrDefault();
        return normal != null && Number(normal, "AiCurseDivisor") is var divisor && divisor > 0 ? divisor : 1;
    }

    /// <summary>
    /// printf over the value kinds tooltips use: <c>%d</c>/<c>%i</c>/<c>%u</c> numbers (a <c>+</c> flag forces the sign),
    /// <c>%s</c> text, <c>%f</c> decimals and <c>%%</c>. A whole-number conversion given a fraction shows up to two decimals,
    /// as ratios such as "1.5 seconds" read in game. Missing values show as "?".
    /// </summary>
    public static string Format(string format, IReadOnlyList<object> values)
    {
        int next = 0;
        return Regex.Replace(format, @"%([-+ 0#]*)([0-9]*)(?:\.([0-9]+))?([diusf%])", m =>
        {
            char kind = m.Groups[4].Value[0];
            if (kind == '%') return "%";
            if (next >= values.Count) return "?";
            var value = values[next++];
            if (kind == 's') return value is decimal d ? Text(d) : value.ToString() ?? "";
            if (value is not decimal number) return value.ToString() ?? "";
            string text = kind == 'f'
                ? number.ToString("F" + (m.Groups[3].Success ? m.Groups[3].Value : "6"), CultureInfo.InvariantCulture)
                : Text(Math.Round(number, 2, MidpointRounding.ToZero));
            return m.Groups[1].Value.Contains('+') && number >= 0 ? "+" + text : text;
        });
    }
}
