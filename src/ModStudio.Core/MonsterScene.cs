namespace ModStudio.Core;

/// <summary>What one rendered frame of a monster shows: the animation frame and direction, the COF event on it (1 = attack lands), and how far the floor has moved.</summary>
public sealed record MonsterSceneState(int Frame, int Frames, int Direction, int Event, double Distance);

/// <summary>
/// Draws a monster as the legacy renderer does: its composite layers stacked in the order the COF gives for the direction
/// and frame, recoloured by its palette shift and blended per layer, over its shadow and light radius. In a moving mode the
/// floor slides under it at its velocity, so walk and run speeds can be compared at game scale.
/// </summary>
public sealed class MonsterScene(int width, int height) : IsoCanvas(width, height)
{
    /// <summary>Screen pixels per game frame for one unit of monstats Velocity/Run. Approximate: a player walks at 6 and runs at 9.</summary>
    public const double PixelsPerVelocity = 0.85;
    /// <summary>Where the monster stands: centred, a little below the middle so tall monsters fit.</summary>
    public (double X, double Y) Anchor => (Width / 2.0, Height * 0.68);

    /// <summary>The animation frame shown after <paramref name="tick"/> game frames: speed is in 256ths of a frame per game frame.</summary>
    public static int FrameAt(int tick, Cof cof, bool once)
    {
        int frame = (int)((long)tick * cof.Speed / 256);
        return once ? Math.Min(frame, cof.FramesPerDirection - 1) : frame % cof.FramesPerDirection;
    }

    public MonsterSceneState Render(int tick, int step64, MonsterComposite composite, MonsterLook look, byte[]? palette, double velocity, bool once)
    {
        var (hx, hy) = Heading(step64);
        double distance = velocity * PixelsPerVelocity * tick;
        Floor(hx * distance, hy * distance);
        var (x, y) = Anchor;
        if (look.Shadow) Shadow(x, y, 34, 13);
        if (look.Light > 0) Light(x, y, look.Light, look.Red, look.Green, look.Blue);
        if (composite.Cof is not { } cof || palette == null) { Disc(x, y, 5, 150, 130, 90); return new(-1, 0, 0, 0, distance); }
        int direction = Dcc.Direction(step64, cof.Directions), frame = FrameAt(tick, cof, once);
        var byPart = composite.Layers.Where(l => l.Animation != null).GroupBy(l => l.Layer.Component).ToDictionary(g => g.Key, g => g.First());
        foreach (var part in cof.Order[Math.Min(direction, cof.Order.Length - 1)][frame])
        {
            if (!byPart.TryGetValue(part, out var layer)) continue;
            var file = layer.Animation!;
            int fileDirection = file.DirectionCount == cof.Directions ? direction : Dcc.Direction(step64, file.DirectionCount);
            Draw(file.Direction(fileDirection), Math.Min(frame, file.FramesPerDirection - 1), x, y, palette, layer.Layer.Blend, composite.Shift);
        }
        return new(frame, cof.FramesPerDirection, direction, cof.Events.Length > frame ? cof.Events[frame] : 0, distance);
    }
}
