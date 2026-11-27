using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record CellOverride(string Shared, string Effective, string Reason, string? RuleFile, string ProfileFile, Dictionary<string, string> Fingerprints);

/// <summary>An edit of a profile's own layer, by table row.</summary>
public abstract record ProfileEdit(int Row);
/// <summary>The profile builds this value. Setting the value the profile would get anyway (shared or inherited) drops its change.</summary>
public sealed record SetProfileCell(int Row, string Column, string Value) : ProfileEdit(Row);
/// <summary>Drops the profile's own change, so the cell builds what its parent (or shared source) has.</summary>
public sealed record RevertProfileCell(int Row, string Column) : ProfileEdit(Row);
/// <summary>Keeps the profile's value but expects the current shared (or inherited) value, clearing a stale change.</summary>
public sealed record AcceptSharedValue(int Row, string Column) : ProfileEdit(Row);
public sealed record RemoveProfileRow(int Row) : ProfileEdit(Row);
public sealed record RestoreProfileRow(int Row) : ProfileEdit(Row);

/// <summary>A file a profile edit wrote, created (Before null) or deleted (After null).</summary>
public sealed record ProfileFileChange(string Path, byte[]? Before, byte[]? After);
public sealed record ProfileWrite(string Profile, IReadOnlyList<ProfileFileChange> Files)
{
    public bool IsEmpty => Files.Count == 0;
}

/// <summary>
/// Writes a profile's own overrides. Studio keeps the overrides it writes in one editable rule set per table,
/// compatibility/&lt;profile&gt;/studio-overrides/&lt;table&gt;.json; changes that already live in another editable file are
/// updated where they are. Generated rule sets and bank-specific rules are left to their authors.
/// </summary>
public static class ProfileEditing
{
    public static string StudioReference(string table) => $"studio-overrides/{table}.json";

    /// <summary>The single-cell view the inspector's override dialog shows. Throws for cells Studio must not edit there.</summary>
    public static CellOverride ReadCell(ModProject project, TableData table, int row, string column, string profile)
    {
        var chain = ProfileChain.Load(project.Root, profile, fingerprints: true);
        var overlay = ProfileOverlay.Compute(chain, table);
        if (overlay.Removed(row) is { } removed) throw new InvalidDataException($"This row is left out of the {profile} build ({Relative(project.Root, removed.RuleFile)}).");
        var shared = table.Cell(row, column); var state = overlay.Cell(row, column);
        var fingerprints = new Dictionary<string, string>(chain.Fingerprints);
        if (state == null) return new(shared, shared, "Shared source", null, chain.Own.SettingsFile, fingerprints);
        if (state.Layer != profile) return new(shared, state.Value, $"Inherited from {state.Layer}: {state.Reason}", null, chain.Own.SettingsFile, fingerprints);
        Require(state.Editable, state.Locked!);
        Require(!state.Stale, "This profile override is stale. Reconcile its expected value in Source before editing.");
        return new(shared, state.Value, state.Reason, state.RuleFile, chain.Own.SettingsFile, fingerprints);
    }

    /// <summary>The dialog's save: one cell, refused when any profile file changed since <paramref name="reviewed"/> was read.</summary>
    public static string WriteCell(ModProject project, TableData table, int row, string column, string profile, string value, string reason, CellOverride reviewed)
    {
        Require(table.Cell(row, column) == reviewed.Shared, "Shared cell changed since this dialog opened.");
        Apply(project, table, profile, [new SetProfileCell(row, column, value)], reason, reviewed.Fingerprints);
        var state = ProfileOverlay.Compute(project, table, profile).Own(row, column);
        return state?.RuleFile ?? ProfileChain.SettingsPath(project.Root, profile);
    }

    private sealed class WorkFile(string reference, string path, JsonNode? root, byte[]? original, bool generated)
    {
        public string Reference { get; } = reference;
        public string Path { get; } = path;
        public JsonNode? Root { get; set; } = root;
        public byte[]? Original { get; } = original;
        public bool Generated { get; } = generated;
        public bool IsSet => Root != null && ProfileRules.IsSet(Root);
        public IEnumerable<JsonObject> Rules => Root == null ? [] : IsSet ? Root["rules"]!.AsArray().OfType<JsonObject>() : [(JsonObject)Root];
    }

    /// <summary>
    /// Applies edits to the profile's own layer and writes the files that changed. Refused when the result would give the
    /// table a problem it did not have (competing rules, a removed row that kept rows follow). The shared table must be the
    /// saved one: changes record its values as what they expect.
    /// </summary>
    public static ProfileWrite Apply(ModProject project, TableData table, string profile, IReadOnlyList<ProfileEdit> edits, string reason, IReadOnlyDictionary<string, string>? reviewed = null)
    {
        Require(edits.Count > 0, "Nothing to change.");
        var chain = ProfileChain.Load(project.Root, profile, fingerprints: true);
        if (reviewed != null) Require(reviewed.Count == chain.Fingerprints.Count && reviewed.All(p => chain.Fingerprints.GetValueOrDefault(p.Key) == p.Value), "Profile files changed since review. Reopen the dialog.");
        var before = ProfileOverlay.Compute(chain, table); var own = chain.Own;
        var settings = (JsonObject)own.Settings.DeepClone();
        var files = own.RuleFiles.Select(f => new WorkFile(f.Reference, f.Path, f.Root.DeepClone(), File.ReadAllBytes(f.Path), f.Generated)).ToList();
        reason = reason.Trim();
        string Record(int row) { Require(row >= 0 && row < table.Records.Count, "Select a table row."); return table.Records[row].S("sourceId"); }
        bool Mine(JsonObject rule, string record) => rule.S("table") == table.Name && rule.S("record") == record;
        void Unlocked(WorkFile file, JsonObject rule)
        {
            Require(!file.Generated, ProfileRules.Generated(file.Root!, $"{profile}/{file.Reference}"));
            Require(rule["targets"] == null, $"This override only applies to some banks. Edit {profile}/{file.Reference} to keep its bank scope.");
        }
        (WorkFile File, JsonObject Rule)? OwnChange(string record, string column)
        {
            var found = files.SelectMany(f => f.Rules.Where(r => Mine(r, record) && r["changes"]?[column] != null).Select(r => (File: f, Rule: r))).ToArray();
            Require(found.Length <= 1, $"Competing overrides in {profile} for {record}/{column}. Edit its files to choose one.");
            return found.Length == 0 ? null : found[0];
        }
        (WorkFile File, JsonObject Rule)? OwnRemoval(string record) =>
            files.SelectMany(f => f.Rules.Where(r => Mine(r, record) && r["remove"] != null).Select(r => (File: f, Rule: r))).Cast<(WorkFile, JsonObject)?>().FirstOrDefault();
        void DropRule(WorkFile file, JsonObject rule)
        {
            if (!file.IsSet) { file.Root = null; return; }
            var list = file.Root!["rules"]!.AsArray(); list.Remove(rule);
            if (list.Count == 0) file.Root = null;
        }
        void DropChange(WorkFile file, JsonObject rule, string column)
        {
            var changes = rule["changes"]!.AsObject(); changes.Remove(column);
            if (changes.Count == 0) DropRule(file, rule);
        }
        WorkFile Studio()
        {
            var reference = StudioReference(table.Name);
            if (files.FirstOrDefault(f => f.Reference.Equals(reference, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                Require(existing.Root == null || existing.IsSet && !existing.Generated, $"{profile}/{reference} is not a Studio rule set. Move its rules or rename it.");
                existing.Root ??= new JsonObject { ["rules"] = new JsonArray() };
                return existing;
            }
            var path = Inside(own.Folder, reference);
            Require(!File.Exists(path), $"An unreferenced file occupies {profile}/{reference}. Add it to tableOverrides or move it before editing.");
            var created = new WorkFile(reference, path, new JsonObject { ["rules"] = new JsonArray() }, null, false); files.Add(created); return created;
        }
        void RequireReason() => Require(reason.Length > 0, $"Describe why {profile} changes this.");
        void AddChange(string record, string column, string expect, string value)
        {
            RequireReason(); var set = Studio(); var rules = set.Root!["rules"]!.AsArray();
            var rule = rules.OfType<JsonObject>().FirstOrDefault(r => Mine(r, record) && r.S("reason") == reason && r["targets"] == null && r["changes"] is JsonObject);
            if (rule == null) { rule = new JsonObject { ["table"] = table.Name, ["record"] = record, ["reason"] = reason, ["changes"] = new JsonObject() }; rules.Add(rule); }
            rule["changes"]![column] = new JsonObject { ["expect"] = expect, ["value"] = value };
        }
        foreach (var edit in edits)
        {
            var record = Record(edit.Row);
            switch (edit)
            {
                case SetProfileCell set:
                {
                    Require(before.Removed(set.Row) == null, $"This row is left out of the {profile} build. Restore it before changing its cells.");
                    if (OwnRemoval(record) != null) throw new InvalidDataException($"This row is left out of the {profile} build. Restore it before changing its cells.");
                    var input = before.Inherited(set.Row, set.Column);
                    if (OwnChange(record, set.Column) is { } current)
                    {
                        Unlocked(current.File, current.Rule);
                        if (set.Value == input) { DropChange(current.File, current.Rule, set.Column); break; }
                        var change = current.Rule["changes"]![set.Column]!;
                        bool sameReason = reason.Length == 0 || current.Rule.S("reason") == reason;
                        if (sameReason || current.Rule["changes"]!.AsObject().Count == 1)
                        {
                            change["expect"] = input; change["value"] = set.Value;
                            if (!sameReason) current.Rule["reason"] = reason;
                        }
                        else { DropChange(current.File, current.Rule, set.Column); AddChange(record, set.Column, input, set.Value); }
                    }
                    else if (set.Value != input) AddChange(record, set.Column, input, set.Value);
                    break;
                }
                case RevertProfileCell revert:
                {
                    if (OwnChange(record, revert.Column) is not { } current)
                    {
                        Require(before.Cell(revert.Row, revert.Column) == null, $"This value comes from {before.Cell(revert.Row, revert.Column)?.Layer}. Revert it in that profile, or set a value here.");
                        break;
                    }
                    Unlocked(current.File, current.Rule); DropChange(current.File, current.Rule, revert.Column);
                    break;
                }
                case AcceptSharedValue accept:
                {
                    var current = OwnChange(record, accept.Column) ?? throw new InvalidDataException($"{profile} has no change of its own here.");
                    Unlocked(current.File, current.Rule);
                    var input = before.Inherited(accept.Row, accept.Column); var change = current.Rule["changes"]![accept.Column]!;
                    if (change.S("value") == input) DropChange(current.File, current.Rule, accept.Column); else change["expect"] = input;
                    break;
                }
                case RemoveProfileRow:
                {
                    if (before.Removed(edit.Row) is { } gone && gone.Layer != profile) throw new InvalidDataException($"{gone.Layer} already leaves this row out.");
                    if (OwnRemoval(record) != null) break;
                    RequireReason();
                    // Removing a row makes the profile's changes to it meaningless, and the build refuses both on one row.
                    foreach (var file in files.ToArray())
                        foreach (var rule in file.Rules.Where(r => Mine(r, record) && r["changes"] != null).ToArray()) { Unlocked(file, rule); DropRule(file, rule); }
                    Studio().Root!["rules"]!.AsArray().Add(new JsonObject { ["table"] = table.Name, ["record"] = record, ["reason"] = reason, ["remove"] = true });
                    break;
                }
                case RestoreProfileRow:
                {
                    if (OwnRemoval(record) is { } removal) { Unlocked(removal.File, removal.Rule); DropRule(removal.File, removal.Rule); break; }
                    Require(before.Removed(edit.Row) == null, $"{before.Removed(edit.Row)?.Layer} leaves this row out. Restore it in that profile.");
                    break;
                }
            }
        }
        // Studio's own rule set keeps table order, so its diffs read like the table.
        var order = new Dictionary<string, int>(StringComparer.Ordinal); for (int i = 0; i < table.Records.Count; i++) order.TryAdd(table.Records[i].S("sourceId"), i);
        var columnOrder = table.Columns.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i, StringComparer.Ordinal);
        if (files.FirstOrDefault(f => f.Reference == StudioReference(table.Name) && f.Root != null) is { } studio)
        {
            var sorted = studio.Rules.OrderBy(r => r.S("table") == table.Name ? order.GetValueOrDefault(r.S("record"), int.MaxValue) : int.MaxValue).ThenBy(r => r.S("reason"), StringComparer.Ordinal).ToArray();
            foreach (var rule in sorted)
                if (rule["changes"] is JsonObject changes)
                {
                    var entries = changes.OrderBy(p => columnOrder.GetValueOrDefault(p.Key, int.MaxValue)).Select(p => (p.Key, Value: p.Value!.DeepClone())).ToArray();
                    changes.Clear(); foreach (var (key, value) in entries) changes[key] = value;
                }
            var list = studio.Root!["rules"]!.AsArray(); list.Clear(); foreach (var rule in sorted) list.Add(rule);
        }
        foreach (var file in files) if (file.IsSet && file.Root!["rules"]!.AsArray().Count == 0) file.Root = null;
        // Profile.json names the files that remain, in their original order, new ones last.
        var references = new JsonArray(files.Where(f => f.Root != null).Select(f => (JsonNode?)JsonValue.Create(f.Reference)).ToArray());
        if (!JsonNode.DeepEquals(references, settings["tableOverrides"] ?? new JsonArray())) settings["tableOverrides"] = references;
        var layer = own with
        {
            Settings = settings, Exists = true,
            RuleFiles = [.. files.Where(f => f.Root != null).Select(f => new ProfileRuleFile(f.Reference, f.Path, f.Root!, ProfileRules.Expand(f.Root!)))],
        };
        var after = ProfileOverlay.Compute(chain.WithOwn(layer), table);
        var added = after.StructuralIssues.Except(before.StructuralIssues).FirstOrDefault();
        Require(added == null, "This edit would break the profile: " + added);

        var written = new List<ProfileFileChange>();
        foreach (var file in files)
        {
            var next = file.Root == null ? null : Utf8.GetBytes(Json(file.Root));
            if (file.Original == null && next == null) continue;
            if (file.Original != null && next != null && (file.Original.AsSpan().SequenceEqual(next) || JsonNode.DeepEquals(JsonNode.Parse(Utf8.GetString(file.Original).TrimStart('﻿')), file.Root))) continue;
            written.Add(new(file.Path, file.Original, next));
        }
        var settingsBytes = own.Exists ? File.ReadAllBytes(own.SettingsFile) : null;
        if (!own.Exists || !JsonNode.DeepEquals(own.Settings, settings)) written.Add(new(own.SettingsFile, settingsBytes, Utf8.GetBytes(Json(settings))));
        if (written.Count == 0) return new(profile, []);
        Commit(written, chain.Fingerprints);
        return new(profile, written);
    }

    /// <summary>
    /// Writes new and changed files first, then profile.json, and deletes files it no longer names last, so an interrupted
    /// write never leaves profile.json naming a file that is gone. A failure restores what was already written.
    /// </summary>
    private static bool IsSettings(string path) => Path.GetFileName(path).Equals("profile.json", StringComparison.OrdinalIgnoreCase);
    private static void Commit(IReadOnlyList<ProfileFileChange> changes, IReadOnlyDictionary<string, string> fingerprints)
    {
        var ordered = changes.Where(c => c.After != null && !IsSettings(c.Path))
            .Concat(changes.Where(c => IsSettings(c.Path)))
            .Concat(changes.Where(c => c.After == null)).ToArray();
        var done = new List<ProfileFileChange>();
        try
        {
            foreach (var change in ordered)
            {
                var expected = change.Before == null ? null : fingerprints.GetValueOrDefault(change.Path) ?? Hash(change.Before);
                if (change.After == null)
                {
                    Require(File.Exists(change.Path) && Hash(File.ReadAllBytes(change.Path)) == expected, $"File changed externally: {change.Path}. Reload or reconcile before saving.");
                    File.Delete(change.Path);
                }
                else AtomicWrite(change.Path, change.After, expected, change.Before == null);
                done.Add(change);
            }
        }
        catch
        {
            foreach (var change in Enumerable.Reverse(done))
                try { if (change.Before == null) File.Delete(change.Path); else AtomicWrite(change.Path, change.Before); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Puts back what a profile write replaced, provided nothing changed those files since.</summary>
    public static void Undo(ProfileWrite write)
    {
        foreach (var change in write.Files)
        {
            var current = File.Exists(change.Path) ? File.ReadAllBytes(change.Path) : null;
            Require(current == null ? change.After == null : change.After != null && current.AsSpan().SequenceEqual(change.After), $"{Path.GetFileName(change.Path)} changed after this edit; undo it in its file.");
        }
        var reverse = write.Files.Select(c => new ProfileFileChange(c.Path, c.After, c.Before)).ToArray();
        Commit(reverse, reverse.Where(c => c.Before != null).ToDictionary(c => c.Path, c => Hash(c.Before!)));
    }
}

/// <summary>
/// A tableOverrides entry names a rule file. It holds one rule, or a rule set: {"generator": "...", "rules": [rule, ...]}.
/// A set applies exactly like its rules listed one per file. Studio edits sets without a generator (it writes its own
/// that way); a generated set is left to its script, which would overwrite the change.
/// </summary>
public static class ProfileRules
{
    public static bool IsSet(JsonNode file) => file is JsonObject o && o.ContainsKey("rules");
    public static bool IsGenerated(JsonNode file) => IsSet(file) && file["generator"] != null;

    /// <summary>
    /// A rule {"table", "record", "reason", "remove": true} leaves the record out of the profile's build. It carries no changes,
    /// and the build accepts it only for a table's last rows, so no row the profile keeps changes position.
    /// </summary>
    public static bool Removes(JsonNode rule)
    {
        if (rule["remove"] is not JsonNode flag) return false;
        Require(flag is JsonValue v && v.TryGetValue<bool>(out var on) && on && rule["changes"] == null, "A remove rule sets \"remove\": true and no changes.");
        return true;
    }

    public static JsonNode[] Expand(JsonNode file)
    {
        if (!IsSet(file)) return [file];
        var set = (JsonObject)file;
        Require(set.All(p => p.Key is "rules" || p.Key is "generator" && p.Value is JsonValue v && v.TryGetValue<string>(out _)), "A rule set holds only \"rules\" and an optional \"generator\" string.");
        Require(set["rules"] is JsonArray { Count: > 0 } list && list.All(r => r is JsonObject x && !x.ContainsKey("rules")), "A rule set's \"rules\" must be a non-empty array of rule objects.");
        return [.. set["rules"]!.AsArray().Select(r => r!)];
    }

    public static string Generated(JsonNode file, string reference) =>
        $"This cell is set by the generated rule set {reference}" + (file["generator"] is JsonNode g ? $" ({g.GetValue<string>()})" : "") + ". Change it at its source.";
}
