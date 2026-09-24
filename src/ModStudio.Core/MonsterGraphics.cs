using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>An animation mode a monster can be in: its two-letter code and what it is.</summary>
public sealed record MonsterMode(string Code, string Name, bool Moves = false, bool Once = false);

/// <summary>
/// How a monster looks in legacy graphics, from its monstats Code (the graphics token) and TransLvl, and its monstats2 row:
/// the variants each body part can be drawn in, which parts it has, its base weapon class, the modes it can play, and its
/// shadow and light.
/// </summary>
public sealed record MonsterLook(string Token, string BaseWeapon, IReadOnlyDictionary<string, string[]> Variants, IReadOnlySet<string> Parts, string[] Modes,
    int TransLvl, bool Shadow, int Light, byte Red, byte Green, byte Blue)
{
    /// <summary>The most looks any part offers: the number of distinct variants to page through.</summary>
    public int LookCount => Math.Max(1, Variants.Values.Select(v => v.Length).DefaultIfEmpty(1).Max());
}

/// <summary>One drawn layer of a composite: the COF layer, the variant it was drawn in, and the DCC or DC6 it came from.</summary>
public sealed record MonsterLayer(CofLayer Layer, string Variant, HdFile? File, LegacyAnimation? Animation);

/// <summary>A monster mode ready to draw: its COF, its layers' animations, the colour shift, and what could not be found.</summary>
public sealed record MonsterComposite(string Mode, HdFile? CofFile, Cof? Cof, MonsterLayer[] Layers, byte[]? Shift, string[] Notes)
{
    public bool Drawable => Cof != null && Layers.Any(l => l.Animation != null);
}

/// <summary>One monster as the builder's search lists it.</summary>
public sealed record MonsterEntry(int Row, string SourceId, string Id, string Name, string Code, string MonType, string Levels)
{
    public bool Inactive => Id.Length == 0;
    public string SearchText { get; } = string.Join(' ', Id, Name, Code, MonType).ToLowerInvariant();
}

/// <summary>A monstats row's identity, read on the UI thread for the search list.</summary>
public sealed record MonsterRow(int Row, string SourceId, string Id, string NameStr, string Code, string MonType, string Levels);

public sealed record MonsterBuilderCatalog(MonsterEntry[] Entries, string[] Tokens, string[] MonTypes, string[] Missiles, string[] Skills, string[] TreasureClasses,
    string[] MonSounds, string[] MonProps, string[] Looks, string[] Issues);

/// <summary>
/// Finds and assembles legacy monster graphics. A mode's COF is data/global/monsters/&lt;token&gt;/cof/&lt;token&gt;&lt;mode&gt;&lt;weapon&gt;.cof;
/// each of its layers is a DCC at &lt;token&gt;/&lt;part&gt;/&lt;token&gt;&lt;part&gt;&lt;variant&gt;&lt;mode&gt;&lt;weapon&gt;.dcc, drawn in the order the COF gives
/// per direction and frame. cof/palshift.dat holds the colour shifts TransLvl picks. Files come from the project first,
/// then the user's extracted game data.
/// </summary>
public static class MonsterGraphics
{
    public static readonly MonsterMode[] Modes = [
        new("NU", "Neutral"), new("WL", "Walk", Moves: true), new("RN", "Run", Moves: true), new("A1", "Attack 1"), new("A2", "Attack 2"), new("SC", "Cast"),
        new("S1", "Skill 1"), new("S2", "Skill 2"), new("S3", "Skill 3"), new("S4", "Skill 4"), new("GH", "Get hit"), new("BL", "Block"), new("KB", "Knockback"),
        new("SQ", "Sequence"), new("DT", "Death", Once: true), new("DD", "Dead")];
    /// <summary>monstats2 names the Lav/Rav columns with a lower-case second letter.</summary>
    private static string VariantColumn(string part) => part switch { "RA" => "Rav", "LA" => "Lav", _ => part + "v" };

    /// <summary>The look a monstats row and its monstats2 row describe. <paramref name="monstats2"/> reads that row's cells.</summary>
    public static MonsterLook Look(string token, int transLvl, Func<string, string> monstats2)
    {
        var variants = new Dictionary<string, string[]>(StringComparer.Ordinal); var parts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in Cof.Components)
        {
            // Lists are sometimes spreadsheet-quoted ("lit,med") in vanilla data.
            var list = monstats2(VariantColumn(part)).Replace("\"", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (list.Length > 0) variants[part] = list;
            if (monstats2(part) == "1") parts.Add(part);
        }
        var modes = Modes.Where(m => monstats2("m" + m.Code) == "1").Select(m => m.Code).ToArray();
        int Int(string column) => int.TryParse(monstats2(column), out var value) ? value : 0;
        byte Channel(string column) => (byte)Math.Clamp(int.TryParse(monstats2(column), out var value) ? value : 255, 0, 255);
        return new(token.Trim(), monstats2("BaseW") is { Length: > 0 } weapon ? weapon : "hth", variants, parts, modes, transLvl, monstats2("Shadow") == "1", Int("Light"),
            Channel("light-r"), Channel("light-g"), Channel("light-b"));
    }

    /// <summary>
    /// Assembles one mode. <paramref name="look"/> picks each part's variant: the look-th in its list, wrapping. A part
    /// whose chosen variant has no file falls back to any variant that has one.
    /// </summary>
    public static MonsterComposite Composite(ModProject project, IReadOnlyList<string> gameData, MonsterLook monster, string mode, int look, CancellationToken token = default)
    {
        var notes = new List<string>(); var t = monster.Token.ToLowerInvariant(); mode = mode.ToLowerInvariant();
        if (t.Length == 0) return new(mode, null, null, [], null, ["No Code: the monster has no graphics token."]);
        var cofFile = HdAppearance.Find(project, gameData, $"data/global/monsters/{t}/cof/{t}{mode}{monster.BaseWeapon.ToLowerInvariant()}.cof");
        if (cofFile == null)
        {
            // A mode drawn with another weapon class than BaseW still has a COF for this token and mode.
            foreach (var root in gameData.Prepend(project.Root))
                if (HdAppearance.LocateFolder(root, $"data/global/monsters/{t}/cof") is { } folder
                    && Directory.EnumerateFiles(folder, $"{t}{mode}*.cof").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } any)
                { cofFile = HdAppearance.Find(project, gameData, $"data/global/monsters/{t}/cof/{Path.GetFileName(any)}"); break; }
        }
        if (cofFile == null)
            return new(mode, null, null, [], null, [gameData.Count == 0
                ? $"No {t}{mode} animation in the project. Choose your extracted game data folder to show the base game's monster."
                : $"data/global/monsters/{t}/cof/{t}{mode}{monster.BaseWeapon}.cof is not in the project or the game data."]);
        Cof cof;
        try { cof = Cof.Read(File.ReadAllBytes(cofFile.Path)); }
        catch (InvalidDataException e) { return new(mode, cofFile, null, [], null, [$"{cofFile.Relative}: {e.Message}"]); }
        var layers = new List<MonsterLayer>();
        foreach (var layer in cof.Layers)
        {
            token.ThrowIfCancellationRequested();
            var part = layer.ComponentCode; var p = part.ToLowerInvariant(); var weapon = (layer.WeaponClass.Length > 0 ? layer.WeaponClass : monster.BaseWeapon).ToLowerInvariant();
            var choices = monster.Variants.GetValueOrDefault(part) ?? [];
            var ordered = choices.Length == 0 ? new List<string> { "lit" } : [.. choices.Skip(look % choices.Length), .. choices.Take(look % choices.Length)];
            HdFile? file = null; string variant = ordered[0];
            // Most parts are DCC; a few monsters (Mephisto's body) still use DC6.
            foreach (var candidate in ordered)
            {
                foreach (var extension in new[] { "dcc", "dc6" })
                    if ((file = HdAppearance.Find(project, gameData, $"data/global/monsters/{t}/{p}/{t}{p}{candidate.ToLowerInvariant()}{mode}{weapon}.{extension}")) != null) break;
                if (file != null) { variant = candidate; break; }
            }
            if (file == null)
            {
                notes.Add($"{part}: no {t}{p}{ordered[0]}{mode}{weapon}.dcc or .dc6");
                layers.Add(new(layer, variant, null, null)); continue;
            }
            try { layers.Add(new(layer, variant, file, LegacyAnimation.Open(file))); }
            catch (InvalidDataException e) { notes.Add($"{file.Relative}: {e.Message}"); layers.Add(new(layer, variant, file, null)); }
        }
        return new(mode, cofFile, cof, [.. layers], Shift(project, gameData, t, monster.TransLvl), [.. notes]);
    }

    /// <summary>
    /// The palette shift TransLvl selects from the token's cof/palshift.dat: table TransLvl + 2, since the first tables
    /// leave the palette as it is. Null when the file or table is missing, or TransLvl is 0.
    /// </summary>
    public static byte[]? Shift(ModProject project, IReadOnlyList<string> gameData, string token, int transLvl)
    {
        if (transLvl <= 0) return null;
        var file = HdAppearance.Find(project, gameData, $"data/global/monsters/{token.ToLowerInvariant()}/cof/palshift.dat");
        if (file == null) return null;
        var bytes = File.ReadAllBytes(file.Path); int table = transLvl + 2;
        return bytes.Length >= (table + 1) * 256 ? bytes.AsSpan(table * 256, 256).ToArray() : null;
    }

    /// <summary>Graphics tokens available: the monster folders in the project and the game data.</summary>
    public static string[] Tokens(ModProject project, IReadOnlyList<string> gameData)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in gameData.Prepend(project.Root))
            if (HdAppearance.LocateFolder(root, "data/global/monsters") is { } folder)
                foreach (var directory in Directory.EnumerateDirectories(folder)) names.Add(Path.GetFileName(directory).ToUpperInvariant());
        return [.. names];
    }

    public static MonsterRow[] Rows(TableData table)
    {
        string Cell(int row, string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        return [.. Enumerable.Range(0, table.Records.Count).Select(i => new MonsterRow(i, table.Records[i].S("sourceId"), Cell(i, "Id"), Cell(i, "NameStr"), Cell(i, "Code"), Cell(i, "MonType"),
            string.Join("/", new[] { "Level", "Level(N)", "Level(H)" }.Select(c => Cell(i, c)).Where(v => v.Length > 0))))];
    }
}

/// <summary>What the monster builder reads besides the row being edited. Worker-owned; reads authored data only.</summary>
public sealed class MonsterBuilderResolver
{
    private readonly PreviewTables tables = new();
    public void Clear() => tables.Clear();

    public MonsterBuilderCatalog Catalog(ModProject project, string profile, string locale, IReadOnlyList<string> gameData, IReadOnlyList<MonsterRow> rows, CancellationToken token)
    {
        var issues = new List<string>();
        var data = tables.Open(project, profile, locale, issues, token);
        string[] Column(string table, string column) => [.. data.Rows(table, false).Select(r => r.S(column)).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)];
        var entries = rows.Select(r => new MonsterEntry(r.Row, r.SourceId, r.Id, r.NameStr.Length == 0 ? "" : data.Localize(r.NameStr, false), r.Code, r.MonType, r.Levels)).ToArray();
        token.ThrowIfCancellationRequested();
        return new(entries, MonsterGraphics.Tokens(project, gameData), Column("montype", "type"), Column("missiles", "Missile"), Column("skills", "skill"),
            Column("treasureclassex", "Treasure Class"), Column("monsounds", "Id"), Column("monprop", "Id"), Column("monstats2", "Id"), [.. issues.Distinct()]);
    }
}
