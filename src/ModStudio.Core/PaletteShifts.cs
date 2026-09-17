using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// The colour-shift ("hue variation") tables of a Diablo II act palette file, <c>data/global/palette/&lt;act&gt;/pal.pl2</c>.
/// A monster's TransLvl (monstats) and a super unique's Utrans (superuniques) name one of these tables; the game recolours
/// the unit's sprite by pushing every palette index through it. Seeing each table applied to the act's base palette is
/// what tells a modder which number is "a good blue" without launching the game once per value.
/// No palette data ships with Studio: the file comes from the user's own extracted game data.
/// </summary>
public sealed class PaletteShifts
{
    /// <summary>Number of hue variation tables in a pal.pl2.</summary>
    public const int Tables = 111;
    /// <summary>Byte offset of the first hue variation table: base palette (1024) + 32 light levels + 16 inventory colours + selected-unit shift + 3 alpha blends + additive + multiplicative blend tables.</summary>
    public const int HueOffset = 1024 + 32 * 256 + 16 * 256 + 256 + 3 * 65536 + 65536 + 65536;
    /// <summary>Size of a complete pal.pl2 as the game ships it.</summary>
    public const int FileSize = 443188;
    private readonly byte[] bytes;
    private PaletteShifts(byte[] bytes) => this.bytes = bytes;

    public static PaletteShifts Load(byte[] bytes)
    {
        Require(bytes.Length >= HueOffset + Tables * 256, $"Not a pal.pl2 palette file: expected at least {HueOffset + Tables * 256:N0} bytes, got {bytes.Length:N0}.");
        return new(bytes);
    }
    public static PaletteShifts Load(string file) => Load(File.ReadAllBytes(file));

    /// <summary>The act's base palette entry, stored as RGBA.</summary>
    public (byte R, byte G, byte B) Base(int index) => (bytes[index * 4], bytes[index * 4 + 1], bytes[index * 4 + 2]);
    /// <summary>The palette index a hue table maps <paramref name="index"/> to.</summary>
    public byte Shift(int table, int index) => bytes[HueOffset + table * 256 + index];
    /// <summary>The base palette pushed through a hue table: what a sprite drawn with that table looks like, colour by colour. Table -1 is the palette unshifted.</summary>
    public (byte R, byte G, byte B)[] Transformed(int table)
    {
        Require(table >= -1 && table < Tables, $"Colour transform {table} is outside 0–{Tables - 1}.");
        var colors = new (byte R, byte G, byte B)[256];
        for (int i = 0; i < 256; i++) colors[i] = Base(table < 0 ? i : Shift(table, i));
        return colors;
    }
    /// <summary>Whether a hue table changes anything at all; identity tables are worth pointing out in a picker.</summary>
    public bool IsIdentity(int table) { for (int i = 0; i < 256; i++) if (Shift(table, i) != i) return false; return true; }

    /// <summary>Columns whose value is a hue table number.</summary>
    public static bool IsTransformColumn(string table, string column) => table switch
    {
        "monstats" => column.Equals("TransLvl", StringComparison.OrdinalIgnoreCase),
        "superuniques" => column.StartsWith("Utrans", StringComparison.OrdinalIgnoreCase),
        _ => false
    };
    /// <summary>Looks for an act palette under the usual native layout beneath each root (the project's data folder, a deployed mod, an extracted game).</summary>
    public static string? Find(IEnumerable<string> roots)
    {
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var palette = Path.Combine(root, "data", "global", "palette");
            if (!Directory.Exists(palette)) palette = Path.Combine(root, "global", "palette");
            if (!Directory.Exists(palette)) continue;
            foreach (var act in new[] { "ACT1", "ACT2", "ACT3", "ACT4", "ACT5" })
            {
                var candidate = Directory.EnumerateDirectories(palette).FirstOrDefault(d => Path.GetFileName(d).Equals(act, StringComparison.OrdinalIgnoreCase));
                if (candidate == null) continue;
                var file = Directory.EnumerateFiles(candidate).FirstOrDefault(f => Path.GetFileName(f).Equals("pal.pl2", StringComparison.OrdinalIgnoreCase));
                if (file != null) return file;
            }
        }
        return null;
    }
}
