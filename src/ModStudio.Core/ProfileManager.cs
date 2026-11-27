using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>Editable profile settings, as the profile manager shows them. Null fields are inherited (or the default).</summary>
public sealed record ProfileSettings(string? BasedOn, string? StringMode, string? Runtime, string? ModName, string? SavePath, string? Color);

/// <summary>
/// Adds, renames, deletes and configures a project's profiles: the folders under compatibility/. Every operation checks
/// the profiles that are based on the one it changes, so no basedOn is left pointing at nothing.
/// </summary>
public static class ProfileManager
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]{0,39}$");
    public static bool IsValidId(string id) => IdPattern.IsMatch(id);
    public static void ValidateId(string id) => Require(IsValidId(id), "Profile names use 1–40 lowercase letters, digits, '-' or '_', starting with a letter or digit.");

    private static string Folder(ModProject project, string id) => Inside(project.Root, "compatibility/" + id);
    private static bool Exists(ModProject project, string id) => project.Profiles.Contains(id, StringComparer.OrdinalIgnoreCase);
    private static JsonObject Settings(ModProject project, string id)
    {
        var file = ProfileChain.SettingsPath(project.Root, id);
        return File.Exists(file) ? Read(file) as JsonObject ?? throw new InvalidDataException($"{id}/profile.json must be an object.") : ProfileChain.DefaultSettings(id);
    }

    /// <summary>Profiles whose basedOn names <paramref name="id"/>.</summary>
    public static IReadOnlyList<string> Children(ModProject project, string id) =>
        [.. project.Profiles.Where(p => p != id && Settings(project, p).S("basedOn") == id)];

    public static ProfileSettings Load(ModProject project, string id)
    {
        var settings = Settings(project, id);
        string? Text(string key) => settings[key]?.GetValue<string>();
        return new(Text("basedOn"), Text("stringMode"), Text("runtime"), settings["modInfo"]?.S("name") is { Length: > 0 } name ? name : null,
            settings["modInfo"]?.S("savepath") is { Length: > 0 } save ? save : null, Text("color"));
    }

    /// <summary>A new profile: empty (optionally based on another), or a copy of every file of <paramref name="copyFrom"/>.</summary>
    public static void Create(ModProject project, string id, string? basedOn = null, string? copyFrom = null, string? color = null)
    {
        ValidateId(id); Require(!Exists(project, id), $"A profile named {id} already exists.");
        Require(basedOn == null || copyFrom == null, "Choose either a parent profile or a profile to copy.");
        if (basedOn != null) Require(Exists(project, basedOn), $"Unknown profile {basedOn}.");
        var folder = Folder(project, id);
        if (copyFrom != null)
        {
            Require(Exists(project, copyFrom), $"Unknown profile {copyFrom}.");
            // Staged outside compatibility/, which would otherwise list the half-copied folder as a profile.
            var source = Folder(project, copyFrom); var stage = Inside(project.Cache, "profile-stage/" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var file in Files(source)) { var target = Inside(stage, Relative(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); }
                var settings = Settings(project, copyFrom); settings["id"] = id;
                if (color != null) settings["color"] = color; else settings.Remove("color");
                ProfileChain.Validate(settings, id);
                WriteJson(Path.Combine(stage, "profile.json"), settings);
                Directory.Move(stage, folder);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            return;
        }
        var fresh = new JsonObject { ["schemaVersion"] = 1, ["id"] = id };
        if (basedOn != null) fresh["basedOn"] = basedOn;
        else { fresh["stringMode"] = "full"; fresh["runtime"] = ProfileChain.PlainRuntime; }
        if (color != null) fresh["color"] = color;
        fresh["tableOverrides"] = new JsonArray(); fresh["assetOverrides"] = new JsonArray();
        ProfileChain.Validate(fresh, id);
        AtomicWrite(ProfileChain.SettingsPath(project.Root, id), Utf8.GetBytes(Json(fresh)), requireAbsent: true);
        // The chain must resolve (an existing parent, no cycle) before the profile counts as created.
        try { ProfileChain.Load(project.Root, id); } catch { Directory.Delete(folder, true); throw; }
    }

    /// <summary>
    /// Writes the settings the manager edits, leaving everything else in profile.json as it is. A change of parent must keep
    /// the chain free of cycles and is refused when it would leave the profile's own overrides stale or broken: a child's
    /// changes expect its parent's values.
    /// </summary>
    public static void Update(ModProject project, string id, ProfileSettings next)
    {
        Require(Exists(project, id), $"Unknown profile {id}.");
        var file = ProfileChain.SettingsPath(project.Root, id); var original = File.Exists(file) ? File.ReadAllBytes(file) : null;
        var settings = Settings(project, id);
        void Set(string key, string? value) { if (string.IsNullOrWhiteSpace(value)) settings.Remove(key); else settings[key] = value.Trim(); }
        Set("basedOn", next.BasedOn); Set("stringMode", next.StringMode); Set("runtime", next.Runtime); Set("color", next.Color);
        var modInfo = new JsonObject();
        if (!string.IsNullOrWhiteSpace(next.ModName)) { ModProject.ValidateName(next.ModName.Trim()); modInfo["name"] = next.ModName.Trim(); }
        if (!string.IsNullOrWhiteSpace(next.SavePath)) modInfo["savepath"] = next.SavePath.Trim();
        if (modInfo.Count > 0) settings["modInfo"] = modInfo; else settings.Remove("modInfo");
        if (next.BasedOn is { Length: > 0 } parent) Require(Exists(project, parent), $"Unknown profile {parent}.");
        ProfileChain.Validate(settings, id);
        AtomicWrite(file, Utf8.GetBytes(Json(settings)), original == null ? null : Hash(original), original == null);
        try { foreach (var profile in Children(project, id).Prepend(id)) ProfileChain.Load(project.Root, profile); }
        catch { if (original == null) File.Delete(file); else AtomicWrite(file, original); throw; }
    }

    /// <summary>
    /// Moves compatibility/&lt;from&gt;/ to compatibility/&lt;to&gt;/, rewrites its id and every child's basedOn, and carries the
    /// profile's local run settings along. Returns the files rewritten outside the moved folder.
    /// </summary>
    public static IReadOnlyList<string> Rename(ModProject project, string from, string to)
    {
        ValidateId(to); Require(Exists(project, from), $"Unknown profile {from}.");
        Require(!Exists(project, to), $"A profile named {to} already exists.");
        var children = Children(project, from);
        // "d2rl" and "standard" imply a runtime and string mode by name; a renamed profile states them so it builds the same.
        var effective = ProfileChain.Load(project.Root, from);
        var source = Folder(project, from); var target = Folder(project, to);
        Directory.Move(source, target);
        var touched = new List<string>();
        try
        {
            var file = Path.Combine(target, "profile.json"); var settings = Settings(project, to); settings["id"] = to;
            if (settings["basedOn"] == null) { settings["runtime"] ??= effective.Runtime; settings["stringMode"] ??= effective.StringMode; }
            WriteJson(file, settings);
            foreach (var child in children)
            {
                var childFile = ProfileChain.SettingsPath(project.Root, child); var childSettings = Settings(project, child);
                childSettings["basedOn"] = to; WriteJson(childFile, childSettings); touched.Add(childFile);
            }
        }
        catch
        {
            foreach (var child in children) { var childFile = ProfileChain.SettingsPath(project.Root, child); var s = Settings(project, child); s["basedOn"] = from; WriteJson(childFile, s); }
            if (Directory.Exists(target) && !Directory.Exists(source)) Directory.Move(target, source);
            var back = Path.Combine(source, "profile.json"); if (File.Exists(back)) { var s = Read(back); s["id"] = from; WriteJson(back, s); }
            throw;
        }
        var run = RunSettings.SettingsFile(project, from);
        if (File.Exists(run)) { var moved = RunSettings.SettingsFile(project, to); if (!File.Exists(moved)) File.Move(run, moved); }
        return touched;
    }

    /// <summary>
    /// Moves a profile's folder to .studio/deleted-profiles/ (it is not erased, and git still has it). Refused for the last
    /// profile and for a profile others are based on.
    /// </summary>
    public static string Delete(ModProject project, string id)
    {
        Require(Exists(project, id), $"Unknown profile {id}.");
        Require(project.Profiles.Count() > 1, "A project keeps at least one profile.");
        var children = Children(project, id);
        Require(children.Count == 0, $"{string.Join(", ", children)} {(children.Count == 1 ? "is" : "are")} based on {id}. Change {(children.Count == 1 ? "its" : "their")} parent first.");
        var trash = Inside(project.Cache, $"deleted-profiles/{id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
        Directory.Move(Folder(project, id), trash);
        var run = RunSettings.SettingsFile(project, id);
        if (File.Exists(run)) File.Move(run, Path.Combine(trash, "run-settings.json"));
        return trash;
    }
}
