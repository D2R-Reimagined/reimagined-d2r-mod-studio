using System.Text.Json.Nodes;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>One level of a missile. Empty strings mean the missile does not author that field.</summary>
public record MissileLevelPreview(int Level, string Physical, string Elemental, string Duration, string Velocity, string Synergy = "", string WithSynergy = "");

/// <param name="Level">The level calculations are shown at, clamped to the missile's range.</param>
/// <param name="Inputs">The assumptions the calculations read (other skills' levels, stats, character level).</param>
/// <param name="ColumnSources">The cells each level-table column is computed from, by column header.</param>
public record MissilePreviewResult(string Name, int MaxLevel, int Level, string[] Lines, PreviewSection[] Sections, MissileLevelPreview[] Levels, CalcInput[] Inputs, string[] Issues,
    IReadOnlyDictionary<string, CellLink[]>? ColumnSources = null)
{
    public PreviewText[] Text { get; } = PreviewText.Parse(Lines);
    public string[] Lines { get; } = PreviewText.Plain(Lines);
}

/// <summary>
/// Worker-owned resolver for a missiles.txt row: what fires it, what it spawns, how it moves and what it does per level.
/// Reads authored data only; never changes records or starts a build.
/// </summary>
/// <remarks>
/// Missiles carry the same damage shape as skills — values in 256ths scaled by <c>HitShift</c>, growing by tier across
/// levels — but their level comes from whichever skill fired them, so the curve runs to the highest <c>maxlvl</c> among
/// the skills that reference this missile, and that skill is the one <c>skill('…'.lvl)</c> in its calculations reads.
/// Server and client functions (<c>pSrvDoFunc</c> and friends) are named from the data guide with the parameters they
/// read; what they do with them is game code.
/// </remarks>
public sealed class MissilePreviewResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();
    public const int DefaultMaxLevel = 20;
    /// <summary>How deep the spawn chain is followed before it is left to the reader.</summary>
    private const int ChainDepth = 3;
    /// <summary>skills.txt columns that name a missile.</summary>
    private static readonly string[] SkillMissileFields = ["srvmissile", "srvmissilea", "srvmissileb", "srvmissilec", "cltmissile", "cltmissilea", "cltmissileb", "cltmissilec", "cltmissiled"];
    /// <summary>missiles.txt columns that name another missile, in the order the card lists them.</summary>
    private static readonly string[] ChainFields = [
        "ExplosionMissile", "SubMissile1", "SubMissile2", "SubMissile3",
        "HitSubMissile1", "HitSubMissile2", "HitSubMissile3", "HitSubMissile4",
        "CltSubMissile1", "CltSubMissile2", "CltSubMissile3",
        "CltHitSubMissile1", "CltHitSubMissile2", "CltHitSubMissile3", "CltHitSubMissile4"];

    public MissilePreviewResult Resolve(ModProject project, JsonObject record, string profile, string locale, CancellationToken token, CalcPreviewOptions? options = null)
    {
        options ??= new();
        var issues = new List<string>(); var lines = new List<string>(); var sections = new List<PreviewSection>();
        var data = tables.Open(project, profile, locale, issues, token);
        var missile = data.Effective("missiles", record);
        var name = missile.S("Missile");
        if (name.Length == 0) return new("Inactive row", 0, 0, ["Inactive/header row · no missile id"], [], [], [], []);

        // What fires this missile, and how high its level can go: a missile has no level of its own.
        var skills = data.Rows("skills", false);
        var firedBy = skills
            .Select(s => (Skill: s, Fields: SkillMissileFields.Where(f => s.S(f) == name).ToArray()))
            .Where(x => x.Fields.Length > 0).ToArray();
        var spawnedBy = data.Rows("missiles", false)
            .Select(m => (Missile: m, Fields: ChainFields.Where(f => m.S(f) == name).ToArray()))
            .Where(x => x.Fields.Length > 0 && x.Missile.S("Missile") != name).ToArray();
        // A missile can take its damage from a named skill instead of its own fields; then that skill's curve is the real one.
        var borrowed = missile.S("Skill");
        JsonObject? borrowedSkill = null;
        if (borrowed.Length > 0)
        {
            borrowedSkill = data.Find("skills", "skill", borrowed, false);
            if (borrowedSkill == null) issues.Add($"Unresolved skills/skill: {borrowed}.");
        }
        int maxLevel = firedBy.Select(x => (int)Number(x.Skill, "maxlvl")).DefaultIfEmpty(0).Max();
        bool fromSkills = maxLevel > 0;
        if (!fromSkills && borrowedSkill != null) maxLevel = (int)Number(borrowedSkill, "maxlvl");
        if (maxLevel <= 0) maxLevel = DefaultMaxLevel;
        Require(maxLevel <= 99, $"maxlvl {maxLevel} is out of range; skill levels stop at 99.");
        int level = Math.Clamp(options.Level, 1, maxLevel);
        var calc = new CalcContext(data, options.Assumptions ?? new(), token);
        // The skill whose level the missile runs at: the one that sets the level range, else the skill it borrows damage from.
        var ownerSkill = firedBy.OrderByDescending(x => Number(x.Skill, "maxlvl")).Select(x => x.Skill).FirstOrDefault() ?? borrowedSkill;
        MissileCalcScope Scope(int at) => calc.Missile(missile, at, ownerSkill == null ? null : calc.Skill(ownerSkill, at));
        string Link(string text, params string[] columns) => data.Link(text, missile, columns);

        lines.Add(fromSkills
            ? $"Levels 1–{maxLevel}, from the highest maxlvl among the skills that fire it"
            : maxLevel != DefaultMaxLevel ? $"Levels 1–{maxLevel}, from the {data.Link($"maxlvl of {borrowed}", borrowedSkill, "maxlvl")}"
            : firedBy.Length > 0
                ? $"Levels 1–{maxLevel}: no skill firing this missile sets maxlvl, so the usual {DefaultMaxLevel} is shown"
                : $"Levels 1–{maxLevel}: no skill fires this missile directly, so the usual {DefaultMaxLevel} is shown");
        if (Flag(missile, "MissileSkill")) lines.Add($"{Link("MissileSkill = 1", "MissileSkill")}: the game uses the damage of the skill that created it; the missile's own damage fields are ignored.");
        if (borrowedSkill != null) lines.Add($"{Link($"Skill = {borrowed}", "Skill")}: the damage below is that skill's, not the missile's own fields.");
        else if (borrowed.Length > 0) lines.Add($"{Link($"Skill = {borrowed}", "Skill")}: the game takes this missile's damage from that skill instead of the fields below.");
        lines.Add($"{profile} · {locale}");

        if (firedBy.Length > 0 || spawnedBy.Length > 0)
            sections.Add(new("Used by", BeforeLevels: true, Lines: [
                .. firedBy.Select(x => $"{data.Link(Describe(x.Skill.S("skill")), x.Skill, "skill")} · {string.Join(", ", x.Fields.Select(f => data.Link(f, x.Skill, f)))}"
                    + (Number(x.Skill, "maxlvl") > 0 ? $" · {data.Link($"maxlvl {Text(Number(x.Skill, "maxlvl"))}", x.Skill, "maxlvl")}" : "")),
                .. spawnedBy.Select(x => $"missile {data.Link(x.Missile.S("Missile"), x.Missile, "Missile")} · {string.Join(", ", x.Fields.Select(f => data.Link(f, x.Missile, f)))}")]));
        else issues.Add("Nothing references this missile: no skill fires it and no missile spawns it.");

        var chain = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal) { name };
        Chain(missile, 0);
        void Chain(JsonObject from, int depth)
        {
            foreach (var field in ChainFields)
            {
                token.ThrowIfCancellationRequested();
                var target = from.S(field); if (target.Length == 0) continue;
                var row = data.Find("missiles", "Missile", target, false);
                if (row == null) { chain.Add(new string(' ', depth * 2) + $"{data.Link(field, from, field)} → {target} (missing)"); issues.Add($"Unresolved missiles/Missile: {target}."); continue; }
                bool repeat = !seen.Add(target);
                chain.Add(new string(' ', depth * 2) + $"{data.Link(field, from, field)} → {data.Link(target, row, "Missile")}" + (repeat ? " (already shown)" : depth + 1 >= ChainDepth ? " …" : ""));
                if (!repeat && depth + 1 < ChainDepth) Chain(row, depth + 1);
            }
        }
        if (chain.Count > 0) sections.Add(new("Spawns", [.. chain], BeforeLevels: true));

        // skills.txt and missiles.txt spell the same curves differently (MinDam/EMinLev1 against MinDamage/MinELev1).
        var source = borrowedSkill ?? missile;
        decimal hitShift = Pow2(Number(source, "HitShift"));
        var physical = borrowedSkill != null
            ? (Min: Curve(source, "MinDam", i => "MinLevDam" + i, DamageTiers), Max: Curve(source, "MaxDam", i => "MaxLevDam" + i, DamageTiers))
            : (Min: Curve(missile, "MinDamage", i => "MinLevDam" + i, DamageTiers), Max: Curve(missile, "MaxDamage", i => "MaxLevDam" + i, DamageTiers));
        var elemental = borrowedSkill != null
            ? (Min: Curve(source, "EMin", i => "EMinLev" + i, DamageTiers), Max: Curve(source, "EMax", i => "EMaxLev" + i, DamageTiers))
            : (Min: Curve(missile, "EMin", i => "MinELev" + i, DamageTiers), Max: Curve(missile, "EMax", i => "MaxELev" + i, DamageTiers));
        var length = Curve(source, "ELen", i => "ELevLen" + i, LengthTiers);
        var element = source.S("EType");
        bool hasPhysical = physical.Min.Authored || physical.Max.Authored;
        bool hasElemental = element.Length > 0 && (elemental.Min.Authored || elemental.Max.Authored);
        bool hasLength = element.Length > 0 && length.Authored;
        if (elemental.Min.Authored && element.Length == 0) issues.Add($"Elemental damage is authored on {(borrowedSkill != null ? borrowed : name)} without an EType, so the game ignores it.");
        decimal velocity = Number(missile, "Vel"), perLevelVelocity = Number(missile, "VelLev"), maxVelocity = Number(missile, "MaxVel");
        bool hasVelocity = missile.S("Vel").Length > 0 || missile.S("VelLev").Length > 0;

        // A missile's own synergy calculation; a borrowed skill's synergies belong to that skill's preview.
        var synergyColumn = borrowedSkill != null ? "" : hasElemental ? "EDmgSymPerCalc" : hasPhysical ? "DmgSymPerCalc" : "";
        bool hasSynergy = synergyColumn.Length > 0 && missile.S(synergyColumn).Length > 0;
        string synergyError = "";
        // The authored cells behind each level-table column; a borrowed skill's curve is read from that skill's row.
        CellLink[] From(JsonObject row, params string[] columns) => [.. columns.Where(c => row.S(c).Length > 0).Select(c => data.Cell(row, c)).OfType<CellLink>()];
        string[] Tiers(string prefix, int count) => [.. Enumerable.Range(1, count).Select(i => prefix + i)];
        var sources = new Dictionary<string, CellLink[]>
        {
            ["Damage"] = borrowedSkill != null ? From(source, ["MinDam", "MaxDam", .. Tiers("MinLevDam", 5), .. Tiers("MaxLevDam", 5), "HitShift"])
                : From(missile, ["MinDamage", "MaxDamage", .. Tiers("MinLevDam", 5), .. Tiers("MaxLevDam", 5), "HitShift"]),
            ["Elemental"] = borrowedSkill != null ? From(source, ["EType", "EMin", "EMax", .. Tiers("EMinLev", 5), .. Tiers("EMaxLev", 5), "HitShift"])
                : From(missile, ["EType", "EMin", "EMax", .. Tiers("MinELev", 5), .. Tiers("MaxELev", 5), "HitShift"]),
            ["Length"] = From(source, ["ELen", .. Tiers("ELevLen", 3)]),
            ["Velocity"] = From(missile, "Vel", "VelLev", "MaxVel"),
            ["Synergy"] = synergyColumn.Length > 0 ? From(missile, synergyColumn) : [],
            ["With synergies"] = synergyColumn.Length > 0 ? From(missile, synergyColumn) : [],
        };
        var levels = new List<MissileLevelPreview>();
        for (int at = 1; at <= maxLevel; at++)
        {
            token.ThrowIfCancellationRequested();
            var speed = velocity + perLevelVelocity * (at - 1);
            if (maxVelocity > 0) speed = Math.Min(speed, maxVelocity);
            string synergy = "", withSynergy = "";
            if (hasSynergy && synergyError.Length == 0)
                try
                {
                    var scope = Scope(at);
                    var percent = scope.Field(synergyColumn);
                    if (percent != 0)
                    {
                        synergy = (percent > 0 ? "+" : "") + percent + "%";
                        long low = scope.Code(hasElemental ? "edmn" : "damn"), high = scope.Code(hasElemental ? "edmx" : "damx");
                        withSynergy = (low == high ? $"{low}" : $"{low}–{high}") + (hasElemental ? " " + element : "");
                    }
                }
                catch (Exception e) when (e is InvalidDataException or FormatException) { synergyError = e.Message; issues.Add($"{synergyColumn}: {e.Message}"); }
            levels.Add(new(at,
                hasPhysical ? Damage(physical.Min.At(at), physical.Max.At(at), hitShift) : "",
                hasElemental ? Damage(elemental.Min.At(at), elemental.Max.At(at), hitShift) + " " + element : "",
                hasLength ? Seconds(length.At(at)) : "",
                hasVelocity ? Text(speed) : "",
                synergy, withSynergy));
        }

        var motion = new List<string>();
        if (hasVelocity)
        {
            motion.Add($"Velocity: {Link($"{Text(velocity)} px/frame", "Vel")}" + (perLevelVelocity != 0 ? $", {Link($"+{Text(perLevelVelocity)} per caster level", "VelLev")}" : "") + (maxVelocity > 0 ? $", {Link($"capped at {Text(maxVelocity)}", "MaxVel")}" : ""));
            if (Number(missile, "Accel") != 0) motion.Add($"Acceleration: {Link($"{Text(Number(missile, "Accel"))} px/frame²", "Accel")}");
        }
        // Range and Radius may be calculations ("100+(lvl*25)"), so they are read at the chosen level.
        long? Measure(string field)
        {
            if (missile.S(field).Trim().Length == 0) return null;
            try { return Scope(level).Field(field); }
            catch (Exception e) when (e is InvalidDataException or FormatException) { issues.Add($"{field}: {e.Message}"); return null; }
        }
        string AtLevel(string field) => long.TryParse(missile.S(field).Trim(), out _) ? "" : $" at level {level}";
        if (Measure("Range") is { } range)
        {
            motion.Add($"Lifetime: {Link($"{range} frames", "Range")} ({Seconds(range)}){AtLevel("Range")}");
            // Constant-velocity reach; acceleration, collisions and skill functions that rewrite Range all change it.
            var speedAt = velocity + perLevelVelocity * (level - 1); if (maxVelocity > 0) speedAt = Math.Min(speedAt, maxVelocity);
            if (hasVelocity && speedAt > 0 && Number(missile, "Accel") == 0)
                motion.Add($"Reach at level {level}: about {Text(speedAt * range)} px, if it never collides");
        }
        foreach (var (field, label) in new[] { ("Activate", "Frames before it can collide"), ("InitSteps", "Frames before it becomes visible") })
            if (Number(missile, field) > 0) motion.Add($"{label}: {Link(Text(Number(missile, field)), field)}");
        if (missile.S("Size").Length > 0) motion.Add($"Collision size: {Link($"{Text(Number(missile, "Size"))} sub-tiles", "Size")}");
        if (Measure("Radius") is { } radius) motion.Add($"Search/VFX radius: {Link($"{radius} sub-tiles", "Radius")}{AtLevel("Radius")}");
        if (motion.Count > 0) sections.Add(new("Motion", [.. motion], BeforeLevels: true));

        var behavior = new List<string>();
        var collide = Number(missile, "CollideType");
        if (missile.S("CollideType").Length > 0) behavior.Add(Link($"CollideType {Text(collide)}", "CollideType") + (collide == 0 ? " · passes through everything" : ""));
        foreach (var (field, text) in new[] {
            ("CollideKill", "Destroyed when it collides"), ("CollideFriend", "Collides with friendly units"), ("LastCollide", "Remembers the last unit it hit"),
            ("Collision", "Has a placement collision mask"), ("ClientCol", "Checks collision on the client"), ("Pierce", "Pierce can apply"),
            ("ToHit", "Uses the caster's attack rating to hit"), ("AlwaysExplode", "Always explodes when killed"), ("Explosion", "Treated as an explosion"),
            ("ApplyMastery", "Elemental mastery applies"), ("CanSlow", "Slow Missiles affects it"), ("ReturnFire", "Triggers Chilling Armor"),
            ("GetHit", "Puts the target into hit recovery"), ("SoftHit", "Causes a soft hit (blood, hit sound)"), ("CanDestroy", "Can be attacked and destroyed"),
            ("Town", "Allowed to exist in town"), ("SrcTown", "Destroyed when the caster is in town"),
            ("NoUniqueMod", "Ignores unique monster modifiers"), ("NoMultiShot", "Ignores the Multi-Shot modifier") })
            if (Flag(missile, field)) behavior.Add(Link(text, field));
        if (Number(missile, "KnockBack") > 0) behavior.Add($"Knockback chance: {Link($"{Text(Number(missile, "KnockBack"))}%", "KnockBack")}");
        if (Flag(missile, "NextHit")) behavior.Add($"{Link("Can hit the same unit again", "NextHit")} after {Link($"{Text(Number(missile, "NextDelay"))} frames", "NextDelay")}");
        // SrcDamage and SrcMissDmg are percentages in 128ths.
        if (Number(missile, "SrcDamage") > 0)
            behavior.Add($"Adds {Link($"{Text(Round(Number(missile, "SrcDamage") * 100 / 128))}%", "SrcDamage")} of the caster's damage" + (Flag(missile, "Half2HSrc") ? $", {Link("halved with a two-handed weapon", "Half2HSrc")}" : ""));
        if (Number(missile, "SrcMissDmg") > 0) behavior.Add($"Adds {Link($"{Text(Round(Number(missile, "SrcMissDmg") * 100 / 128))}%", "SrcMissDmg")} of the source missile's damage");
        if (behavior.Count > 0) sections.Add(new("Behavior", [.. behavior]));

        var visual = new List<string>();
        if (missile.S("CelFile").Length > 0) visual.Add($"Graphics: {Link(missile.S("CelFile"), "CelFile")}" + (missile.S("NumDirections").Length > 0 ? $" · {Link($"{missile.S("NumDirections")} directions", "NumDirections")}" : ""));
        if (missile.S("AnimLen").Length > 0) visual.Add($"Animation: {Link($"{Text(Number(missile, "AnimLen"))} frames", "AnimLen")} ({Seconds(Number(missile, "AnimLen"))})" + (Flag(missile, "LoopAnim") ? $", {Link("looping", "LoopAnim")}" : ", played once"));
        if (Flag(missile, "SubLoop")) visual.Add($"{Link("Loops frames", "SubLoop")} {Link(missile.S("SubStart"), "SubStart")}–{Link(missile.S("SubStop"), "SubStop")}");
        if (missile.S("AnimSpeed").Length > 0) visual.Add($"AnimSpeed: {Link(missile.S("AnimSpeed"), "AnimSpeed")} (16ths)");
        if (Number(missile, "Light") > 0)
            visual.Add($"Light radius {Link(Text(Number(missile, "Light")), "Light")} · {Link($"RGB {missile.S("Red", "0")},{missile.S("Green", "0")},{missile.S("Blue", "0")}", "Red", "Green", "Blue")}" + (Flag(missile, "Flicker") ? $" · {Link("flickers", "Flicker")}" : ""));
        foreach (var (field, label) in new[] { ("TravelSound", "Travel sound"), ("HitSound", "Hit sound"), ("ProgSound", "Progress sound"), ("ProgOverlay", "Progress overlay"), ("MissileWeaponVFX", "Weapon VFX") })
            if (missile.S(field).Length > 0) visual.Add($"{label}: {Link(missile.S(field), field)}");
        if (visual.Count > 0) sections.Add(new("Visuals and sound", [.. visual]));

        var calculations = new List<string>();
        foreach (var column in CalcColumns("missiles", missile))
            try { calculations.Add(DescribeCalc(column, missile.S(column), at => Scope(at).Field(column), level, maxLevel, false, data.Cell(missile, column))); }
            catch (Exception e) when (e is InvalidDataException or FormatException) { calculations.Add($"{Link(column, column)} = ⚠ {e.Message} · {missile.S(column)}"); issues.Add($"{column}: {e.Message}"); }
        if (calculations.Count > 0) sections.Add(new($"Calculations at level {level}", [.. calculations], BeforeLevels: true));

        // Each parameter group keeps its authored description in a differently spelled comment column.
        var parameterGroups = new[] { ("Param", "*param{0} desc"), ("CltParam", "*client param{0} desc"), ("sHitPar", "*server hit param{0} desc"), ("cHitPar", "*client hit param{0} desc"), ("dParam", "*damage param{0} desc") };
        string Parameter(string field)
        {
            var group = parameterGroups.FirstOrDefault(g => field.StartsWith(g.Item1, StringComparison.OrdinalIgnoreCase) && int.TryParse(field[g.Item1.Length..], out _));
            var note = group.Item1 == null ? "" : missile.S(string.Format(group.Item2, field[group.Item1.Length..]));
            return Link($"{field} = {missile.S(field)}", field) + (note.Length > 0 ? $" ({note})" : "");
        }
        var functions = new List<string>(); var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in new[] { "pSrvDoFunc", "pSrvHitFunc", "pSrvDmgFunc", "pCltDoFunc", "pCltHitFunc" })
        {
            var value = missile.S(column).Trim(); if (value is "" or "0") continue;
            var function = FunctionGuide.Describe("missiles", column, value);
            if (function == null) { functions.Add($"{Link($"{column} {value}", column)} · not in the data guide"); continue; }
            var summary = function.Summary.Replace('\n', ' ');
            functions.Add(Link(function.Title, column) + (summary.Length > 0 ? " — " + (summary.Length > 180 ? summary[..180].TrimEnd() + "…" : summary) : ""));
            var fields = FunctionGuide.Fields("missiles", column, function, [.. missile.Select(f => f.Key)]).Where(f => missile.S(f).Length > 0).ToArray();
            read.UnionWith(fields);
            if (fields.Length > 0) functions.Add("    reads " + string.Join(" · ", fields.Select(Parameter)));
        }
        var others = parameterGroups.SelectMany(g => Enumerable.Range(1, 5).Select(i => g.Item1 + i)).Where(f => missile.S(f).Length > 0 && !read.Contains(f)).ToArray();
        if (others.Length > 0) functions.Add("Other parameters: " + string.Join(" · ", others.Select(Parameter)));
        if (functions.Count > 0) sections.Add(new("Functions and parameters", [.. functions], BeforeLevels: true));

        if (hasSynergy)
            lines.Add(levels.Any(l => l.Synergy.Length > 0)
                ? $"Synergies: {Link($"{synergyColumn} = {missile.S(synergyColumn)}", synergyColumn)}; the level table adds them at the skill levels set above"
                : $"Synergies: {Link($"{synergyColumn} = {missile.S(synergyColumn)}", synergyColumn)}; set the other skills' levels above to see them in the level table");
        var notes = calc.Notes.ToList();
        if (ownerSkill != null) notes.Insert(0, $"Calculations run at the level of {Describe(ownerSkill.S("skill"))}, which skill('{ownerSkill.S("skill")}'.lvl) reads as that level.");
        notes.Add("Base missile values plus the synergies set above; masteries, +skills, difficulty resistances and caster bonuses are not applied.");
        sections.Add(new("Assumptions", [.. notes]));
        return new(name, maxLevel, level, [.. lines], [.. sections], [.. levels], [.. calc.Inputs], [.. issues.Distinct()], sources);

        string Describe(string skill) => skill.Length > 0 ? skill : "(unnamed skill)";
    }
}
