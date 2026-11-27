using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>One file a profile's tableOverrides names, with the rules it holds (one, or a rule set's list).</summary>
public sealed record ProfileRuleFile(string Reference, string Path, JsonNode Root, JsonNode[] Rules)
{
    /// <summary>A rule set a script writes ({"generator": ..., "rules": [...]}); Studio leaves it to its generator.</summary>
    public bool Generated => ProfileRules.IsGenerated(Root);
}

/// <summary>
/// One profile's own folder, compatibility/&lt;id&gt;/: its profile.json and the rule files it names. A profile with
/// "basedOn" is the top layer of a chain whose lower layers are its parents.
/// </summary>
public sealed record ProfileLayer(string Id, string Folder, string SettingsFile, JsonObject Settings, bool Exists, IReadOnlyList<ProfileRuleFile> RuleFiles)
{
    public JsonNode[] Rules => [.. RuleFiles.SelectMany(f => f.Rules)];
    public string? BasedOn => Settings["basedOn"] is JsonNode parent ? parent.GetValue<string>() : null;
}

/// <summary>
/// A profile and the profiles it is based on, root first. A child applies its rules to what its parent builds (so a change's
/// "expect" is the parent's value) and inherits every setting it does not set itself; asset overrides add up, a child's
/// replacing its parent's for the same target.
/// </summary>
public sealed class ProfileChain
{
    public const string LoaderRuntime = "loader", PlainRuntime = "plain";
    public const int MaxDepth = 8;
    private static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$");

    public string Id => Own.Id;
    /// <summary>Root first; the profile itself is last.</summary>
    public IReadOnlyList<ProfileLayer> Layers { get; }
    public ProfileLayer Own => Layers[^1];
    /// <summary>SHA-256 of every file read, by full path; empty unless loaded with fingerprints.</summary>
    public IReadOnlyDictionary<string, string> Fingerprints { get; }
    public string StringMode { get; }
    /// <summary>"loader" (D2RLoader: d2rloader/ assets may ship) or "plain" (the game alone).</summary>
    public string Runtime { get; }
    /// <summary>modInfo fields merged root to leaf; null when no layer sets any.</summary>
    public JsonObject? ModInfo { get; }
    public string? Color => Own.Settings["color"]?.GetValue<string>();
    /// <summary>Each layer's rules, root first: what <see cref="BuildService.ResolveTable(TableData, string, IReadOnlyList{JsonNode[]})"/> applies in turn.</summary>
    public IReadOnlyList<JsonNode[]> RuleLayers { get; }
    public IEnumerable<JsonNode> AllRules => RuleLayers.SelectMany(r => r);

    private ProfileChain(IReadOnlyList<ProfileLayer> layers, IReadOnlyDictionary<string, string> fingerprints)
    {
        Layers = layers; Fingerprints = fingerprints;
        StringMode = Nearest("stringMode") ?? (layers[0].Id == "standard" ? "standard" : "full");
        Runtime = Nearest("runtime") ?? (layers[0].Id == "d2rl" ? LoaderRuntime : PlainRuntime);
        JsonObject? info = null;
        foreach (var layer in layers)
            if (layer.Settings["modInfo"] is JsonObject fields) { info ??= []; foreach (var pair in fields) info[pair.Key] = pair.Value?.DeepClone(); }
        ModInfo = info;
        RuleLayers = [.. layers.Select(l => l.Rules)];
    }
    private string? Nearest(string key)
    {
        for (int i = Layers.Count - 1; i >= 0; i--) if (Layers[i].Settings[key] is JsonNode value) return value.GetValue<string>();
        return null;
    }

    /// <summary>The same chain with its own layer replaced (an edit being validated before it is written).</summary>
    public ProfileChain WithOwn(ProfileLayer own) => new([.. Layers.Take(Layers.Count - 1), own], Fingerprints);

    public static string SettingsPath(string root, string profile) => Inside(root, $"compatibility/{profile}/profile.json");
    public static JsonObject DefaultSettings(string profile) => new()
    {
        ["schemaVersion"] = 1, ["id"] = profile, ["stringMode"] = profile == "standard" ? "standard" : "full",
        ["runtime"] = profile == "d2rl" ? LoaderRuntime : PlainRuntime, ["tableOverrides"] = new JsonArray(), ["assetOverrides"] = new JsonArray(),
    };

    /// <summary>Loads a profile of the project (or build snapshot) at <paramref name="root"/> with every parent it names.</summary>
    /// <param name="read">Parses a file; lets callers with a parse cache reuse it. Ignored when <paramref name="fingerprints"/> is set.</param>
    public static ProfileChain Load(string root, string profile, Func<string, JsonNode>? read = null, bool fingerprints = false)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonNode Parse(string file)
        {
            if (!fingerprints) return read?.Invoke(file) ?? Read(file);
            var bytes = File.ReadAllBytes(file); hashes[file] = Hash(bytes);
            return JsonNode.Parse(Utf8.GetString(bytes).TrimStart('﻿')) ?? throw new InvalidDataException("Empty JSON: " + file);
        }
        var layers = new List<ProfileLayer>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (string? id = profile; id != null;)
        {
            Require(seen.Add(id), $"Profile {profile} is based on itself through {id}.");
            Require(layers.Count < MaxDepth, $"Profile {profile} has more than {MaxDepth} levels of basedOn.");
            var layer = LoadLayer(root, id, Parse, layers.Count == 0 ? null : layers[^1].Id);
            layers.Add(layer); id = layer.BasedOn;
        }
        layers.Reverse();
        return new(layers, hashes);
    }

    private static ProfileLayer LoadLayer(string root, string id, Func<string, JsonNode> parse, string? child)
    {
        var folder = Inside(root, "compatibility/" + id); var file = Path.Combine(folder, "profile.json");
        if (child != null) Require(Directory.Exists(folder), $"Profile {child} is based on {id}, which does not exist.");
        bool exists = File.Exists(file);
        var settings = exists ? parse(file) as JsonObject ?? throw new InvalidDataException($"{id}/profile.json must be an object.") : DefaultSettings(id);
        Validate(settings, id);
        var files = new List<ProfileRuleFile>(); var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in (JsonArray?)settings["tableOverrides"] ?? [])
        {
            Require(node is JsonValue v && v.TryGetValue<string>(out _), $"{id}: tableOverrides lists file paths.");
            var reference = node!.GetValue<string>(); Require(references.Add(reference), $"{id}: duplicate profile override reference {reference}.");
            var path = Inside(folder, reference); Require(File.Exists(path), $"{id}: missing profile override file {reference}.");
            var content = parse(path); files.Add(new(reference, path, content, ProfileRules.Expand(content)));
        }
        return new(id, folder, file, settings, exists, files);
    }

    /// <summary>Checks the settings a profile.json may hold; the build, the grid and the profile manager share these rules.</summary>
    public static void Validate(JsonObject settings, string id)
    {
        Require(settings.I("schemaVersion") == 1 && settings["id"] is JsonValue && settings.S("id") == id, $"{id}: invalid profile schema or identity.");
        string? Text(string key)
        {
            if (settings[key] is not JsonNode node) return null;
            Require(node is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0, $"{id}: {key} must be a non-empty string."); return node.GetValue<string>();
        }
        Require(Text("stringMode") is null or "standard" or "full", $"{id}: invalid string mode.");
        Require(Text("runtime") is null or LoaderRuntime or PlainRuntime, $"{id}: runtime is \"loader\" or \"plain\".");
        Require(Text("color") is not { } color || ColorPattern.IsMatch(color), $"{id}: color is #RRGGBB.");
        if (Text("basedOn") is { } parent) Require(!parent.Equals(id, StringComparison.OrdinalIgnoreCase) && ProfileManager.IsValidId(parent), $"{id}: basedOn names another profile.");
        if (settings["modInfo"] is JsonNode modInfo)
        {
            var fields = modInfo as JsonObject ?? throw new InvalidDataException($"{id}: profile modInfo must be an object.");
            Require(fields.Count > 0 && fields.All(p => p.Key is "name" or "savepath" && p.Value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0), "Profile modInfo may only set non-empty name and savepath strings.");
            if (fields.ContainsKey("savepath")) { var save = fields.S("savepath"); Require(save.EndsWith('/') && !save.StartsWith('/') && !save.Contains('\\') && !save.Split('/').Any(part => part is "." or ".."), "Profile savepath must be a relative folder ending in '/'."); }
        }
        Require(settings["tableOverrides"] is null or JsonArray && settings["assetOverrides"] is null or JsonArray, $"{id}: tableOverrides and assetOverrides are arrays.");
    }

    /// <summary>
    /// Asset overrides of every layer with the folder their source is relative to. A child's override for a target its parent
    /// also overrides replaces the parent's; two in one layer compete.
    /// </summary>
    public IReadOnlyList<(JsonNode Asset, string Folder)> AssetOverrides()
    {
        var result = new List<(JsonNode Asset, string Folder, int Layer)>();
        for (int layer = 0; layer < Layers.Count; layer++)
            foreach (var asset in (JsonArray?)Layers[layer].Settings["assetOverrides"] ?? [])
            {
                var key = asset.S("scope", "data") + ":" + asset.S("target");
                int existing = result.FindIndex(a => a.Asset.S("scope", "data") + ":" + a.Asset.S("target") == key);
                if (existing < 0) { result.Add((asset!, Layers[layer].Folder, layer)); continue; }
                Require(result[existing].Layer != layer, "Competing asset overrides.");
                result[existing] = (asset!, Layers[layer].Folder, layer);
            }
        return [.. result.Select(a => (a.Asset, a.Folder))];
    }

    /// <summary>What decides a build's output: every layer's settings and rules (a profile's display color does not).</summary>
    public string ContentKey() => string.Join("\n", Layers.Select(l =>
    {
        var settings = (JsonObject)l.Settings.DeepClone(); settings.Remove("color");
        return l.Id + Json(settings) + string.Join("", l.Rules.Select(Json));
    }));
}
