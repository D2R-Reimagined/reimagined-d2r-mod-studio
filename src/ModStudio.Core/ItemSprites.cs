using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// The HD inventory picture of a unique or set item: the sprite file it resolved to, the asset name that led there, and
/// the decoded first frame. <see cref="Pixels"/> is null when no sprite was found; <see cref="Notes"/> says why.
/// </summary>
public sealed record ItemSprite(HdFile? File, string? Asset, string Via, PreviewPixels? Pixels, string[] Notes);

/// <summary>
/// Finds the inventory sprite D2R draws for a unique or set item. data/hd/items/uniques.json (sets.json) names an asset
/// per item, keyed by the item's index with punctuation dropped and spaces turned to underscores, one entry per base tier;
/// items it does not list fall back to their base item's asset in items.json. The asset is drawn from
/// data/hd/global/ui/items/&lt;weapon|armor|misc&gt;/&lt;asset&gt;.sprite. Every file is looked for in the project first,
/// then in the user's extracted game data, so a mod that ships none of them still shows the base game's pictures.
/// </summary>
public static class ItemSprites
{
    private static readonly ConcurrentDictionary<string, Dictionary<string, JsonObject>> maps = new(StringComparer.Ordinal);
    private static readonly string[] Categories = ["weapon", "armor", "misc"];

    /// <summary>The key an item's index is looked up by: letters and digits only, lower case. "Bul-Kathos' Sacred Charge" and bul_kathos_sacred_charge meet at bulkathossacredcharge.</summary>
    public static string Key(string index) => new([.. index.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);

    /// <summary>The asset tier a base code is in: normal, uber (exceptional) or ultra (elite), from the base row's tier codes.</summary>
    public static string Tier(JsonObject? baseItem, string code) =>
        baseItem == null ? "normal" : code.Equals(baseItem.S("ultracode"), StringComparison.OrdinalIgnoreCase) && code.Length > 0 ? "ultra"
        : code.Equals(baseItem.S("ubercode"), StringComparison.OrdinalIgnoreCase) && code.Length > 0 ? "uber" : "normal";

    /// <summary>
    /// Resolves and decodes the sprite. <paramref name="baseTable"/> (weapons, armor or misc) picks the folder tried first.
    /// Reads only; a missing mapping or sprite is reported in the notes, never thrown.
    /// </summary>
    public static ItemSprite Resolve(ModProject project, IReadOnlyList<string> gameData, string table, string index, string code, string? baseTable, string tier = "normal", CancellationToken token = default)
    {
        var notes = new List<string>();
        string? asset = null, via = "";
        var named = Map(project, gameData, table == "setitems" ? "data/hd/items/sets.json" : "data/hd/items/uniques.json", notes);
        if (named != null && index.Length > 0 && named.TryGetValue(Key(index), out var entry))
        {
            asset = Asset(entry, tier); via = (table == "setitems" ? "sets.json" : "uniques.json") + " · " + index;
        }
        token.ThrowIfCancellationRequested();
        if (asset == null && code.Length > 0 && Map(project, gameData, "data/hd/items/items.json", notes) is { } bases && bases.TryGetValue(code.ToLowerInvariant(), out var baseEntry))
        {
            asset = Asset(baseEntry, tier); via = "items.json · base " + code;
        }
        if (asset == null)
        {
            notes.Add(gameData.Count == 0 && named == null
                ? "No HD item list in the project. Choose your extracted game data folder to show base-game item pictures."
                : $"No HD picture is listed for {(index.Length > 0 ? index : code)}.");
            return new(null, null, via, null, [.. notes]);
        }
        var preferred = baseTable switch { "weapons" => "weapon", "armor" => "armor", "misc" => "misc", _ => null };
        // The low-end (reduced resolution) sprite stands in when an extraction or mod has only that one.
        foreach (var (category, suffix) in Categories.OrderBy(c => c == preferred ? 0 : 1).SelectMany(c => new[] { (c, ".sprite"), (c, ".lowend.sprite") }).OrderBy(p => p.Item2.Length))
        {
            token.ThrowIfCancellationRequested();
            if (HdAppearance.Find(project, gameData, $"data/hd/global/ui/items/{category}/{asset}{suffix}") is not { } file) continue;
            try
            {
                var decoded = SpecialistPreview.Load(file.Path);
                return new(file, asset, via, decoded.Decode(decoded.Frames.Length > 1 ? 1 : 0), [.. notes]);
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or NotSupportedException or IOException)
            {
                notes.Add($"{file.Relative}: {e.Message}");
                return new(file, asset, via, null, [.. notes]);
            }
        }
        notes.Add($"Sprite for {asset} is not under data/hd/global/ui/items in the project{(gameData.Count == 0 ? "; choose your extracted game data folder to read the base game's copy" : " or the game data")}.");
        return new(null, asset, via, null, [.. notes]);
    }

    /// <summary>An entry's asset: the tier's name in uniques/sets.json, or "asset" in items.json.</summary>
    private static string? Asset(JsonObject entry, string tier)
    {
        foreach (var key in new[] { tier, "normal", "asset" })
            if (entry[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) return text.Replace('\\', '/').Trim('/');
        return null;
    }

    /// <summary>An HD item list (an array of one-key objects) by key; cached per file content. Null when the file is not found or unreadable.</summary>
    private static Dictionary<string, JsonObject>? Map(ModProject project, IReadOnlyList<string> gameData, string relative, List<string> notes)
    {
        HdFile? file;
        try { file = HdAppearance.Find(project, gameData, relative); }
        catch (IOException e) { notes.Add($"{relative}: {e.Message}"); return null; }
        if (file == null) return null;
        var cacheKey = relative + "|" + file.Hash;
        if (maps.TryGetValue(cacheKey, out var cached)) return cached;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file.Path, Utf8).TrimStart('﻿'), null, Document.SourceJsonOptions);
            var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            IEnumerable<KeyValuePair<string, JsonNode?>> pairs = root switch
            {
                JsonArray array => array.OfType<JsonObject>().SelectMany(o => o),
                JsonObject obj => obj,
                _ => []
            };
            // Base codes are matched as written; item names by their squashed key.
            bool items = relative.EndsWith("/items.json", StringComparison.OrdinalIgnoreCase);
            foreach (var pair in pairs)
                if (pair.Value is JsonObject value) map.TryAdd(items ? pair.Key.ToLowerInvariant() : Key(pair.Key), value);
            if (maps.Count > 16) maps.Clear();
            return maps[cacheKey] = map;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            notes.Add($"{file.Relative} ({file.Origin}) could not be read: {e.Message}");
            return null;
        }
    }
}
