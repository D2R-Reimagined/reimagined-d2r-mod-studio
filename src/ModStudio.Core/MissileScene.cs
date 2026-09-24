using System.Globalization;

namespace ModStudio.Core;

/// <summary>
/// How a missile row moves and animates, read from its cells: missiles.txt measures velocity in pixels per frame, range
/// and delays in frames (25 a second), and animation speed in sixteenths of a frame per game frame.
/// </summary>
public sealed record MissileMotion(double Vel, double MaxVel, double Accel, int Range, int InitSteps, double FramesPerTick, bool Loop, int RandStart,
    bool SubLoop, int SubStart, int SubStop, int Trans, int Light, int Flicker, byte Red, byte Green, byte Blue, int XOffset, int YOffset, int ZOffset)
{
    public static MissileMotion Read(Func<string, string> cell)
    {
        double Number(string column, double fallback = 0) => double.TryParse(cell(column), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        int Int(string column, int fallback = 0) => (int)Math.Round(Number(column, fallback));
        byte Channel(string column) => (byte)Math.Clamp(Int(column, 255), 0, 255);
        return new(Number("Vel"), Number("MaxVel"), Number("Accel"), Int("Range"), Int("InitSteps"), Number("AnimSpeed", 16) / 16, cell("LoopAnim") == "1", Int("RandStart"),
            cell("SubLoop") == "1", Int("SubStart"), Int("SubStop"), Int("Trans"), Int("Light"), Int("Flicker"), Channel("Red"), Channel("Green"), Channel("Blue"),
            Int("xoffset"), Int("yoffset"), Int("zoffset"));
    }

    /// <summary>Distance travelled after <paramref name="ticks"/> frames: velocity changes by Accel each frame, capped at MaxVel when accelerating, never below zero.</summary>
    public double Distance(int ticks)
    {
        double distance = 0, velocity = Vel;
        for (int i = 0; i < ticks; i++)
        {
            distance += velocity;
            velocity += Accel;
            if (Accel > 0 && MaxVel > 0) velocity = Math.Min(velocity, MaxVel);
            velocity = Math.Max(0, velocity);
        }
        return distance;
    }

    /// <summary>The animation frame shown after <paramref name="ticks"/> frames alive, or -1 when nothing is drawn (before InitSteps, or after a non-looping animation ends).</summary>
    public int Frame(int ticks, int frames)
    {
        if (frames <= 0 || ticks < InitSteps) return -1;
        // RandStart makes the game start somewhere up to that frame at random; the preview always starts at 0.
        int frame = (int)Math.Floor((ticks - InitSteps) * Math.Max(0, FramesPerTick));
        if (SubLoop && SubStop > SubStart && SubStart >= 0 && SubStop < frames && frame > SubStop)
            return SubStart + (frame - SubStart) % (SubStop - SubStart + 1);
        if (Loop) return frame % frames;
        return frame < frames ? frame : -1;
    }

    /// <summary>How long a one-shot animation plays, in frames; 0 when the speed is 0.</summary>
    public int PlayLength(int frames) => FramesPerTick <= 0 ? 0 : (int)Math.Ceiling(frames / FramesPerTick);
}

/// <summary>A decoded animation ready to draw: its frames, the act palette (B,G,R) and how it blends and plays.</summary>
public sealed record SceneSprite(Dcc.Animation Animation, byte[] Palette, MissileMotion Motion);

/// <summary>
/// Draws a missile's flight as the legacy renderer would, in software so it can be tested and captured: an isometric
/// floor, the caster, the missile moving at its authored velocity with its light radius, blended per its Trans mode
/// (0 normal, 1 additive light, 2 half transparent), then its explosion missile where it ends. Scale is one game pixel
/// to one screen pixel.
/// </summary>
public sealed class MissileScene(int width, int height) : IsoCanvas(width, height)
{
    /// <summary>Frames of stillness after the explosion before the flight starts again.</summary>
    public const int Pause = 15;

    /// <summary>Frames in one loop of the scene: the flight, the explosion, and a pause.</summary>
    public static int Period(MissileMotion motion, SceneSprite? explosion) =>
        Math.Max(1, motion.Range) + (explosion == null ? 0 : explosion.Motion.PlayLength(explosion.Animation.FramesPerDirection)) + Pause;

    /// <summary>
    /// Where the caster stands and how far the missile can go before the camera follows it. A flight that fits is centred
    /// and the camera stays still; a longer one starts near the edge and the view scrolls with the missile once it nears
    /// the far edge, so its end and explosion stay in sight at full scale.
    /// </summary>
    public (double X, double Y, double Travel) Origin(MissileMotion motion, int step64)
    {
        var (hx, hy) = Heading(step64);
        double length = motion.Distance(Math.Max(1, motion.Range));
        double reach = Math.Min(Math.Abs(hx) < 1e-6 ? double.MaxValue : (Width / 2.0 - 40) / Math.Abs(hx), Math.Abs(hy) < 1e-6 ? double.MaxValue : (Height / 2.0 - 30) / Math.Abs(hy));
        double back = Math.Min(length / 2, reach);
        // A followed flight stops short of the far edge, leaving room for the explosion it ends in.
        return (Width / 2.0 - hx * back, Height / 2.0 - hy * back, length / 2 <= reach ? double.MaxValue : 1.6 * reach);
    }

    /// <summary>Draws frame <paramref name="tick"/> of the loop into <see cref="Pixels"/> (RGBA). Returns what is on screen, for captions and tests.</summary>
    public SceneState Render(int tick, int step64, MissileMotion motion, SceneSprite? missile, SceneSprite? explosion)
    {
        int range = Math.Max(1, motion.Range), period = Period(motion, explosion);
        tick = ((tick % period) + period) % period;
        var (hx, hy) = Heading(step64); var (ox, oy, travel) = Origin(motion, step64);
        double shown = motion.Distance(Math.Min(tick, range)), camera = Math.Max(0, shown - travel);
        Floor(hx * camera, hy * camera);
        Disc(ox - hx * camera, oy - hy * camera, 5, 150, 130, 90);
        if (tick < range)
        {
            double distance = shown;
            double x = ox + hx * (distance - camera), y = oy + hy * (distance - camera);
            if (tick >= motion.InitSteps) Light(x, y, motion, tick);
            int frame = -1, direction = 0;
            if (missile != null)
            {
                direction = Dcc.Direction(step64, missile.Animation.Directions.Length);
                frame = motion.Frame(tick, missile.Animation.FramesPerDirection);
                if (frame >= 0) Draw(missile, direction, frame, x + motion.XOffset, y + motion.YOffset - motion.ZOffset, motion.Trans);
            }
            return new(SceneStage.Flight, tick, frame, direction, distance, x, y);
        }
        double end = shown;
        double ex = ox + hx * (end - camera), ey = oy + hy * (end - camera);
        if (explosion != null && tick - range < explosion.Motion.PlayLength(explosion.Animation.FramesPerDirection))
        {
            int t = tick - range, direction = Dcc.Direction(step64, explosion.Animation.Directions.Length);
            int frame = Math.Min(explosion.Animation.FramesPerDirection - 1, (int)Math.Floor(t * explosion.Motion.FramesPerTick));
            Light(ex, ey, explosion.Motion, t);
            Draw(explosion, direction, frame, ex + explosion.Motion.XOffset, ey + explosion.Motion.YOffset - explosion.Motion.ZOffset, explosion.Motion.Trans);
            return new(SceneStage.Explosion, t, frame, direction, end, ex, ey);
        }
        return new(SceneStage.Pause, tick - range, -1, 0, end, ex, ey);
    }

    private void Draw(SceneSprite sprite, int direction, int frame, double x, double y, int trans) =>
        Draw(sprite.Animation.Directions[direction], frame, x, y, sprite.Palette, trans switch { 1 => Blend.Add, 2 => Blend.Half, _ => Blend.Normal });

    /// <summary>The light radius, flickering every fourth frame by up to Flicker sub-tiles.</summary>
    private void Light(double x, double y, MissileMotion motion, int tick)
    {
        int radius = motion.Light;
        if (radius > 0 && motion.Flicker > 0) radius += (int)((uint)(tick / 4 * 2654435761u) % (uint)(motion.Flicker + 1));
        Light(x, y, radius, motion.Red, motion.Green, motion.Blue);
    }
}

public enum SceneStage { Flight, Explosion, Pause }

/// <summary>What one rendered frame shows: the stage, the frame within it, the sprite frame and direction drawn, and the missile's position.</summary>
public sealed record SceneState(SceneStage Stage, int Tick, int Frame, int Direction, double Distance, double X, double Y);
