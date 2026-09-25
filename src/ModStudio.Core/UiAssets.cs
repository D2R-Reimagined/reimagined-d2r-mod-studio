using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// A UI sprite as the game would load it for a layout's filename. HD UI sprites are drawn 1:1 with layout pixels; a
/// .lowend.sprite (half resolution) stands in when only that one exists and is drawn at twice its size.
/// </summary>
public sealed record UiSpriteInfo(string Filename, string Path, string Relative, bool InProject, bool LowEnd, int FrameWidth, int Height, int Frames, int Stride, long Length, DateTime Modified)
{
    public double Factor => LowEnd ? 2 : 1;
    public double Width => FrameWidth * Factor;
    public double DrawHeight => Height * Factor;
    public string Origin => InProject ? "project" : "game data";
    /// <summary>Identifies the decoded pixels: a changed file on disk is a different key.</summary>
    public string CacheKey => $"{Path}|{Length}|{Modified.Ticks}";
}

/// <summary>
/// The files a UI layout draws with: sprites (data/hd/global/ui/…), the game's fonts (data/hd/ui/fonts) and localized
/// strings. Each is looked for in the layout's own data folder, the project, then the extracted game data, the order the
/// game applies a mod over the base game. Nothing ships with Studio.
/// </summary>
public sealed class UiAssets
{
    private readonly List<string> roots = [];
    private readonly HashSet<string> projectRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UiSpriteInfo?> sprites = new(StringComparer.OrdinalIgnoreCase);
    public ModProject? Project { get; }
    public IReadOnlyList<string> GameData { get; }
    public bool HasGameData => GameData.Count > 0;

    public UiAssets(ModProject? project, IReadOnlyList<string> gameData, string? documentPath = null)
    {
        Project = project; GameData = gameData;
        // <data>/global/ui/layouts → <data>
        if (documentPath != null && UiLayoutSources.LayoutsRoot(documentPath) is { } layouts && Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(layouts))) is { } data)
        {
            roots.Add(data);
            if (project != null && Contains(project.Root, data)) projectRoots.Add(data);
        }
        if (project != null) { roots.Add(project.Root); projectRoots.Add(project.Root); }
        roots.AddRange(gameData);
    }

    /// <summary>The data-relative sprite path a layout filename names ("PANEL\\Inventory\\Background" → data/hd/global/ui/panel/inventory/background).</summary>
    public static string SpritePath(string filename) => "data/hd/global/ui/" + filename.Replace('\\', '/').Replace("//", "/").Trim('/').ToLowerInvariant();

    /// <summary>The sprite a filename names, from its header only; null when neither the project nor the game data has it.</summary>
    public UiSpriteInfo? Sprite(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.StartsWith('$')) return null;
        return sprites.GetOrAdd(filename, name =>
        {
            var relative = SpritePath(name);
            foreach (var suffix in new[] { ".sprite", ".lowend.sprite" })
                foreach (var root in roots)
                    if (HdAppearance.Locate(root, relative + suffix) is { } path && ReadHeader(name, path, relative + suffix, projectRoots.Contains(root), suffix != ".sprite") is { } info)
                        return info;
            return null;
        });
    }

    /// <summary>Forgets resolved sprite paths (files were added or the game data folder changed).</summary>
    public void Forget() => sprites.Clear();

    private static UiSpriteInfo? ReadHeader(string filename, string path, string relative, bool inProject, bool lowEnd)
    {
        try
        {
            var info = new FileInfo(path);
            using var stream = info.OpenRead();
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) < 24 || BinaryPrimitives.ReadInt32LittleEndian(header) != 0x31417053) return null;
            int frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]), width = BinaryPrimitives.ReadInt32LittleEndian(header[8..]), height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]), frames = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
            if (frames <= 0 || width <= 0 || height <= 0 || frameWidth <= 0) return null;
            if (frameWidth > width / frames) return null;
            return new(filename, path, relative, inProject, lowEnd, frameWidth, height, frames, width / frames, info.Length, info.LastWriteTimeUtc);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Decodes one frame (0-based; clamped to the frames the sprite has).</summary>
    public static PreviewPixels Decode(UiSpriteInfo sprite, int frame)
    {
        var asset = SpecialistPreview.Load(sprite.Path);
        return asset.Decode(Math.Clamp(frame, 0, sprite.Frames - 1) + 1);
    }

    // ── Fonts ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The game's font files by the face names styles use: Exocet for most text, Formal for speech and descriptions.</summary>
    public static readonly IReadOnlyDictionary<string, string> FontFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Exocet"] = "exocetblizzardot-medium.otf", ["Formal"] = "formal436bt.ttf", ["Philosopher"] = "philosopher-bolditalic.ttf"
    };

    /// <summary>Where a game font file is: the project's copy first, then the game data's.</summary>
    public string? FontFile(string face)
    {
        var file = FontFiles.TryGetValue(face, out var known) ? known : FontFiles["Exocet"];
        foreach (var root in roots) if (HdAppearance.Locate(root, "data/hd/ui/fonts/" + file) is { } path) return path;
        return null;
    }

    // ── Strings ───────────────────────────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> stringFiles = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IReadOnlyDictionary<string, string>>? strings;
    private string stringsLocale = "";
    private static readonly Regex ColorCode = new("ÿc.", RegexOptions.Compiled);

    /// <summary>
    /// The text of an "@key" string: the project's string catalogs (source/strings), then the game data's
    /// local/lng/strings files. Null when no catalog has the key.
    /// </summary>
    public string? Localize(string key, string locale = "enUS")
    {
        if (strings == null || stringsLocale != locale) { strings = LoadStrings(locale); stringsLocale = locale; }
        foreach (var catalog in strings) if (catalog.TryGetValue(key, out var text)) return text;
        return null;
    }

    private List<IReadOnlyDictionary<string, string>> LoadStrings(string locale)
    {
        var result = new List<IReadOnlyDictionary<string, string>>();
        if (Project != null && Directory.Exists(Path.Combine(Project.Root, "source", "strings")))
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Project.Root, "source", "strings"), "*.json").Order(StringComparer.Ordinal))
                if (Strings(file, locale, catalog: true) is { } map) result.Add(map);
        foreach (var folder in GameData)
            if (HdAppearance.LocateFolder(folder, "data/local/lng/strings") is { } stringsFolder)
                foreach (var file in Directory.EnumerateFiles(stringsFolder, "*.json").Order(StringComparer.Ordinal))
                    if (Strings(file, locale, catalog: false) is { } map) result.Add(map);
        return result;
    }

    /// <summary>One strings file (a Studio catalog or the game's array of {Key, enUS…}) for a locale, cached by file version.</summary>
    private static IReadOnlyDictionary<string, string>? Strings(string file, string locale, bool catalog)
    {
        try
        {
            var info = new FileInfo(file);
            var key = $"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{locale}";
            return stringFiles.GetOrAdd(key, _ =>
            {
                // Layouts write @strClose where catalogs may spell strclose; the game matches either.
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var root = JsonNode.Parse(File.ReadAllText(file, Utf8).TrimStart('﻿'), null, Document.SourceJsonOptions);
                var rows = catalog ? root?["records"] as JsonArray : root as JsonArray;
                foreach (var row in rows?.OfType<JsonObject>() ?? [])
                {
                    var name = (row["Key"] as JsonValue)?.TryGetValue<string>(out var k) == true ? k : null;
                    if (name == null) continue;
                    var value = catalog ? row["translations"]?[locale] : row[locale];
                    if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0) map.TryAdd(name, ColorCode.Replace(text, ""));
                }
                if (stringFiles.Count > 64) stringFiles.Clear();
                return map;
            });
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }
}
