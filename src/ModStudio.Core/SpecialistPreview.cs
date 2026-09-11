using System.Buffers.Binary;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record PreviewPixels(int Width, int Height, byte[] Rgba);
public record PreviewAsset(string Summary, string[] Frames, Func<int, PreviewPixels> Decode, int InitialFrame = 0, bool UsesPalette = false);

/// <summary>Read-only, bounded decoders. No game assets or palettes are distributed.</summary>
public static partial class SpecialistPreview
{
    public static bool Supports(string file) => Path.GetExtension(file).ToLowerInvariant() is ".sprite" or ".texture" or ".dds" or ".dc6" or ".ds1";
    public static PreviewAsset Load(string file, byte[]? palette = null)
    {
        NoLinks(file); using var stream = File.OpenRead(file);
        Require(stream.Length <= 64 * 1024 * 1024, "Preview limit is 64 MiB. Use the hex view to inspect this file.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        return Path.GetExtension(file).ToLowerInvariant() switch {
            ".sprite" => Sprite(bytes), ".texture" => Texture(bytes), ".dc6" => Dc6(bytes, palette), ".ds1" => Ds1(bytes), ".dds" => Dds(bytes),
            _ => throw new NotSupportedException("No visual decoder for this format.") };
    }
    private static int Int(byte[] b, int offset) { Range(b, offset, 4); return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset, 4)); }
    private static int Short(byte[] b, int offset) { Range(b, offset, 2); return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset, 2)); }
    private static void Range(byte[] b, int offset, int length) => Require(offset >= 0 && length >= 0 && (long)offset + length <= b.Length, "Truncated or invalid asset data range.");
    private static int Pixels(int w, int h) { Require(w > 0 && h > 0 && w <= 32768 && h <= 32768 && (long)w * h <= 16 * 1024 * 1024, "Image exceeds the 16-megapixel preview limit or has invalid dimensions."); return checked(w * h); }
    private static PreviewPixels Decode(byte[] data, int offset, int size, int w, int h, int format)
    {
        int count = Pixels(w, h); Range(data, offset, size);
        if (format == 31) { Require(size == count * 4, "Invalid RGBA payload size."); return new(w, h, data.AsSpan(offset, size).ToArray()); }
        var compression = format switch { 57 or 58 => CompressionFormat.Bc1, 61 or 62 => CompressionFormat.Bc3, 63 => CompressionFormat.Bc4, _ => throw new NotSupportedException($"Texture compression {format} is not supported.") };
        var expected = checked(((w + 3) / 4) * ((h + 3) / 4) * (compression is CompressionFormat.Bc1 or CompressionFormat.Bc4 ? 8 : 16));
        Require(size == expected, "Compressed payload length does not match image dimensions.");
        var colors = new BcDecoder().DecodeRaw(data.AsSpan(offset, size).ToArray(), w, h, compression);
        var rgba = new byte[count * 4];
        for (int i = 0; i < count; i++) { rgba[i * 4] = colors[i].r; rgba[i * 4 + 1] = colors[i].g; rgba[i * 4 + 2] = colors[i].b; rgba[i * 4 + 3] = colors[i].a; }
        return new(w, h, rgba);
    }
    private static PreviewAsset Sprite(byte[] b)
    {
        Range(b, 0, 40); Require(Int(b, 0) == 0x31417053, "Not a SpA1 sprite.");
        int format = Short(b, 4), frameWidth = Short(b, 6), w = Int(b, 8), h = Int(b, 12), frames = Int(b, 20);
        Pixels(w, h); Require(frames is > 0 and <= 4096 && w % frames == 0 && frameWidth > 0 && frameWidth <= w / frames, "Unsupported sprite frame layout.");
        int length = format == 31 ? checked(w * h * 4) : checked(((w + 3) / 4) * ((h + 3) / 4) * (format is 57 or 58 or 63 ? 8 : 16)); Range(b, 40, length);
        return new($"D2R sprite · {w} × {h} atlas · {frames} frames · format {format}", new[] { "Full atlas" }.Concat(Enumerable.Range(0, frames).Select(i => $"Frame {i + 1}")).ToArray(), index => {
            Require(index >= 0 && index <= frames, "Invalid sprite frame.");
            if (index == 0) return Decode(b, 40, length, w, h, format);
            // Uncompressed sprites can crop directly from the encoded bytes, avoiding a full atlas copy.
            var source = format == 31 ? b : Decode(b, 40, length, w, h, format).Rgba;
            int sourceOffset = format == 31 ? 40 : 0;
            var result = new byte[checked(frameWidth * h * 4)];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(source, sourceOffset + (y * w + (index - 1) * (w / frames)) * 4, result, y * frameWidth * 4, frameWidth * 4);
            return new(frameWidth, h, result);
        });
    }
    private static PreviewAsset Texture(byte[] b)
    {
        Range(b, 0, 44); Require(Int(b, 0) == 0x2845443c, "Unknown D2R texture signature.");
        int format = Short(b, 4), w = Int(b, 8), h = Int(b, 12), mips = Int(b, 28);
        Require(w > 0 && h > 0 && w <= 32768 && h <= 32768 && mips is >= 1 and <= 16, "Invalid texture dimensions or mip count."); Range(b, 36, mips * 8);
        var names = Enumerable.Range(0, mips).Select(i => $"Mip {i} · {Math.Max(1, w >> i)} × {Math.Max(1, h >> i)}").ToArray(); int initial = 0;
        while (initial + 1 < mips && Math.Max(w >> initial, h >> initial) > 1024) initial++;
        return new($"D2R texture · {w} × {h} · {mips} mip levels · format {format}", names, mip => {
            Require(mip >= 0 && mip < mips, "Invalid mip level."); int field = 40 + mip * 8, offset = checked(field + Int(b, field));
            Require(offset >= 36 + mips * 8, "Texture payload overlaps the header.");
            return Decode(b, offset, Int(b, field - 4), Math.Max(1, w >> mip), Math.Max(1, h >> mip), format);
        }, initial);
    }
    private static PreviewAsset Dds(byte[] b)
    {
        Range(b, 0, 128); Require(Int(b, 0) == 0x20534444 && Int(b, 4) == 124, "Invalid DDS header.");
        int w = Int(b, 16), h = Int(b, 12); Pixels(w, h);
        Require(Int(b, 28) is >= 0 and <= 16, "Invalid DDS mip count.");
        if (Int(b, 84) == 0x30315844) { Range(b, 128, 20); Require(Int(b, 132) == 3 && Int(b, 140) == 1 && (Int(b, 136) & 4) == 0, "DDS arrays, volumes and cubemaps are not supported."); }
        // Reject arrays, volumes and cubemaps rather than silently showing an arbitrary surface.
        Require(Int(b, 24) <= 1 && (Int(b, 112) & 0xFE00) == 0, "DDS volumes and cubemaps are not supported in this preview.");
        return new($"DDS texture · {w} × {h} · first surface / base mip", ["Base mip"], _ => {
            using var input = new MemoryStream(b, false);
            var colors = new BcDecoder().Decode(BCnEncoder.Shared.ImageFiles.DdsFile.Load(input)); var rgba = new byte[checked(w * h * 4)];
            Require(colors.Length == w * h, "Unsupported DDS surface layout.");
            for (int i = 0; i < colors.Length; i++) { rgba[i * 4] = colors[i].r; rgba[i * 4 + 1] = colors[i].g; rgba[i * 4 + 2] = colors[i].b; rgba[i * 4 + 3] = colors[i].a; }
            return new(w, h, rgba);
        });
    }
}
