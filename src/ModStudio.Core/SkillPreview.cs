using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>One level of a skill as the game would present it. Empty strings mean the skill does not use that field.</summary>
public record SkillLevelPreview(int Level, string Mana, string Physical, string Elemental, string Duration, string AttackRating);

public record SkillPreviewResult(string Name, string CharacterClass, int MaxLevel, string[] Lines, SkillLevelPreview[] Levels, string[] Descriptions, string[] Issues);

/// <summary>
/// Worker-owned resolver for a skills.txt row: the level curve the authored fields produce, level 1 through the row's
/// own <c>maxlvl</c>. Reads authored data only; never changes records or starts a build.
/// </summary>
/// <remarks>
/// Mana, damage and elemental length are the fields the game derives arithmetically from the row, so they are computed:
/// mana and damage are stored in 256ths and shifted (<c>manashift</c>, <c>HitShift</c>), and each grows by a different
/// amount inside the level tiers 2–8, 9–16, 17–22, 23–28 and 29+ (durations use 2–8, 9–16 and 17+).
/// The skilldesc tooltip lines are listed with their authored calculations rather than evaluated: those expressions read
/// other skills' levels (synergies, masteries) and character state, which no preview of a single row can know.
/// </remarks>
public sealed class SkillPreviewResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();
    /// <summary>Levels past which each per-level field switches to its next tier.</summary>
    private static readonly int[] DamageTiers = [8, 16, 22, 28, int.MaxValue];
    private static readonly int[] LengthTiers = [8, 16, int.MaxValue];
    public const int DefaultMaxLevel = 20;

    public SkillPreviewResult Resolve(ModProject project, JsonObject record, string profile, string locale, CancellationToken token)
    {
        var issues = new List<string>(); var lines = new List<string>(); var descriptions = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        var skill = data.Effective("skills", record);
        var id = skill.S("skill");
        var description = skill.S("skilldesc").Length > 0 ? data.Find("skilldesc", "skilldesc", skill.S("skilldesc"), false) : null;
        if (skill.S("skilldesc").Length > 0 && description == null) issues.Add($"Unresolved skilldesc: {skill.S("skilldesc")}.");
        var nameKey = description.S("str name");
        var name = nameKey.Length > 0 ? data.Localize(nameKey) : id.Length > 0 ? id : "Unnamed skill";
        var characterClass = skill.S("charclass") switch
        {
            "ama" => "Amazon", "sor" => "Sorceress", "nec" => "Necromancer", "pal" => "Paladin",
            "bar" => "Barbarian", "dru" => "Druid", "ass" => "Assassin", "" => "Shared / monster", _ => skill.S("charclass")
        };
        if (id.Length == 0) return new(name, characterClass, 0, ["Inactive/header row · no skill id"], [], [], []);

        int maxLevel = (int)Number(skill, "maxlvl");
        bool authoredMax = maxLevel > 0;
        if (!authoredMax) maxLevel = DefaultMaxLevel;
        Require(maxLevel <= 99, $"maxlvl {maxLevel} is out of range; skill levels stop at 99.");

        lines.Add($"{characterClass} · {id}" + (skill.S("skilldesc").Length > 0 ? $" · skilldesc {skill.S("skilldesc")}" : ""));
        lines.Add(authoredMax
            ? $"Levels 1–{maxLevel} from skill points (maxlvl {maxLevel}); items can raise it further"
            : $"Levels 1–{maxLevel} (maxlvl is not set on this row, so the usual {DefaultMaxLevel} is shown)");
        if (skill.S("reqlevel").Length > 0) lines.Add($"Required character level: {Text(Number(skill, "reqlevel"))}");
        var prerequisites = new[] { "reqskill1", "reqskill2", "reqskill3" }.Select(f => skill.S(f)).Where(v => v.Length > 0).ToArray();
        if (prerequisites.Length > 0) lines.Add("Requires: " + string.Join(", ", prerequisites));
        if (description != null)
        {
            var longKey = description.S("str long");
            if (longKey.Length > 0) lines.Add(data.Localize(longKey));
            if (description.S("SkillPage").Length > 0) lines.Add($"Skill tree page {description.S("SkillPage")}, row {description.S("SkillRow")}, column {description.S("SkillColumn")}");
        }
        lines.Add($"{profile} · {locale}");

        decimal manaShift = Pow2(Number(skill, "manashift")), hitShift = Pow2(Number(skill, "HitShift"));
        decimal baseMana = Number(skill, "mana"), perLevelMana = Number(skill, "lvlmana"), minimumMana = Number(skill, "minmana");
        bool hasMana = skill.S("mana").Length > 0 || skill.S("lvlmana").Length > 0;
        var physical = (Min: Curve(skill, "MinDam", "MinLevDam", DamageTiers), Max: Curve(skill, "MaxDam", "MaxLevDam", DamageTiers));
        var elemental = (Min: Curve(skill, "EMin", "EMinLev", DamageTiers), Max: Curve(skill, "EMax", "EMaxLev", DamageTiers));
        var length = Curve(skill, "ELen", "ELevLen", LengthTiers);
        var element = skill.S("EType");
        bool hasPhysical = physical.Min.Authored || physical.Max.Authored;
        bool hasElemental = element.Length > 0 && (elemental.Min.Authored || elemental.Max.Authored);
        bool hasLength = element.Length > 0 && length.Authored;
        bool hasAttackRating = skill.S("ToHit").Length > 0 || skill.S("LevToHit").Length > 0;
        decimal toHit = Number(skill, "ToHit"), perLevelToHit = Number(skill, "LevToHit");
        if (element.Length > 0 && !hasElemental && !hasLength) issues.Add($"EType is {element} but no elemental damage or length is authored.");
        if (elemental.Min.Authored && element.Length == 0) issues.Add("Elemental damage is authored without an EType, so the game ignores it.");

        var levels = new List<SkillLevelPreview>();
        for (int level = 1; level <= maxLevel; level++)
        {
            token.ThrowIfCancellationRequested();
            // Mana and damage are stored in 256ths; their shift scales them back to the numbers the game shows.
            var mana = Math.Max(minimumMana, (baseMana + perLevelMana * (level - 1)) * manaShift / 256);
            levels.Add(new(level,
                hasMana ? Text(Round(mana)) : "",
                hasPhysical ? Damage(physical.Min.At(level), physical.Max.At(level), hitShift) : "",
                hasElemental ? Damage(elemental.Min.At(level), elemental.Max.At(level), hitShift) + " " + element : "",
                hasLength ? Text(Round(length.At(level) / 25)) + " sec" : "",
                hasAttackRating ? "+" + Text(toHit + perLevelToHit * (level - 1)) + "%" : ""));
        }

        foreach (var (prefix, count, label) in new[] { ("desc", 6, "descline"), ("dsc2", 5, "dsc2line"), ("dsc3", 7, "dsc3line") })
        {
            if (description == null) break;
            var group = new List<string>();
            for (int i = 1; i <= count; i++)
            {
                var line = description.S($"{prefix}line{i}"); if (line.Length == 0) continue;
                var texts = new[] { description.S($"{prefix}texta{i}"), description.S($"{prefix}textb{i}") }.Where(t => t.Length > 0).Select(t => $"{t} = \"{data.Localize(t)}\"");
                var calcs = new[] { description.S($"{prefix}calca{i}"), description.S($"{prefix}calcb{i}") }.Where(c => c.Length > 0);
                group.Add($"  {i}. function {line}" + (texts.Any() ? " · " + string.Join(" · ", texts) : "") + (calcs.Any() ? " · calc " + string.Join(" / ", calcs) : ""));
            }
            if (group.Count == 0) continue;
            descriptions.Add(label); descriptions.AddRange(group);
        }
        if (descriptions.Count > 0) descriptions.Add("Tooltip calculations are listed as authored, not evaluated: they read other skills' levels and character state.");

        var synergies = new[] { "EDmgSymPerCalc", "DmgSymPerCalc", "ELenSymPerCalc" }.Where(f => skill.S(f).Length > 0).ToArray();
        if (synergies.Length > 0) lines.Add("Excludes synergy scaling: " + string.Join(", ", synergies.Select(f => $"{f} = {skill.S(f)}")));
        lines.Add("Base skill values only; masteries, +skills, difficulty resistances and character bonuses are not applied.");
        return new(name, characterClass, maxLevel, lines.ToArray(), levels.ToArray(), descriptions.ToArray(), issues.Distinct().ToArray());
    }

    /// <summary>A base field plus its per-level fields, which each apply inside one tier of levels.</summary>
    private readonly record struct LevelCurve(decimal Base, decimal[] PerLevel, int[] Tiers, bool Authored)
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
    private static LevelCurve Curve(JsonObject skill, string baseField, string perLevelField, int[] tiers)
    {
        var perLevel = Enumerable.Range(1, tiers.Length).Select(i => Number(skill, perLevelField + i)).ToArray();
        bool authored = skill.S(baseField).Length > 0 || Enumerable.Range(1, tiers.Length).Any(i => skill.S(perLevelField + i).Length > 0);
        return new(Number(skill, baseField), perLevel, tiers, authored);
    }
    /// <summary>Damage as the game shows it: scaled out of 256ths and rounded down, the way the tooltip does.</summary>
    private static string Damage(decimal min, decimal max, decimal shift)
    {
        decimal low = Math.Floor(min * shift / 256), high = Math.Floor(max * shift / 256);
        return low == high ? Text(low) : $"{Text(low)}–{Text(high)}";
    }
    private static decimal Pow2(decimal exponent)
    {
        Require(exponent is >= 0 and <= 16, $"Shift {Text(exponent)} is out of range; skills shift by 0–16.");
        return (decimal)Math.Pow(2, (double)exponent);
    }
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.ToZero);
    private static string Text(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static decimal Number(JsonObject row, string field)
    {
        var text = row.S(field); if (text.Length == 0) return 0;
        Require(decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value), $"{field} is not a number: {text}.");
        return value;
    }
}
