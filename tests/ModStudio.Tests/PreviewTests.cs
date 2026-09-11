using System.Buffers.Binary;
using ModStudio.Core;

internal static class PreviewTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        string Save(string name, byte[] bytes) { var path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
        void Put(byte[] b, int o, int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o, 4), value);
        var sprite = new byte[72]; Put(sprite, 0, 0x31417053); Put(sprite, 4, 31 | (2 << 16)); Put(sprite, 8, 4); Put(sprite, 12, 2); Put(sprite, 20, 2);
        for (int i = 40; i < sprite.Length; i += 4) { sprite[i] = (byte)(i - 40); sprite[i + 3] = 255; }
        var spriteFile = Save("fixture.sprite", sprite); var asset = SpecialistPreview.Load(spriteFile);
        check(asset.Frames.Length == 3 && asset.Decode(0).Width == 4 && asset.Decode(2).Rgba[0] == 8 && asset.Decode(2).Rgba[8] == 24, "Sprite atlas and frame cropping preserve pixel rows");
        var texture = new byte[52]; Put(texture, 0, 0x2845443c); Put(texture, 4, 31); Put(texture, 8, 2); Put(texture, 12, 1); Put(texture, 28, 1); Put(texture, 36, 8); Put(texture, 40, 4); texture[44] = 123; texture[47] = 255;
        check(SpecialistPreview.Load(Save("fixture.texture", texture)).Decode(0).Rgba[0] == 123, "Texture mip offset is relative to its offset field");
        Put(texture, 40, int.MaxValue); throws(() => SpecialistPreview.Load(Save("bad.texture", texture)).Decode(0), "Out-of-range texture payload rejected");
        byte[] runs = [2, 1, 2, 128, 129, 1, 3, 128]; var dc6 = new byte[60 + runs.Length]; Put(dc6, 0, 6); Put(dc6, 16, 1); Put(dc6, 20, 1); Put(dc6, 24, 28); Put(dc6, 32, 2); Put(dc6, 36, 2); Put(dc6, 56, runs.Length); runs.CopyTo(dc6, 60);
        var dc6File = Save("fixture.dc6", dc6); var palette = new byte[768]; palette[3] = 1; palette[4] = 2; palette[5] = 3;
        var frame = SpecialistPreview.Load(dc6File, palette).Decode(0);
        check(frame.Rgba[3] == 0 && frame.Rgba[8] == 3 && frame.Rgba[9] == 2 && frame.Rgba[10] == 1 && frame.Rgba[11] == 255, "DC6 bottom-up runs, transparent skips and BGR palette decode correctly");
        check(SpecialistPreview.Load(dc6File).Summary.Contains("not game-accurate"), "Palette-free DC6 preview identifies grayscale fallback");
        throws(() => SpecialistPreview.Load(dc6File, new byte[10]), "Invalid palette size rejected");
        var ds1 = new byte[96]; Put(ds1, 0, 18); Put(ds1, 4, 1); Put(ds1, 8, 1); Put(ds1, 24, 1); Put(ds1, 28, 1); Put(ds1, 64, 1); Put(ds1, 68, 0x20001);
        var ds1File = Save("fixture.ds1", ds1); var map = SpecialistPreview.Load(ds1File); frame = map.Decode(0);
        check(map.Frames.Length == 3 && frame.Width == 2 && frame.Height == 2 && frame.Rgba[0] == 50 && frame.Rgba[4] == 225, "DS1 overview distinguishes occupied floor and explicit blocked flag");
        var dds = new byte[136]; Put(dds, 0, 0x20534444); Put(dds, 4, 124); Put(dds, 8, 0x81007); Put(dds, 12, 4); Put(dds, 16, 4); Put(dds, 20, 8); Put(dds, 76, 32); Put(dds, 80, 4); Put(dds, 84, 0x31545844); Put(dds, 108, 0x1000); dds[129] = 0xf8;
        frame = SpecialistPreview.Load(Save("fixture.dds", dds)).Decode(0);
        check(frame.Width == 4 && frame.Rgba[0] > 240 && frame.Rgba[1] == 0, "DDS BC1 surface decodes to expected red pixels");
        foreach (var pair in new[] { ("sprite", sprite), ("dc6", dc6), ("ds1", ds1), ("dds", dds) })
            throws(() => SpecialistPreview.Load(Save("truncated." + pair.Item1, pair.Item2[..12])).Decode(0), "Truncated " + pair.Item1 + " rejected safely");
        Put(sprite, 8, int.MaxValue); throws(() => SpecialistPreview.Load(Save("huge.sprite", sprite)), "Oversized sprite rejected before allocation");
        check(File.ReadAllBytes(dc6File).SequenceEqual(dc6) && File.ReadAllBytes(ds1File).SequenceEqual(ds1), "Preview loading never modifies source assets");
    }
}
