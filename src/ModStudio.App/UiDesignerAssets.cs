using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The game's own fonts (Exocet, Formal) loaded from the project or the extracted game data, so the designer sets text the
/// way the game does. Falls back to a serif system face when neither has them.
/// </summary>
internal static class UiFonts
{
    public static readonly FontFamily Fallback = new("Palatino Linotype, Book Antiqua, Georgia, serif");
    private static readonly Dictionary<string, FontFamily?> loaded = new(StringComparer.OrdinalIgnoreCase);
    private static int collections;

    private sealed class GameFontCollection(Uri key) : FontCollectionBase
    {
        public override Uri Key { get; } = key;
    }

    /// <summary>The family for a style's fontFace ("Formal"; anything else is Exocet, the game's default).</summary>
    public static FontFamily For(UiAssets? assets, string? face)
    {
        if (assets?.FontFile(face ?? "Exocet") is not { } path) return Fallback;
        if (!loaded.TryGetValue(path, out var family)) loaded[path] = family = Load(path);
        return family ?? Fallback;
    }

    /// <summary>Whether text is being set in the game's fonts (false when they were not found).</summary>
    public static bool HasGameFont(UiAssets? assets) => !ReferenceEquals(For(assets, "Exocet"), Fallback);

    private static FontFamily? Load(string path)
    {
        try
        {
            var collection = new GameFontCollection(new Uri($"fonts:d2rgame{++collections}"));
            FontManager.Current.AddFontCollection(collection);
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            if (!collection.TryAddGlyphTypeface(stream, out var glyph) || glyph == null) return null;
            return new FontFamily($"{collection.Key}#{glyph.FamilyName}");
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Decoded UI sprite frames as bitmaps, shared by every designer. Frames decode on a worker (a panel background is 7 MB of
/// pixels) and the canvas is told to repaint when one arrives; the cache keeps the most recently drawn frames within a budget.
/// </summary>
internal static class UiBitmaps
{
    private const long Budget = 768L * 1024 * 1024;
    /// <summary>Opacity is kept per 4 × 4 pixel block: enough to pick what is drawn under the pointer, at a sixteenth of the memory.</summary>
    private const int MaskBlock = 4;
    private sealed record Mask(int Width, int Height, int Columns, bool[] Opaque);
    private static readonly Dictionary<string, (Bitmap Bitmap, long Bytes, LinkedListNode<string> Node, Mask Mask)> cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> order = new();
    private static readonly Dictionary<string, Task> pending = new(StringComparer.Ordinal);
    private static readonly HashSet<string> failed = new(StringComparer.Ordinal);
    private static long bytes;

    /// <summary>Decodes still running (tests await them before looking at the canvas).</summary>
    public static Task Pending => Task.WhenAll(pending.Values.ToArray());

    public static bool Failed(UiSpriteInfo sprite, int frame) => failed.Contains(Key(sprite, frame));

    private static string Key(UiSpriteInfo sprite, int frame) => sprite.CacheKey + "#" + Math.Clamp(frame, 0, sprite.Frames - 1);

    /// <summary>The frame's bitmap, or null while it decodes (<paramref name="ready"/> runs on the UI thread once it has).</summary>
    public static Bitmap? Get(UiSpriteInfo sprite, int frame, Action ready)
    {
        var key = Key(sprite, frame);
        if (cache.TryGetValue(key, out var hit)) { order.Remove(hit.Node); order.AddFirst(hit.Node); return hit.Bitmap; }
        if (failed.Contains(key) || pending.ContainsKey(key)) return null;
        pending[key] = Task.Run(() => { var pixels = UiAssets.Decode(sprite, frame); return (Pixels: pixels, Mask: MaskOf(pixels)); }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            pending.Remove(key);
            if (!t.IsCompletedSuccessfully) { failed.Add(key); ready(); return; }
            var (pixels, mask) = t.Result;
            var bitmap = Bitmaps.From(pixels.Width, pixels.Height, pixels.Rgba);
            long size = (long)pixels.Width * pixels.Height * 4 + mask.Opaque.Length;
            cache[key] = (bitmap, size, order.AddFirst(key), mask); bytes += size;
            while (bytes > Budget && order.Last is { } last && last.Value != key)
            {
                var old = cache[last.Value]; cache.Remove(last.Value); order.RemoveLast(); bytes -= old.Bytes;
                old.Bitmap.Dispose();
            }
            ready();
        }), TaskScheduler.Default);
        return null;
    }

    private static Mask MaskOf(PreviewPixels pixels)
    {
        int columns = (pixels.Width + MaskBlock - 1) / MaskBlock, rows = (pixels.Height + MaskBlock - 1) / MaskBlock;
        var opaque = new bool[columns * rows];
        for (int y = 0; y < pixels.Height; y++)
        {
            int row = y / MaskBlock * columns, offset = y * pixels.Width * 4 + 3;
            for (int x = 0; x < pixels.Width; x++) if (pixels.Rgba[offset + x * 4] > 24) opaque[row + x / MaskBlock] = true;
        }
        return new(pixels.Width, pixels.Height, columns, opaque);
    }

    /// <summary>
    /// Whether a frame is drawn at a point given as a fraction of its width and height; null while it has not been decoded
    /// (callers then treat the whole frame as drawn).
    /// </summary>
    public static bool? Opaque(UiSpriteInfo sprite, int frame, double u, double v)
    {
        if (!cache.TryGetValue(Key(sprite, frame), out var hit)) return null;
        var m = hit.Mask;
        int x = Math.Clamp((int)(u * m.Width), 0, m.Width - 1) / MaskBlock, y = Math.Clamp((int)(v * m.Height), 0, m.Height - 1) / MaskBlock;
        return m.Opaque[y * m.Columns + x];
    }
}
