using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>One decoded DCC frame: palette indices (0 is transparent) over its direction's bounding box.</summary>
public sealed record DccFrame(int Width, int Height, byte[] Indices);

/// <summary>
/// One direction of a DCC animation. Every frame shares the direction's bounding box; <see cref="OriginX"/>/<see cref="OriginY"/>
/// is where the unit's position falls inside it, so frames line up when drawn at the same point.
/// </summary>
public sealed record DccDirection(int OriginX, int OriginY, DccFrame[] Frames);

/// <summary>
/// Decoder for Diablo II's DCC animations (the legacy graphics missiles, monsters and overlays are drawn with). A direction
/// is a bitstream of frame headers followed by five interleaved streams — equal cells, pixel masks, encoding types, raw
/// pixel codes, and pixel codes with displacements — that rebuild the frames 4 × 4 cells at a time, each cell reusing the
/// palette of the same cell in the frame before. Bounded and read-only; bottom-up frames and optional data are refused.
/// </summary>
public static class Dcc
{
    private static readonly int[] BitWidths = [0, 1, 2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 26, 28, 30, 32];
    private static readonly int[] MaskBits = [0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4];

    public sealed record Animation(int FramesPerDirection, DccDirection[] Directions);

    public static Animation Decode(byte[] data, CancellationToken token = default)
    {
        Require(data.Length >= 15 && data[0] == 0x74, "Not a DCC animation.");
        int directions = data[2], perDirection = BitConverter.ToInt32(data, 3);
        Require(directions is > 0 and <= 32 && perDirection is > 0 and <= 256, "Invalid DCC direction or frame count.");
        Require(data.Length >= 15 + directions * 4, "Truncated DCC header.");
        var result = new DccDirection[directions];
        for (int d = 0; d < directions; d++)
        {
            token.ThrowIfCancellationRequested();
            int offset = BitConverter.ToInt32(data, 15 + d * 4);
            Require(offset > 0 && offset < data.Length, "Invalid DCC direction offset.");
            result[d] = DecodeDirection(data, offset, perDirection);
        }
        return new(perDirection, result);
    }

    /// <summary>Decodes one direction of a DCC whose header has been checked (see <see cref="LegacyAnimation"/>).</summary>
    public static DccDirection DecodeDirection(byte[] data, int direction)
    {
        int offset = BitConverter.ToInt32(data, 15 + direction * 4);
        Require(offset > 0 && offset < data.Length, "Invalid DCC direction offset.");
        return DecodeDirection(data, offset, BitConverter.ToInt32(data, 3));
    }

    private sealed class Bits(byte[] data, long position)
    {
        public long Position = position;
        public Bits Copy() => new(data, Position);
        public uint Get(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                long at = Position >> 3;
                Require(at < data.Length, "Truncated DCC bitstream.");
                value |= (uint)((data[at] >> (int)(Position & 7)) & 1) << i;
                Position++;
            }
            return value;
        }
        public int Signed(int count)
        {
            if (count == 0) return 0;
            uint value = Get(count);
            return count < 32 && (value & (1u << (count - 1))) != 0 ? (int)(value | ~((1u << count) - 1)) : (int)value;
        }
    }

    private sealed class Frame
    {
        public int Width, Height, Left, Top;
        public int CellsX, CellsY;
        public (int X, int Y, int W, int H)[] Cells = [];
    }

    private sealed class Entry { public byte[] Value = new byte[4]; public int Frame = -1, Cell = -1; }

    private static DccDirection DecodeDirection(byte[] data, int offset, int perDirection)
    {
        var bits = new Bits(data, (long)offset * 8);
        bits.Get(32); // coded size
        int compression = (int)bits.Get(2);
        int variable0 = BitWidths[bits.Get(4)], widthBits = BitWidths[bits.Get(4)], heightBits = BitWidths[bits.Get(4)],
            xBits = BitWidths[bits.Get(4)], yBits = BitWidths[bits.Get(4)], optionalBits = BitWidths[bits.Get(4)], codedBits = BitWidths[bits.Get(4)];
        var frames = new Frame[perDirection];
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int f = 0; f < perDirection; f++)
        {
            bits.Get(variable0);
            var frame = frames[f] = new Frame { Width = (int)bits.Get(widthBits), Height = (int)bits.Get(heightBits) };
            int x = bits.Signed(xBits), y = bits.Signed(yBits);
            Require(bits.Get(optionalBits) == 0, "DCC optional frame data is not supported.");
            bits.Get(codedBits);
            Require(bits.Get(1) == 0, "Bottom-up DCC frames are not supported.");
            Require(frame.Width is > 0 and <= 2048 && frame.Height is > 0 and <= 2048, "Invalid DCC frame size.");
            frame.Left = x; frame.Top = y - frame.Height + 1;
            minX = Math.Min(minX, frame.Left); minY = Math.Min(minY, frame.Top);
            maxX = Math.Max(maxX, frame.Left + frame.Width); maxY = Math.Max(maxY, frame.Top + frame.Height);
        }
        int boxWidth = maxX - minX, boxHeight = maxY - minY;
        Require((long)boxWidth * boxHeight <= 4 * 1024 * 1024, "DCC direction exceeds the preview size limit.");
        long equalSize = 0, maskSize, encodingSize = 0, rawSize = 0;
        if ((compression & 2) != 0) equalSize = bits.Get(20);
        maskSize = bits.Get(20);
        if ((compression & 1) != 0) { encodingSize = bits.Get(20); rawSize = bits.Get(20); }
        var paletteKey = new byte[256]; int keys = 0;
        for (int i = 0; i < 256; i++) if (bits.Get(1) != 0) paletteKey[keys++] = (byte)i;
        var equal = bits.Copy(); bits.Position += equalSize;
        var mask = bits.Copy(); bits.Position += maskSize;
        var encoding = bits.Copy(); bits.Position += encodingSize;
        var raw = bits.Copy(); bits.Position += rawSize;
        var codes = bits.Copy();

        // The direction's cell grid: 4 × 4 cells from its top-left corner.
        int cellsX = 1 + (boxWidth - 1) / 4, cellsY = 1 + (boxHeight - 1) / 4;
        // Each frame's own cells: the first row and column are cut to meet the direction's grid.
        foreach (var frame in frames)
        {
            int w = 4 - (frame.Left - minX) % 4, h = 4 - (frame.Top - minY) % 4;
            frame.CellsX = Count(frame.Width, w); frame.CellsY = Count(frame.Height, h);
            var widths = Sizes(frame.CellsX, frame.Width, w); var heights = Sizes(frame.CellsY, frame.Height, h);
            frame.Cells = new (int, int, int, int)[frame.CellsX * frame.CellsY];
            int offsetY = frame.Top - minY;
            for (int y = 0; y < frame.CellsY; y++)
            {
                int offsetX = frame.Left - minX;
                for (int x = 0; x < frame.CellsX; x++) { frame.Cells[x + y * frame.CellsX] = (offsetX, offsetY, widths[x], heights[y]); offsetX += widths[x]; }
                offsetY += heights[y];
            }
        }

        // Pixel buffer: the up-to-four palette entries each frame cell draws with.
        var buffer = new List<Entry>();
        var current = new Entry?[cellsX * cellsY];
        for (int f = 0; f < frames.Length; f++)
        {
            var frame = frames[f];
            int originX = (frame.Left - minX) / 4, originY = (frame.Top - minY) / 4;
            for (int cy = 0; cy < frame.CellsY; cy++)
                for (int cx = 0; cx < frame.CellsX; cx++)
                {
                    int cell = originX + cx + (cy + originY) * cellsX;
                    Require(cell < current.Length, "DCC frame cell is outside its direction.");
                    uint pixelMask = 0x0F; bool decode = true;
                    if (current[cell] != null)
                    {
                        if (equalSize > 0 && equal.Get(1) != 0) decode = false;
                        else pixelMask = mask.Get(4);
                    }
                    if (!decode) continue;
                    var stack = new uint[4]; uint last = 0; int decoded = 0, count = MaskBits[pixelMask];
                    bool rawPixels = count != 0 && encodingSize > 0 && encoding.Get(1) != 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (rawPixels) stack[i] = raw.Get(8);
                        else
                        {
                            stack[i] = last; uint step;
                            do { step = codes.Get(4); stack[i] += step; } while (step == 15);
                        }
                        if (stack[i] == last) { stack[i] = 0; break; }
                        last = stack[i]; decoded++;
                    }
                    var old = current[cell]; var entry = new Entry { Frame = f, Cell = cx + cy * frame.CellsX };
                    int index = decoded - 1;
                    for (int i = 0; i < 4; i++)
                        entry.Value[i] = (pixelMask & (1u << i)) != 0 ? index >= 0 ? (byte)stack[index--] : (byte)0 : old!.Value[i];
                    buffer.Add(entry); current[cell] = entry;
                }
        }
        foreach (var entry in buffer) for (int i = 0; i < 4; i++) entry.Value[i] = paletteKey[entry.Value[i]];

        // Frames: each cell is either copied from where it was last drawn, cleared, or filled from the pixel codes.
        var canvas = new byte[boxWidth * boxHeight];
        var lastCell = new (int W, int H, int X, int Y)[cellsX * cellsY];
        Array.Fill(lastCell, (-1, -1, 0, 0));
        var result = new DccFrame[frames.Length];
        int next = 0;
        for (int f = 0; f < frames.Length; f++)
        {
            var frame = frames[f]; var pixels = new byte[boxWidth * boxHeight];
            for (int c = 0; c < frame.Cells.Length; c++)
            {
                var (x0, y0, w, h) = frame.Cells[c];
                int gridIndex = x0 / 4 + y0 / 4 * cellsX; var previous = lastCell[gridIndex];
                var entry = next < buffer.Count ? buffer[next] : null;
                if (entry == null || entry.Frame != f || entry.Cell != c)
                {
                    if (w != previous.W || h != previous.H) Fill(canvas, boxWidth, x0, y0, w, h, 0);
                    else
                        for (int y = 0; y < h; y++)
                            Buffer.BlockCopy(canvas, previous.X + (previous.Y + y) * boxWidth, canvas, x0 + (y0 + y) * boxWidth, w);
                }
                else
                {
                    if (entry.Value[0] == entry.Value[1]) Fill(canvas, boxWidth, x0, y0, w, h, entry.Value[0]);
                    else
                    {
                        int read = entry.Value[1] == entry.Value[2] ? 1 : 2;
                        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) canvas[x0 + x + (y0 + y) * boxWidth] = entry.Value[codes.Get(read)];
                    }
                    next++;
                }
                for (int y = 0; y < h; y++) Buffer.BlockCopy(canvas, x0 + (y0 + y) * boxWidth, pixels, x0 + (y0 + y) * boxWidth, w);
                lastCell[gridIndex] = (w, h, x0, y0);
            }
            result[f] = new(boxWidth, boxHeight, pixels);
        }
        return new(-minX, -minY, result);

        static int Count(int size, int first)
        {
            if (size - first <= 1) return 1;
            int rest = size - first - 1;
            return 2 + rest / 4 - (rest % 4 == 0 ? 1 : 0);
        }
        static int[] Sizes(int count, int size, int first)
        {
            var sizes = new int[count];
            if (count == 1) { sizes[0] = size; return sizes; }
            sizes[0] = first;
            for (int i = 1; i < count - 1; i++) sizes[i] = 4;
            sizes[^1] = size - first - 4 * (count - 2);
            return sizes;
        }
        static void Fill(byte[] canvas, int stride, int x0, int y0, int w, int h, byte value)
        {
            for (int y = 0; y < h; y++) Array.Fill(canvas, value, x0 + (y0 + y) * stride, w);
        }
    }

    /// <summary>The facing step (of 64, 0 down the screen, turning clockwise) each file direction is drawn for: the game's direction order.</summary>
    public static int Facing(int direction, int directions) => directions switch
    {
        4 => new[] { 8, 24, 40, 56 }[direction],
        _ when direction < 8 => new[] { 8, 24, 40, 56, 0, 16, 32, 48 }[direction],
        _ when direction < 16 => 4 + 8 * (direction - 8),
        _ => 2 + 4 * (direction - 16)
    };

    /// <summary>
    /// The file direction a sprite with <paramref name="directions"/> directions draws for one of the game's 64 facing
    /// steps: the one facing nearest. Step 0 faces down the screen and steps turn clockwise (16 is left, 32 up, 48 right).
    /// </summary>
    public static int Direction(int step64, int directions)
    {
        if (directions is not (4 or 8 or 16 or 32)) return 0;
        step64 = ((step64 % 64) + 64) % 64;
        return Enumerable.Range(0, directions).MinBy(d => { int gap = Math.Abs(Facing(d, directions) - step64); return Math.Min(gap, 64 - gap); });
    }
}
