using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public static partial class SpecialistPreview
{
    private static PreviewAsset Dc6(byte[] b, byte[]? palette)
    {
        Range(b, 0, 24); Require(Int(b, 0) == 6 && Int(b, 8) == 0, "Only uncompressed DC6 version 6 is supported.");
        int directions = Int(b, 16), perDirection = Int(b, 20);
        Require(directions is > 0 and <= 32 && perDirection is > 0 and <= 4096 && (long)directions * perDirection <= 4096, "Invalid DC6 frame count.");
        int count = directions * perDirection; Range(b, 24, count * 4);
        Require(palette == null || palette.Length == 768, "Select a 768-byte Diablo II pal.dat palette (256 BGR colors).");
        return new($"DC6 · {directions} directions · {perDirection} frames per direction\n" + (palette == null ? "Grayscale palette indices: colors are not game-accurate. Load a pal.dat palette for color." : "Colors use the selected pal.dat palette; act/UI palette choice affects appearance."),
            Enumerable.Range(0, count).Select(i => $"Direction {i / perDirection} · frame {i % perDirection}").ToArray(), frame => {
                Require(frame >= 0 && frame < count, "Invalid DC6 frame."); int offset = Int(b, 24 + frame * 4); Require(offset >= 24 + count * 4, "DC6 frame overlaps its header."); Range(b, offset, 32);
                int flip = Int(b, offset), w = Int(b, offset + 4), h = Int(b, offset + 8), length = Int(b, offset + 28);
                Require(flip is 0 or 1, "Unsupported DC6 flip flag."); var rgba = new byte[checked(Pixels(w, h) * 4)]; int start = offset + 32; Range(b, start, length);
                int row = 0, x = 0, pos = start;
                while (pos < start + length && row < h)
                {
                    int run = b[pos++]; if (run == 128) { row++; x = 0; continue; }
                    if ((run & 128) != 0) { x += run & 127; Require(x <= w, "DC6 transparent run exceeds row width."); continue; }
                    Require(run > 0 && x + run <= w && pos + run <= start + length, "Invalid DC6 pixel run.");
                    for (int i = 0; i < run; i++)
                    {
                        int color = b[pos++], y = flip == 0 ? h - 1 - row : row, pixel = (y * w + x++) * 4;
                        rgba[pixel] = palette == null ? (byte)color : palette[color * 3 + 2];
                        rgba[pixel + 1] = palette == null ? (byte)color : palette[color * 3 + 1];
                        rgba[pixel + 2] = palette == null ? (byte)color : palette[color * 3]; rgba[pixel + 3] = 255;
                    }
                }
                Require(row == h, "Truncated DC6 scanlines."); return new(w, h, rgba);
            }, UsesPalette: true);
    }
    private static PreviewAsset Ds1(byte[] b)
    {
        using var reader = new BinaryReader(new MemoryStream(b, false));
        int version = reader.ReadInt32(); Require(version is >= 16 and <= 18, "DS1 layer overview currently supports versions 16–18.");
        int w = checked(reader.ReadInt32() + 1), h = checked(reader.ReadInt32() + 1), act = checked(reader.ReadInt32() + 1), tag = reader.ReadInt32();
        Require(w is > 0 and <= 512 && h is > 0 and <= 512 && act is >= 1 and <= 5, "Invalid DS1 dimensions or act.");
        int files = reader.ReadInt32(); Require(files is >= 0 and <= 1024, "Invalid DS1 dependency count."); var dependencies = new List<string>();
        for (int i = 0; i < files; i++)
        {
            var name = new List<byte>(); byte c;
            while ((c = reader.ReadByte()) != 0) { Require(name.Count < 4096, "Unterminated DS1 dependency name."); name.Add(c); }
            dependencies.Add(System.Text.Encoding.Latin1.GetString(name.ToArray()));
        }
        int walls = reader.ReadInt32(), floors = reader.ReadInt32(); Require(walls is >= 1 and <= 4 && floors is >= 1 and <= 2, "Invalid DS1 layers.");
        int cells = w * h, size = cells * 4, position = (int)reader.BaseStream.Position;
        Range(b, position, checked(size * (walls * 2 + floors + 1 + (tag is 1 or 2 ? 1 : 0))));
        var layers = new List<(string Name, int Offset, bool Wall)>();
        for (int i = 0; i < walls; i++) { layers.Add(($"Wall {i + 1}", position, true)); position += size * 2; }
        for (int i = 0; i < floors; i++) { layers.Add(($"Floor {i + 1}", position, false)); position += size; }
        return new($"DS1 v{version} · act {act} · {w} × {h} tiles\nTop-down layer occupancy, not rendered terrain or full collision. Teal: floor. Gold: wall. Red: explicit unwalkable flag. Dark: empty.\nDependencies (not loaded):\n" + string.Join("\n", dependencies),
            new[] { "Combined layers" }.Concat(layers.Select(l => l.Name)).ToArray(), index => {
                Require(index >= 0 && index <= layers.Count, "Invalid DS1 layer."); var rgba = new byte[cells * 4];
                var selectedLayers = index == 0 ? layers.OrderBy(l => l.Wall).ToArray() : [layers[index - 1]];
                for (int i = 0; i < cells; i++)
                {
                    byte r = 30, g = 32, blue = 35;
                    foreach (var layer in selectedLayers)
                    {
                        uint cell = unchecked((uint)Int(b, layer.Offset + i * 4)); if ((cell & 255) == 0) continue;
                        if ((cell & 0x20000) != 0) { r = 225; g = 65; blue = 55; break; }
                        (r, g, blue) = layer.Wall ? ((byte)220, (byte)166, (byte)67) : ((byte)50, (byte)160, (byte)155);
                    }
                    rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = blue; rgba[i * 4 + 3] = 255;
                }
                return new(w, h, rgba);
            });
    }
}
