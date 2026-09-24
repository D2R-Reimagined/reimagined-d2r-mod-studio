using System.Buffers.Binary;
using System.Text;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>One layer of a composite animation: the body part it draws, its weapon class, and whether and how it blends.</summary>
public sealed record CofLayer(int Component, bool Shadow, bool Transparent, int DrawEffect, string WeaponClass)
{
    public string ComponentCode => Cof.Components[Component];
    /// <summary>The blend a transparent layer is drawn with (DrawEffect 0–2 are 25/50/75 % opacity, 3–4 add light); opaque layers draw normally.</summary>
    public Blend Blend => !Transparent ? Blend.Normal : DrawEffect switch { 0 => Blend.Quarter, 1 => Blend.Half, 2 => Blend.ThreeQuarters, 3 or 4 => Blend.Add, 5 => Blend.Normal, _ => Blend.Half };
}

/// <summary>
/// A COF: how one mode of a unit is assembled from body-part animations. It names the layers, how many directions and
/// frames the mode has, its speed (256 is one animation frame per game frame), the event on each frame (1 is where an
/// attack lands), and for every direction and frame the order the layers are drawn in.
/// </summary>
public sealed record Cof(CofLayer[] Layers, int Directions, int FramesPerDirection, int Speed, byte[] Events, int[][][] Order)
{
    /// <summary>Component codes in COF order: head, torso, legs, arms, hands, shield, then special parts S1–S8.</summary>
    public static readonly string[] Components = ["HD", "TR", "LG", "RA", "LA", "RH", "LH", "SH", "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8"];

    public static Cof Read(byte[] data)
    {
        Require(data.Length >= 28, "Not a COF file.");
        int layers = data[0], frames = data[1], directions = data[2];
        Require(layers is > 0 and <= 16 && frames is > 0 && directions is > 0 and <= 64, "Invalid COF layer, frame or direction count.");
        int speed = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));
        int offset = 28;
        Require(data.Length >= offset + layers * 9 + frames + directions * frames * layers, "Truncated COF file.");
        var parsed = new CofLayer[layers];
        for (int i = 0; i < layers; i++, offset += 9)
        {
            Require(data[offset] < Components.Length, "Unknown COF component.");
            parsed[i] = new(data[offset], data[offset + 1] != 0, data[offset + 3] != 0, data[offset + 4], Encoding.ASCII.GetString(data, offset + 5, 4).TrimEnd('\0', ' '));
        }
        var events = data.AsSpan(offset, frames).ToArray(); offset += frames;
        var order = new int[directions][][];
        for (int d = 0; d < directions; d++)
        {
            order[d] = new int[frames][];
            for (int f = 0; f < frames; f++) { order[d][f] = [.. data.AsSpan(offset, layers).ToArray().Select(b => (int)b)]; offset += layers; }
        }
        return new(parsed, directions, frames, speed is > 0 and <= 4096 ? speed : 256, events, order);
    }
}

/// <summary>
/// A legacy animation (DCC, or the older DC6 some monster parts still use) whose directions are decoded when first
/// drawn: a monster mode needs only the facing on screen. Frames are palette indices over the direction's shared box.
/// </summary>
public sealed class LegacyAnimation
{
    private readonly byte[] data;
    private readonly bool dc6;
    private readonly DccDirection?[] directions;
    public int DirectionCount { get; }
    public int FramesPerDirection { get; }
    /// <summary>Bytes held by the file and the directions decoded so far, for the cache's budget.</summary>
    public long Retained { get; private set; }

    public LegacyAnimation(byte[] data)
    {
        dc6 = data.Length >= 24 && BitConverter.ToInt32(data, 0) == 6;
        if (dc6)
        {
            Require(BitConverter.ToInt32(data, 8) == 0, "Only uncompressed DC6 version 6 is supported.");
            DirectionCount = BitConverter.ToInt32(data, 16); FramesPerDirection = BitConverter.ToInt32(data, 20);
            Require(DirectionCount is > 0 and <= 32 && FramesPerDirection is > 0 and <= 256 && data.Length >= 24 + DirectionCount * FramesPerDirection * 4, "Invalid DC6 header.");
        }
        else
        {
            Require(data.Length >= 15 && data[0] == 0x74, "Not a DCC or DC6 animation.");
            DirectionCount = data[2]; FramesPerDirection = BitConverter.ToInt32(data, 3);
            Require(DirectionCount is > 0 and <= 32 && FramesPerDirection is > 0 and <= 256 && data.Length >= 15 + DirectionCount * 4, "Invalid DCC header.");
        }
        this.data = data; directions = new DccDirection?[DirectionCount]; Retained = data.Length;
    }

    public DccDirection Direction(int index)
    {
        index = Math.Clamp(index, 0, DirectionCount - 1);
        lock (directions)
        {
            if (directions[index] is { } done) return done;
            var decoded = dc6 ? Dc6Direction(index) : Dcc.DecodeDirection(data, index);
            Retained += decoded.Frames.Sum(f => (long)f.Indices.Length);
            return directions[index] = decoded;
        }
    }

    /// <summary>
    /// A DC6 direction as a DCC one: each DC6 frame has its own size and offset (its bottom row at offsetY), so the frames
    /// are placed in one box that holds them all, with the origin where the offsets meet.
    /// </summary>
    private DccDirection Dc6Direction(int direction)
    {
        var frames = new (int Width, int Height, int Left, int Top, byte[] Pixels)[FramesPerDirection];
        for (int f = 0; f < FramesPerDirection; f++)
        {
            int offset = BitConverter.ToInt32(data, 24 + (direction * FramesPerDirection + f) * 4);
            Require(offset >= 24 && offset + 32 <= data.Length, "DC6 frame is outside the file.");
            int flip = BitConverter.ToInt32(data, offset), w = BitConverter.ToInt32(data, offset + 4), h = BitConverter.ToInt32(data, offset + 8),
                x0 = BitConverter.ToInt32(data, offset + 12), y0 = BitConverter.ToInt32(data, offset + 16), length = BitConverter.ToInt32(data, offset + 28);
            Require(w is >= 0 and <= 2048 && h is >= 0 and <= 2048 && offset + 32 + length <= data.Length, "Invalid DC6 frame.");
            var pixels = new byte[w * h]; int row = 0, x = 0, pos = offset + 32, end = offset + 32 + length;
            while (pos < end && row < h)
            {
                int run = data[pos++];
                if (run == 0x80) { row++; x = 0; continue; }
                if ((run & 0x80) != 0) { x += run & 0x7F; continue; }
                for (int i = 0; i < run && pos < end; i++, x++) { byte color = data[pos++]; if (x < w) pixels[(flip == 0 ? h - 1 - row : row) * w + x] = color; }
            }
            frames[f] = (w, h, x0, y0 - h + 1, pixels);
        }
        int left = frames.Min(f => f.Left), top = frames.Min(f => f.Top), right = frames.Max(f => f.Left + f.Width), bottom = frames.Max(f => f.Top + f.Height);
        int boxWidth = Math.Max(1, right - left), boxHeight = Math.Max(1, bottom - top);
        var result = frames.Select(f =>
        {
            var indices = new byte[boxWidth * boxHeight];
            for (int y = 0; y < f.Height; y++) Buffer.BlockCopy(f.Pixels, y * f.Width, indices, (f.Top - top + y) * boxWidth + f.Left - left, f.Width);
            return new DccFrame(boxWidth, boxHeight, indices);
        }).ToArray();
        return new(-left, -top, result);
    }

    private static readonly LinkedList<(string Key, LegacyAnimation File)> cache = new();
    private const long Budget = 256L * 1024 * 1024;

    /// <summary>An animation read through a small byte-bounded cache keyed by file content.</summary>
    public static LegacyAnimation Open(HdFile file)
    {
        lock (cache)
        {
            for (var node = cache.First; node != null; node = node.Next)
                if (node.Value.Key == file.Hash) { cache.Remove(node); cache.AddFirst(node); return node.Value.File; }
        }
        var opened = new LegacyAnimation(File.ReadAllBytes(file.Path));
        lock (cache)
        {
            cache.AddFirst((file.Hash, opened));
            while (cache.Count > 1 && cache.Sum(c => c.File.Retained) > Budget) cache.RemoveLast();
        }
        return opened;
    }
}
