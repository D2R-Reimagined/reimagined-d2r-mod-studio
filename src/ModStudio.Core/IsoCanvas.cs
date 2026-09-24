namespace ModStudio.Core;

/// <summary>How a sprite's pixels combine with what is under them.</summary>
public enum Blend { Normal, Add, Half, Quarter, ThreeQuarters }

/// <summary>
/// A software canvas for legacy-graphics previews: an isometric floor that can scroll, DCC frames drawn through an act
/// palette (and optionally a palette shift) with the game's blend modes, light radii and ground shadows. RGBA, one game
/// pixel per canvas pixel. Scenes draw on it; it is pure, so tests and captures see exactly what the view shows.
/// </summary>
public class IsoCanvas
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    private readonly byte[] floor;
    private readonly int floorWidth;
    /// <summary>The floor repeats every tile: 160 pixels across, 80 down.</summary>
    protected const int TileX = 160, TileY = 80;

    public IsoCanvas(int width, int height)
    {
        Width = width; Height = height; Pixels = new byte[width * height * 4];
        // 32 × 16 sub-tile diamonds, every fifth line brighter as the 160 × 80 tile edge. One tile wider and taller than
        // the view, so scrolling is a copy from an offset.
        floorWidth = width + TileX; floor = new byte[floorWidth * (height + TileY) * 4];
        for (int y = 0; y < height + TileY; y++)
            for (int x = 0; x < floorWidth; x++)
            {
                double u = x / 32.0 + y / 16.0, v = x / 32.0 - y / 16.0;
                double du = Math.Abs(u - Math.Round(u)), dv = Math.Abs(v - Math.Round(v));
                bool tileU = Math.Abs(u / 5 - Math.Round(u / 5)) * 5 < 0.06, tileV = Math.Abs(v / 5 - Math.Round(v / 5)) * 5 < 0.06;
                int shade = 20 + ((x * 7 + y * 13) % 5);
                if (du < 0.03 || dv < 0.03) shade += 8;
                if (tileU || tileV) shade += 10;
                int i = (y * floorWidth + x) * 4;
                floor[i] = (byte)shade; floor[i + 1] = (byte)(shade - 2); floor[i + 2] = (byte)(shade - 4); floor[i + 3] = 255;
            }
    }

    /// <summary>The screen direction of a 64-step facing: step 0 points down the screen, steps turn clockwise; the floor halves vertical movement.</summary>
    public static (double X, double Y) Heading(int step64)
    {
        double angle = Math.PI / 2 + step64 * Math.PI / 32;
        return (Math.Cos(angle), Math.Sin(angle) * 0.5);
    }

    /// <summary>The facing step that points from the canvas centre toward a point on it.</summary>
    public int FacingToward(double x, double y)
    {
        double dx = x - Width / 2.0, dy = (y - Height / 2.0) * 2;
        if (dx == 0 && dy == 0) return 48;
        double angle = Math.Atan2(dy, dx) - Math.PI / 2;
        return ((int)Math.Round(angle / (Math.PI / 32)) % 64 + 64) % 64;
    }

    /// <summary>Copies the floor into the view, scrolled by an offset.</summary>
    protected void Floor(double offsetX, double offsetY)
    {
        int fx = (int)(((Math.Round(offsetX) % TileX) + TileX) % TileX), fy = (int)(((Math.Round(offsetY) % TileY) + TileY) % TileY);
        for (int y = 0; y < Height; y++) Buffer.BlockCopy(floor, ((y + fy) * floorWidth + fx) * 4, Pixels, y * Width * 4, Width * 4);
    }

    /// <summary>
    /// Draws one frame with its origin at (x, y). Palette indices go through <paramref name="shift"/> first when given
    /// (a palshift table recolouring a monster), then the act palette (B,G,R). Index 0 is transparent.
    /// </summary>
    protected void Draw(DccDirection direction, int frame, double x, double y, byte[] palette, Blend blend, byte[]? shift = null)
    {
        var image = direction.Frames[frame];
        int left = (int)Math.Round(x) - direction.OriginX, top = (int)Math.Round(y) - direction.OriginY;
        for (int sy = 0; sy < image.Height; sy++)
        {
            int py = top + sy; if (py < 0 || py >= Height) continue;
            for (int sx = 0; sx < image.Width; sx++)
            {
                int px = left + sx; if (px < 0 || px >= Width) continue;
                int index = image.Indices[sx + sy * image.Width]; if (index == 0) continue;
                if (shift != null) index = shift[index];
                int r = palette[index * 3 + 2], g = palette[index * 3 + 1], b = palette[index * 3], i = (py * Width + px) * 4;
                switch (blend)
                {
                    case Blend.Add: Pixels[i] = Add(Pixels[i], r); Pixels[i + 1] = Add(Pixels[i + 1], g); Pixels[i + 2] = Add(Pixels[i + 2], b); break;
                    case Blend.Half: Mix(i, r, g, b, 0.5); break;
                    case Blend.Quarter: Mix(i, r, g, b, 0.25); break;
                    case Blend.ThreeQuarters: Mix(i, r, g, b, 0.75); break;
                    default: Pixels[i] = (byte)r; Pixels[i + 1] = (byte)g; Pixels[i + 2] = (byte)b; break;
                }
            }
        }
    }

    private void Mix(int i, int r, int g, int b, double amount)
    {
        Pixels[i] = (byte)(Pixels[i] + (r - Pixels[i]) * amount); Pixels[i + 1] = (byte)(Pixels[i + 1] + (g - Pixels[i + 1]) * amount); Pixels[i + 2] = (byte)(Pixels[i + 2] + (b - Pixels[i + 2]) * amount);
    }

    /// <summary>A light radius: an additive glow <paramref name="radius"/> sub-tiles across the floor.</summary>
    protected void Light(double x, double y, int radius, byte red, byte green, byte blue)
    {
        if (radius <= 0) return;
        double rx = radius * 22, ry = rx / 2;
        int x0 = (int)Math.Max(0, x - rx), x1 = (int)Math.Min(Width - 1, x + rx), y0 = (int)Math.Max(0, y - ry), y1 = (int)Math.Min(Height - 1, y + ry);
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                double d = Math.Pow((px - x) / rx, 2) + Math.Pow((py - y) / ry, 2); if (d >= 1) continue;
                double strength = (1 - d) * (1 - d) * 0.45; int i = (py * Width + px) * 4;
                Pixels[i] = Add(Pixels[i], (int)(red * strength)); Pixels[i + 1] = Add(Pixels[i + 1], (int)(green * strength)); Pixels[i + 2] = Add(Pixels[i + 2], (int)(blue * strength));
            }
    }

    /// <summary>A soft dark ellipse on the floor.</summary>
    protected void Shadow(double x, double y, double rx, double ry)
    {
        int x0 = (int)Math.Max(0, x - rx), x1 = (int)Math.Min(Width - 1, x + rx), y0 = (int)Math.Max(0, y - ry), y1 = (int)Math.Min(Height - 1, y + ry);
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                double d = Math.Pow((px - x) / rx, 2) + Math.Pow((py - y) / ry, 2); if (d >= 1) continue;
                double keep = 0.45 + 0.55 * d; int i = (py * Width + px) * 4;
                Pixels[i] = (byte)(Pixels[i] * keep); Pixels[i + 1] = (byte)(Pixels[i + 1] * keep); Pixels[i + 2] = (byte)(Pixels[i + 2] * keep);
            }
    }

    protected void Disc(double x, double y, int radius, byte r, byte g, byte b)
    {
        for (int py = (int)y - radius; py <= (int)y + radius; py++)
            for (int px = (int)x - radius * 2; px <= (int)x + radius * 2; px++)
            {
                if (px < 0 || py < 0 || px >= Width || py >= Height || Math.Pow((px - x) / (radius * 2.0), 2) + Math.Pow((py - y) / (double)radius, 2) > 1) continue;
                int i = (py * Width + px) * 4; Pixels[i] = r; Pixels[i + 1] = g; Pixels[i + 2] = b;
            }
    }

    protected static byte Add(byte a, int b) => (byte)Math.Min(255, a + b);
}
