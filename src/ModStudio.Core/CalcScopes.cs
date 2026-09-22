using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.PreviewMath;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// The values a preview of one row cannot know: how many points the character has in other skills (synergies and
/// masteries), stat totals read by <c>stat()</c>, and the character's own level. Missing entries count as 0, except the
/// character level.
/// </summary>
public sealed record CalcAssumptions
{
    public const int DefaultCharacterLevel = 30;
    public int CharacterLevel { get; init; } = DefaultCharacterLevel;
    public IReadOnlyDictionary<string, int> SkillLevels { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, long> Stats { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>An assumption a preview's calculations actually read, so the inspector can offer it as an input.</summary>
/// <param name="Kind"><c>skill</c> (another skill's level), <c>stat</c> (a stat total) or <c>ulvl</c> (character level).</param>
public sealed record CalcInput(string Kind, string Name, long Value)
{
    public string Label => Kind switch { "skill" => Name + " level", "stat" => Name, _ => "Character level" };
}

/// <summary>
/// One resolution's calculation state over the preview tables: parsed expressions, the assumptions read so far and notes
/// about values the preview had to pick. Worker-owned, like the session it reads.
/// </summary>
public sealed class CalcContext(PreviewTables.Session data, CalcAssumptions assumptions, CancellationToken token)
{
    private readonly Dictionary<string, CalcNode> parsed = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Kind, string Name), CalcInput> inputs = [];
    private readonly List<string> notes = [];
    private readonly List<string> evaluating = [];
    public PreviewTables.Session Data { get; } = data;
    public CalcAssumptions Assumptions { get; } = assumptions;
    /// <summary>Assumptions the calculations read, in first-read order with the character level last.</summary>
    public IReadOnlyList<CalcInput> Inputs => inputs.Values.OrderBy(i => i.Kind == "ulvl").ThenBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    /// <summary>Values the preview could not know and had to pick, each said once.</summary>
    public IReadOnlyList<string> Notes => notes;
    public void Note(string note) { if (!notes.Contains(note)) notes.Add(note); }

    public CalcNode Parse(string expression)
    {
        if (!parsed.TryGetValue(expression, out var node))
        {
            var warnings = new List<string>();
            parsed[expression] = node = Calc.Parse(expression, warnings);
            foreach (var warning in warnings) Note($"{expression.Trim()} · {warning}");
        }
        return node;
    }
    public SkillCalcScope Skill(JsonObject row, int level) => new(this, row, level);
    public MissileCalcScope Missile(JsonObject row, int level, SkillCalcScope? owner) => new(this, row, level, owner);
    public JsonObject? FindSkill(string name) => Data.Find("skills", "skill", name, false) ?? Data.Rows("skills", false).FirstOrDefault(r => r.S("skill").Equals(name, StringComparison.OrdinalIgnoreCase));
    public JsonObject? FindMissile(string name) => Data.Find("missiles", "Missile", name, false) ?? Data.Rows("missiles", false).FirstOrDefault(r => r.S("Missile").Equals(name, StringComparison.OrdinalIgnoreCase));

    internal int SkillLevel(string name)
    {
        var level = Assumptions.SkillLevels.TryGetValue(name, out var value) ? value : 0;
        inputs.TryAdd(("skill", name), new("skill", name, level));
        return level;
    }
    internal long Stat(string name)
    {
        var value = Assumptions.Stats.TryGetValue(name, out var stat) ? stat : 0;
        inputs.TryAdd(("stat", name), new("stat", name, value));
        return value;
    }
    internal long CharacterLevel()
    {
        inputs.TryAdd(("ulvl", ""), new("ulvl", "", Assumptions.CharacterLevel));
        return Assumptions.CharacterLevel;
    }
    /// <summary>Runs one calc field, refusing a field that (through other fields and lookups) ends up reading itself.</summary>
    internal long Enter(string key, Func<long> evaluate)
    {
        token.ThrowIfCancellationRequested();
        Require(!evaluating.Contains(key), $"{key} refers back to itself: {string.Join(" → ", evaluating.Append(key))}.");
        Require(evaluating.Count < 40, $"Calculations nest too deeply at {key}.");
        evaluating.Add(key);
        try { return evaluate(); }
        finally { evaluating.RemoveAt(evaluating.Count - 1); }
    }
}

/// <summary>What a calc expression's codes mean inside one row at one level. Subclasses supply the table's own codes.</summary>
public abstract class CalcScope(CalcContext context, JsonObject row, int level) : ICalcScope
{
    public CalcContext Context { get; } = context;
    public JsonObject Row { get; } = row;
    public int Level { get; } = level;
    protected abstract string Identity { get; }

    /// <summary>A calc column of this row; an empty column is 0.</summary>
    public long Field(string column)
    {
        var text = Row.S(column).Trim();
        return text.Length == 0 ? 0 : Context.Enter($"{Identity}/{column}@{Level}", () => Calc.Evaluate(Context.Parse(text), this));
    }
    /// <summary>An expression evaluated as if it were a calc column of this row.</summary>
    public long Evaluate(string expression) => Calc.Evaluate(Context.Parse(expression), this);
    public abstract long Code(string code);
    public long Random(long low, long high)
    {
        Context.Note("rand(low, high) is rolled by the game; the preview takes its low end.");
        return Math.Min(low, high);
    }
    public virtual long Reference(CalcReference reference)
    {
        switch (reference.Function)
        {
            case "stat":
                return Context.Stat(reference.Name);
            case "miss":
            {
                var missile = Context.FindMissile(reference.Name);
                Require(missile != null, $"Unresolved missile '{reference.Name}' in {reference}.");
                return Context.Missile(missile!, Level, Owner).Code(reference.Path[0]);
            }
            case "skill":
            case "sksrc":
            {
                var skill = Context.FindSkill(reference.Name);
                Require(skill != null, $"Unresolved skill '{reference.Name}' in {reference}.");
                var code = reference.Path[0];
                // The skill being previewed is at the previewed level; any other skill is at the level the reader assumes.
                if (Owner is { } self && SameSkill(self.Row, skill!)) return code is "lvl" or "blvl" ? self.Level : self.Code(code);
                int level = Context.SkillLevel(skill!.S("skill"));
                if (code is "lvl" or "blvl" || level <= 0) return code is "lvl" or "blvl" ? level : 0;
                return Context.Skill(skill!, level).Code(code);
            }
            case "sklvl":
            {
                var skill = Context.FindSkill(reference.Name);
                Require(skill != null, $"Unresolved skill '{reference.Name}' in {reference}.");
                var levelPart = reference.Path[0];
                long level = Regex.IsMatch(levelPart, "^[0-9]+$") ? long.Parse(levelPart) : Code(levelPart);
                if (level <= 0) return 0;
                Require(level <= 99, $"{reference} asks for level {level}; skill levels stop at 99.");
                return Context.Skill(skill!, (int)level).Code(reference.Path[1]);
            }
            default: throw new InvalidDataException($"Unknown calc lookup {reference.Function}().");
        }
    }
    /// <summary>The skill whose level this scope runs at: the skill itself, or the skill that fired a missile.</summary>
    protected abstract SkillCalcScope? Owner { get; }
    protected static bool SameSkill(JsonObject a, JsonObject b) => a.S("skill").Equals(b.S("skill"), StringComparison.OrdinalIgnoreCase);
    protected long Whole(string column) => (long)Math.Truncate(Number(Row, column));
    /// <summary>A linear (<c>ln</c>) or diminishing-returns (<c>dm</c>) curve between two parameters, as skillcalc.txt defines them.</summary>
    protected long Curve(bool diminishing, long a, long b) => diminishing
        ? (110L * Level * (b - a)) / (100L * (Level + 6)) + a
        : a + (Level - 1L) * b;
    /// <summary>Damage in 256ths: a base field and its tiered per-level fields scaled by <c>HitShift</c>, plus a synergy percentage.</summary>
    private protected long Damage256(LevelCurve curve, string synergyColumn)
    {
        if (Level <= 0) return 0;
        long value = (long)Math.Truncate(curve.At(Level) * Pow2(Number(Row, "HitShift")));
        long synergy = Field(synergyColumn);
        return value + value * synergy / 100;
    }
    private protected long Length(LevelCurve curve, string synergyColumn)
    {
        if (Level <= 0) return 0;
        long value = (long)Math.Truncate(curve.At(Level));
        return synergyColumn.Length == 0 ? value : value + value * Field(synergyColumn) / 100;
    }
    protected InvalidDataException Unknown(string code, string table) => new($"Unknown {table} code '{code}'.");
}

/// <summary>skillcalc.txt codes for a skills.txt row at a level.</summary>
public sealed class SkillCalcScope(CalcContext context, JsonObject row, int level) : CalcScope(context, row, level)
{
    private static readonly Dictionary<string, (int A, int B)> Pairs = new()
    {
        ["12"] = (1, 2), ["34"] = (3, 4), ["56"] = (5, 6), ["78"] = (7, 8), ["91"] = (9, 10), ["21"] = (11, 12),
        ["43"] = (13, 14), ["65"] = (15, 16), ["87"] = (17, 18), ["92"] = (19, 20)
    };
    protected override string Identity => "skills/" + Row.S("skill");
    protected override SkillCalcScope? Owner => this;
    internal LevelCurve ElementalMin => PreviewMath.Curve(Row, "EMin", i => "EMinLev" + i, DamageTiers);
    internal LevelCurve ElementalMax => PreviewMath.Curve(Row, "EMax", i => "EMaxLev" + i, DamageTiers);
    internal LevelCurve PhysicalMin => PreviewMath.Curve(Row, "MinDam", i => "MinLevDam" + i, DamageTiers);
    internal LevelCurve PhysicalMax => PreviewMath.Curve(Row, "MaxDam", i => "MaxLevDam" + i, DamageTiers);
    internal LevelCurve ElementalLength => PreviewMath.Curve(Row, "ELen", i => "ELevLen" + i, LengthTiers);

    public override long Code(string code)
    {
        var match = Regex.Match(code, "^(ln|dm)([0-9]{2})$");
        if (match.Success && Pairs.TryGetValue(match.Groups[2].Value, out var pair))
            return Curve(match.Groups[1].Value == "dm", Param(pair.A), Param(pair.B));
        match = Regex.Match(code, "^(?:par([1-9])|pa([0-9]{2}))$");
        if (match.Success) return Param(int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value));
        match = Regex.Match(code, "^m([123])(en|ex|el|rn|eo|ey|nm|xm)$|^me(3)([oy])$");
        if (match.Success) return DescMissile(match);
        switch (code)
        {
            case "lvl": case "blvl": return Level;
            case "ulvl": return Context.CharacterLevel();
            case "edmn": return Damage256(ElementalMin, "EDmgSymPerCalc") >> 8;
            case "edmx": return Damage256(ElementalMax, "EDmgSymPerCalc") >> 8;
            case "edns": return Damage256(ElementalMin, "EDmgSymPerCalc");
            case "edxs": return Damage256(ElementalMax, "EDmgSymPerCalc");
            case "edln": return Length(ElementalLength, "ELenSymPerCalc");
            case "enma": case "exma": case "edma": case "enms": case "exms":
                Context.Note("Elemental mastery is not applied: enma, exma and edma show the same values as edmn, edmx and edln.");
                return Code(code switch { "enma" => "edmn", "exma" => "edmx", "edma" => "edln", "enms" => "edns", _ => "edxs" });
            case "pnma": return Damage256(PhysicalMin, "DmgSymPerCalc") >> 8;
            case "pxma": return Damage256(PhysicalMax, "DmgSymPerCalc") >> 8;
            case "pnms": return Damage256(PhysicalMin, "DmgSymPerCalc");
            case "pxms": return Damage256(PhysicalMax, "DmgSymPerCalc");
            case "toht": return Row.S("ToHitCalc").Length > 0 ? Field("ToHitCalc") : Whole("ToHit") + Whole("LevToHit") * (Level - 1L);
            case "usmc": return ManaCost256();
            case "mana": return ManaCost256() >> 8;
            case "mps": return ManaCost256() * 25 >> 8;
            case "math": case "madm": case "macr": case "mael": case "mapi": case "manc": case "mair":
                Context.Note("Masteries granted by other skills (math, madm, macr, mael, mapi, manc, mair) are taken as 0.");
                return 0;
            case "sctk":
                Context.Note("The channeling tick (sctk) is only known while casting; the preview takes 0.");
                return 0;
            case "len": return Field("auralencalc");
            case "rng": return Field("aurarangecalc");
            case "pets": return Field("petmax");
            case "skpt": return Field("skpoints");
            case "clc0": return Field("calc10");
        }
        match = Regex.Match(code, "^(?:clc([1-9])|ast([1-6])|pst([1-9])|ps(1[0-4]))$");
        if (match.Success)
        {
            if (match.Groups[1].Success) return Field("calc" + match.Groups[1].Value);
            if (match.Groups[2].Success) return Field("aurastatcalc" + match.Groups[2].Value);
            return Field("passivecalc" + (match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value));
        }
        throw Unknown(code, "skillcalc");
    }
    private long Param(int index) => Whole("Param" + index);
    /// <summary>Mana cost in 256ths: <c>mana</c> plus <c>lvlmana</c> per level, scaled by <c>manashift</c>, never below <c>minmana</c>.</summary>
    public long ManaCost256()
    {
        if (Level <= 0) return 0;
        long cost = (long)Math.Truncate((Number(Row, "mana") + Number(Row, "lvlmana") * (Level - 1)) * Pow2(Number(Row, "manashift")));
        return Math.Max(cost, (long)Math.Truncate(Number(Row, "minmana") * 256));
    }
    /// <summary>The <c>m1en</c> family: the descmissile# linked in this skill's skilldesc row, at this skill's level.</summary>
    private long DescMissile(Match match)
    {
        var slot = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
        var part = match.Groups[2].Success ? match.Groups[2].Value : "e" + match.Groups[4].Value;
        var description = Row.S("skilldesc") is { Length: > 0 } key ? Context.Data.Find("skilldesc", "skilldesc", key, false) : null;
        var name = description?.S("descmissile" + slot) ?? "";
        Require(name.Length > 0, $"m{slot}{part} reads descmissile{slot} of this skill's skilldesc row, which is not set.");
        var missile = Context.FindMissile(name);
        Require(missile != null, $"Unresolved missile '{name}' in descmissile{slot}.");
        if (part is "nm" or "xm") Context.Note("Elemental mastery is not applied: m#nm and m#xm show the same values as m#en and m#ex.");
        return Context.Missile(missile!, Level, this).Code(part switch
        {
            "en" or "nm" => "edmn", "ex" or "xm" => "edmx", "el" => "edln", "rn" => "rang", "eo" => "edns", _ => "edxs"
        });
    }
}

/// <summary>misscalc.txt codes for a missiles.txt row, at the level of the skill that fired it.</summary>
public sealed class MissileCalcScope(CalcContext context, JsonObject row, int level, SkillCalcScope? owner) : CalcScope(context, row, level)
{
    private static readonly Dictionary<string, (string Kind, string A, string B)> Curves = new()
    {
        ["sl12"] = ("ln", "Param1", "Param2"), ["sd12"] = ("dm", "Param1", "Param2"), ["sl34"] = ("ln", "Param3", "Param4"), ["sd34"] = ("dm", "Param3", "Param4"),
        ["cl12"] = ("ln", "CltParam1", "CltParam2"), ["cd12"] = ("dm", "CltParam1", "CltParam2"), ["cl34"] = ("ln", "CltParam3", "CltParam4"), ["cd34"] = ("dm", "CltParam3", "CltParam4"),
        ["shl1"] = ("ln", "sHitPar1", "sHitPar2"), ["shd1"] = ("dm", "sHitPar1", "sHitPar2"), ["chl1"] = ("ln", "cHitPar1", "cHitPar2"), ["chd1"] = ("dm", "cHitPar1", "cHitPar2"),
        ["dl12"] = ("ln", "dParam1", "dParam2"), ["dd12"] = ("dm", "dParam1", "dParam2")
    };
    protected override string Identity => "missiles/" + Row.S("Missile");
    protected override SkillCalcScope? Owner => owner;
    internal LevelCurve ElementalMin => PreviewMath.Curve(Row, "EMin", i => "MinELev" + i, DamageTiers);
    internal LevelCurve ElementalMax => PreviewMath.Curve(Row, "EMax", i => "MaxELev" + i, DamageTiers);
    internal LevelCurve PhysicalMin => PreviewMath.Curve(Row, "MinDamage", i => "MinLevDam" + i, DamageTiers);
    internal LevelCurve PhysicalMax => PreviewMath.Curve(Row, "MaxDamage", i => "MaxLevDam" + i, DamageTiers);
    internal LevelCurve ElementalLength => PreviewMath.Curve(Row, "ELen", i => "ELevLen" + i, LengthTiers);

    public override long Code(string code)
    {
        if (Curves.TryGetValue(code, out var curve)) return Curve(curve.Kind == "dm", Whole(curve.A), Whole(curve.B));
        var match = Regex.Match(code, "^(par|cpa|hpa|chp|dpa)([1-5])$");
        if (match.Success)
        {
            var column = match.Groups[1].Value switch { "par" => "Param", "cpa" => "CltParam", "hpa" => "sHitPar", "chp" => "cHitPar", _ => "dParam" };
            return Whole(column + match.Groups[2].Value);
        }
        switch (code)
        {
            case "lvl": return Level;
            case "edmn": return Damage256(ElementalMin, "EDmgSymPerCalc") >> 8;
            case "edmx": return Damage256(ElementalMax, "EDmgSymPerCalc") >> 8;
            case "edns": return Damage256(ElementalMin, "EDmgSymPerCalc");
            case "edxs": return Damage256(ElementalMax, "EDmgSymPerCalc");
            case "edln": return Length(ElementalLength, "");
            case "damn": return Damage256(PhysicalMin, "DmgSymPerCalc") >> 8;
            case "damx": return Damage256(PhysicalMax, "DmgSymPerCalc") >> 8;
            case "dmns": return Damage256(PhysicalMin, "DmgSymPerCalc");
            case "dmxs": return Damage256(PhysicalMax, "DmgSymPerCalc");
            case "rang": return Field("Range");
            case "rad": return Field("Radius");
            case "ccl1": return Field("CltCalc1");
            case "scl1": return Field("SrvCalc1");
            case "fnum":
                Context.Note("The missile's frame number (fnum) changes while it flies; the preview takes 0.");
                return 0;
        }
        throw Unknown(code, "misscalc");
    }
}
