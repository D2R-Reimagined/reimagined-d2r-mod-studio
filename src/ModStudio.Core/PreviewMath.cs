using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// A labelled block of a preview card; empty sections are never produced. <paramref name="BeforeLevels"/> marks the blocks
/// that belong above a level table dozens of rows long.
/// </summary>
public record PreviewSection(string Title, string[] Lines, bool BeforeLevels = false);

/// <summary>
/// The arithmetic skills and missiles share. Damage and mana are stored in 256ths and scaled by a shift field, and a
/// base field plus its per-level fields grows by a different amount inside each tier of levels.
/// </summary>
internal static class PreviewMath
{
    /// <summary>Levels that close each tier of a five-step per-level field (2–8, 9–16, 17–22, 23–28, 29+).</summary>
    public static readonly int[] DamageTiers = [8, 16, 22, 28, int.MaxValue];
    /// <summary>Levels that close each tier of a three-step per-level field (2–8, 9–16, 17+).</summary>
    public static readonly int[] LengthTiers = [8, 16, int.MaxValue];

    /// <summary>A base field plus its per-level fields, which each apply inside one tier of levels.</summary>
    public readonly record struct LevelCurve(decimal Base, decimal[] PerLevel, int[] Tiers, bool Authored)
    {
        public decimal At(int level)
        {
            decimal total = Base;
            for (int tier = 0, from = 2; tier < PerLevel.Length && from <= level; from = Tiers[tier] + 1, tier++)
            {
                int to = Math.Min(level, Tiers[tier]);
                if (to >= from) total += PerLevel[tier] * (to - from + 1);
            }
            return total;
        }
    }
    public static LevelCurve Curve(JsonObject row, string baseField, Func<int, string> perLevelField, int[] tiers)
    {
        var perLevel = Enumerable.Range(1, tiers.Length).Select(i => Number(row, perLevelField(i))).ToArray();
        bool authored = row.S(baseField).Length > 0 || Enumerable.Range(1, tiers.Length).Any(i => row.S(perLevelField(i)).Length > 0);
        return new(Number(row, baseField), perLevel, tiers, authored);
    }
    /// <summary>Damage as the game shows it: scaled out of 256ths by its shift and rounded down, the way the tooltip does.</summary>
    public static string Damage(decimal min, decimal max, decimal shift)
    {
        decimal low = Math.Floor(min * shift / 256), high = Math.Floor(max * shift / 256);
        return low == high ? Text(low) : $"{Text(low)}–{Text(high)}";
    }
    public static decimal Pow2(decimal exponent)
    {
        Require(exponent is >= 0 and <= 16, $"Shift {Text(exponent)} is out of range; 0–16 is what the game allows.");
        return (decimal)Math.Pow(2, (double)exponent);
    }
    /// <summary>Frames as seconds, at the game's 25 frames per second.</summary>
    public static string Seconds(decimal frames) => Text(Round(frames / 25)) + " sec";
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.ToZero);
    public static string Text(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    /// <summary>A share as a percentage, culture-independent (the "P" format adds a space in some cultures).</summary>
    public static string Percent(double share, int decimals = 1) => (share * 100).ToString(decimals > 0 ? "0." + new string('0', decimals) : "0", CultureInfo.InvariantCulture) + "%";
    public static decimal Number(JsonObject row, string field)
    {
        var text = row.S(field); if (text.Length == 0) return 0;
        Require(decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value), $"{field} is not a number: {text}.");
        return value;
    }
    public static bool Flag(JsonObject row, string field) => row.S(field) is not ("" or "0");

    /// <summary>Calc columns worth explaining: authored, and more than a plain number.</summary>
    public static IEnumerable<string> CalcColumns(string table, JsonObject row) =>
        row.Where(f => f.Value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String && v.GetValue<string>().Trim() is { Length: > 0 } text
                && !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                && ColumnGuide.Find(table, f.Key)?.Type == "parse")
            .Select(f => f.Key);

    /// <summary>
    /// "calc1 = 28 (level 1: 10 → level 20: 48) · ln12*2": a calc column at the chosen level, how it moves across the level
    /// range, and the authored formula. Frame counts also read as seconds.
    /// </summary>
    public static string DescribeCalc(string column, string expression, Func<int, long> at, int level, int maxLevel, bool frames)
    {
        string Show(long value) => frames ? $"{value} ({Seconds(value)})" : value.ToString(CultureInfo.InvariantCulture);
        long first = at(1), last = at(maxLevel), current = at(level);
        var range = first == last ? "same at every level" : $"level 1: {Show(first)} → level {maxLevel}: {Show(last)}";
        return $"{column} = {Show(current)}  ({range}) · {expression}";
    }

    /// <summary>"srvdofunc 27 · Teleport — summary" and, indented under it, the authored fields the function reads.</summary>
    public static IEnumerable<string> DescribeFunctions(IEnumerable<FunctionUse> uses, JsonObject row, int summaryLength = 180)
    {
        foreach (var use in uses)
        {
            var summary = use.Function.Summary.Replace('\n', ' ');
            if (summary.Length > summaryLength) summary = summary[..summaryLength].TrimEnd() + "…";
            yield return use.Function.Title + (summary.Length > 0 ? " — " + summary : "") + (use.Function.Supplemental ? " (Studio note)" : "");
            var read = use.Fields.Where(f => row.S(f).Length > 0).Select(f => $"{f} = {row.S(f)}").ToArray();
            if (read.Length > 0) yield return "    reads " + string.Join(" · ", read);
        }
    }
}
