using System.Text.Json.Nodes;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// One level of a skill as the game would present it. Empty strings mean the skill does not use that field.
/// <paramref name="Synergy"/> and <paramref name="WithSynergy"/> are filled only when synergies add something at the
/// assumed skill levels.
/// </summary>
public record SkillLevelPreview(int Level, string Mana, string Physical, string Elemental, string Duration, string AttackRating, string Synergy = "", string WithSynergy = "");

/// <param name="Level">The level the tooltip and calculations are shown at, clamped to the skill's range.</param>
/// <param name="Inputs">The assumptions the calculations read (other skills' levels, stats, character level).</param>
/// <param name="ColumnSources">The skills.txt cells each level-table column is computed from, by column header.</param>
public record SkillPreviewResult(string Name, string CharacterClass, int MaxLevel, int Level, string[] Lines, PreviewSection[] Sections, SkillLevelPreview[] Levels, CalcInput[] Inputs, string[] Issues,
    IReadOnlyDictionary<string, CellLink[]>? ColumnSources = null)
{
    public PreviewText[] Text { get; } = PreviewText.Parse(Lines);
    public string[] Lines { get; } = PreviewText.Plain(Lines);
}

/// <summary>The level to show the tooltip and calculations at, and the values a single row cannot know.</summary>
public sealed record CalcPreviewOptions(int Level = 1, CalcAssumptions? Assumptions = null);

/// <summary>
/// Worker-owned resolver for a skills.txt row: the level curve the authored fields produce, level 1 through the row's
/// own <c>maxlvl</c>, the skilldesc tooltip with its calculations evaluated, and the numbered functions the row uses.
/// Reads authored data only; never changes records or starts a build.
/// </summary>
/// <remarks>
/// Mana and damage are stored in 256ths and shifted (<c>manashift</c>, <c>HitShift</c>), and each grows by a different
/// amount inside the level tiers 2–8, 9–16, 17–22, 23–28 and 29+ (durations use 2–8, 9–16 and 17+). Calculations
/// (<c>calc1</c>, <c>EDmgSymPerCalc</c>, the tooltip's <c>desccalca1</c>…) are evaluated by <see cref="Calc"/>; the
/// values they read from outside the row — other skills' levels, stats, the character's level — come from
/// <see cref="CalcAssumptions"/> and are reported back as <see cref="SkillPreviewResult.Inputs"/>.
/// </remarks>
public sealed class SkillPreviewResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();
    public const int DefaultMaxLevel = 20;
    /// <summary>skills.txt calc columns that count frames.</summary>
    private static readonly HashSet<string> FrameColumns = new(StringComparer.OrdinalIgnoreCase) { "auralencalc", "localdelay", "globaldelay", "perdelay" };

    public SkillPreviewResult Resolve(ModProject project, JsonObject record, string profile, string locale, CancellationToken token, CalcPreviewOptions? options = null)
    {
        options ??= new();
        var issues = new List<string>(); var lines = new List<string>(); var sections = new List<PreviewSection>();
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
        if (id.Length == 0) return new(name, characterClass, 0, 0, ["Inactive/header row · no skill id"], [], [], [], []);

        int maxLevel = (int)Number(skill, "maxlvl");
        bool authoredMax = maxLevel > 0;
        if (!authoredMax) maxLevel = DefaultMaxLevel;
        Require(maxLevel <= 99, $"maxlvl {maxLevel} is out of range; skill levels stop at 99.");
        int level = Math.Clamp(options.Level, 1, maxLevel);
        var calc = new CalcContext(data, options.Assumptions ?? new(), token);

        string Link(string text, params string[] columns) => data.Link(text, skill, columns);
        lines.Add($"{Link(characterClass, "charclass")} · {Link(id, "skill")}" + (skill.S("skilldesc").Length > 0 ? $" · {Link("skilldesc " + skill.S("skilldesc"), "skilldesc")}" : ""));
        lines.Add(authoredMax
            ? $"Levels 1–{maxLevel} from skill points ({Link($"maxlvl {maxLevel}", "maxlvl")}); items can raise it further"
            : $"Levels 1–{maxLevel} ({Link("maxlvl is not set", "maxlvl")} on this row, so the usual {DefaultMaxLevel} is shown)");
        if (skill.S("reqlevel").Length > 0) lines.Add($"Required character level: {Link(Text(Number(skill, "reqlevel")), "reqlevel")}");
        var prerequisites = new[] { "reqskill1", "reqskill2", "reqskill3" }.Where(f => skill.S(f).Length > 0).Select(f => Link(skill.S(f), f)).ToArray();
        if (prerequisites.Length > 0) lines.Add("Requires: " + string.Join(", ", prerequisites));
        if (description != null)
        {
            var longKey = description.S("str long");
            if (longKey.Length > 0) lines.Add(data.Link(data.Localize(longKey), description, "str long"));
            if (description.S("SkillPage").Length > 0)
                lines.Add($"Skill tree {data.Link($"page {description.S("SkillPage")}", description, "SkillPage")}, {data.Link($"row {description.S("SkillRow")}", description, "SkillRow")}, {data.Link($"column {description.S("SkillColumn")}", description, "SkillColumn")}");
        }
        lines.Add($"{profile} · {locale}");

        decimal manaShift = Pow2(Number(skill, "manashift")), hitShift = Pow2(Number(skill, "HitShift"));
        decimal baseMana = Number(skill, "mana"), perLevelMana = Number(skill, "lvlmana"), minimumMana = Number(skill, "minmana");
        bool hasMana = skill.S("mana").Length > 0 || skill.S("lvlmana").Length > 0;
        var physical = (Min: Curve(skill, "MinDam", i => "MinLevDam" + i, DamageTiers), Max: Curve(skill, "MaxDam", i => "MaxLevDam" + i, DamageTiers));
        var elemental = (Min: Curve(skill, "EMin", i => "EMinLev" + i, DamageTiers), Max: Curve(skill, "EMax", i => "EMaxLev" + i, DamageTiers));
        var length = Curve(skill, "ELen", i => "ELevLen" + i, LengthTiers);
        var element = skill.S("EType");
        bool hasPhysical = physical.Min.Authored || physical.Max.Authored;
        bool hasElemental = element.Length > 0 && (elemental.Min.Authored || elemental.Max.Authored);
        bool hasLength = element.Length > 0 && length.Authored;
        bool hasAttackRating = skill.S("ToHit").Length > 0 || skill.S("LevToHit").Length > 0;
        decimal toHit = Number(skill, "ToHit"), perLevelToHit = Number(skill, "LevToHit");
        if (element.Length > 0 && !hasElemental && !hasLength) issues.Add($"EType is {element} but no elemental damage or length is authored.");
        if (elemental.Min.Authored && element.Length == 0) issues.Add("Elemental damage is authored without an EType, so the game ignores it.");

        // Synergies raise elemental damage when the skill deals it, physical damage otherwise.
        var synergyColumn = hasElemental ? "EDmgSymPerCalc" : hasPhysical ? "DmgSymPerCalc" : "";
        bool hasSynergy = synergyColumn.Length > 0 && skill.S(synergyColumn).Length > 0;
        string synergyError = "";
        var levels = new List<SkillLevelPreview>();
        for (int at = 1; at <= maxLevel; at++)
        {
            token.ThrowIfCancellationRequested();
            // Mana and damage are stored in 256ths; their shift scales them back to the numbers the game shows.
            var mana = Math.Max(minimumMana, (baseMana + perLevelMana * (at - 1)) * manaShift / 256);
            string synergy = "", withSynergy = "";
            if (hasSynergy && synergyError.Length == 0)
                try
                {
                    var scope = calc.Skill(skill, at);
                    var percent = scope.Field(synergyColumn);
                    if (percent != 0)
                    {
                        synergy = (percent > 0 ? "+" : "") + percent + "%";
                        long low = scope.Code(hasElemental ? "edmn" : "pnma"), high = scope.Code(hasElemental ? "edmx" : "pxma");
                        withSynergy = (low == high ? $"{low}" : $"{low}–{high}") + (hasElemental ? " " + element : "");
                    }
                }
                catch (Exception e) when (e is InvalidDataException or FormatException) { synergyError = e.Message; issues.Add($"{synergyColumn}: {e.Message}"); }
            levels.Add(new(at,
                hasMana ? Text(Round(mana)) : "",
                hasPhysical ? Damage(physical.Min.At(at), physical.Max.At(at), hitShift) : "",
                hasElemental ? Damage(elemental.Min.At(at), elemental.Max.At(at), hitShift) + " " + element : "",
                hasLength ? Seconds(length.At(at)) : "",
                hasAttackRating ? "+" + Text(toHit + perLevelToHit * (at - 1)) + "%" : "",
                synergy, withSynergy));
        }
        if (hasSynergy)
            lines.Add(levels.Any(l => l.Synergy.Length > 0)
                ? $"Synergies: {Link($"{synergyColumn} = {skill.S(synergyColumn)}", synergyColumn)}; the level table adds them at the skill levels set above"
                : $"Synergies: {Link($"{synergyColumn} = {skill.S(synergyColumn)}", synergyColumn)}; set the other skills' levels above to see them in the level table");
        // The authored cells behind each level-table column.
        CellLink[] From(params string[] columns) => [.. columns.Where(c => skill.S(c).Length > 0).Select(c => data.Cell(skill, c)).OfType<CellLink>()];
        string[] Tiers(string prefix, int count) => [.. Enumerable.Range(1, count).Select(i => prefix + i)];
        var sources = new Dictionary<string, CellLink[]>
        {
            ["Mana"] = From("mana", "lvlmana", "minmana", "manashift"),
            ["Damage"] = From(["MinDam", "MaxDam", .. Tiers("MinLevDam", 5), .. Tiers("MaxLevDam", 5), "HitShift"]),
            ["Elemental"] = From(["EType", "EMin", "EMax", .. Tiers("EMinLev", 5), .. Tiers("EMaxLev", 5), "HitShift"]),
            ["Length"] = From(["ELen", .. Tiers("ELevLen", 3)]),
            ["Attack"] = From("ToHit", "LevToHit"),
            ["Synergy"] = synergyColumn.Length > 0 ? From(synergyColumn) : [],
            ["With synergies"] = synergyColumn.Length > 0 ? From(synergyColumn) : [],
        };

        if (description != null)
        {
            var tooltip = SkillTooltips.Render(calc, skill, description, level, maxLevel, issues);
            var tooltipLines = tooltip.Lines($"Current skill level: {level}", $"Next level: {level + 1}").ToArray();
            if (tooltipLines.Length > 0) sections.Add(new($"Tooltip at level {level}", tooltipLines, BeforeLevels: true));
        }

        var calculations = new List<string>();
        foreach (var column in CalcColumns("skills", skill))
            try { calculations.Add(DescribeCalc(column, skill.S(column), at => calc.Skill(skill, at).Field(column), level, maxLevel, FrameColumns.Contains(column), data.Cell(skill, column))); }
            catch (Exception e) when (e is InvalidDataException or FormatException) { calculations.Add($"{Link(column, column)} = ⚠ {e.Message} · {skill.S(column)}"); issues.Add($"{column}: {e.Message}"); }
        if (calculations.Count > 0) sections.Add(new($"Calculations at level {level}", [.. calculations], BeforeLevels: true));

        var functions = DescribeFunctions(FunctionGuide.ForRow("skills", [.. skill.Select(f => f.Key)], c => skill.S(c)), skill, data: data).ToArray();
        if (functions.Length > 0) sections.Add(new("Functions", functions, BeforeLevels: true));

        var notes = calc.Notes.Append("Base skill values plus the synergies set above; +skills, difficulty resistances and other character bonuses are not applied.").ToArray();
        sections.Add(new("Assumptions", notes));
        return new(name, characterClass, maxLevel, level, [.. lines], [.. sections], [.. levels], [.. calc.Inputs], [.. issues.Distinct()], sources);
    }
}
