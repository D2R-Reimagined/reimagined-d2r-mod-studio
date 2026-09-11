using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record CellOverride(string Shared, string Effective, string Reason, string? RuleFile, string ProfileFile, Dictionary<string, string> Fingerprints);
public static class ProfileEditing
{
    public static CellOverride ReadCell(ModProject project, TableData table, int row, string column, string profile)
    {
        Require(!table.IsCatalog, "Catalog profiles use compact/full translation fields.");
        var profileFile = Inside(project.Root, $"compatibility/{profile}/profile.json");
        var shared = table.Cell(row, column); var effective = shared; var reason = "Shared source";
        var fingerprints = new Dictionary<string, string>(); string? ruleFile = null;
        if (!File.Exists(profileFile)) return new(shared, shared, reason, null, profileFile, fingerprints);
        var bytes = File.ReadAllBytes(profileFile); fingerprints[profileFile] = Hash(bytes);
        var settings = JsonNode.Parse(Utf8.GetString(bytes).TrimStart('\uFEFF'))!;
        foreach (var reference in (JsonArray?)settings["tableOverrides"] ?? [])
        {
            var file = Inside(Path.GetDirectoryName(profileFile)!, reference!.GetValue<string>()); bytes = File.ReadAllBytes(file); fingerprints[file] = Hash(bytes);
            var rule = JsonNode.Parse(Utf8.GetString(bytes).TrimStart('\uFEFF'))!;
            if (rule.S("table") != table.Name || rule.S("record") != table.Records[row].S("sourceId") || rule["changes"]?[column] is not JsonNode change) continue;
            Require(ruleFile == null, "Multiple bank-specific overrides exist. Edit their source files to choose the intended bank.");
            Require(rule["targets"] == null, "This cell has a bank-specific override. Edit its source file to preserve the bank scope.");
            Require(change.S("expect") == shared, "This profile override is stale. Reconcile its expected value in Source before editing.");
            ruleFile = file; effective = change.S("value"); reason = rule.S("reason");
        }
        return new(shared, effective, reason, ruleFile, profileFile, fingerprints);
    }
    public static string WriteCell(ModProject project, TableData table, int row, string column, string profile, string value, string reason, CellOverride reviewed)
    {
        Require(reason.Trim().Length > 0, "Describe why this profile changes the cell.");
        Require(table.Cell(row, column) == reviewed.Shared, "Shared cell changed since this dialog opened.");
        var validation = new TableData(table.Schema, (JsonArray)table.Records.DeepClone()); validation.SetCell(row, column, value);
        var current = ReadCell(project, table, row, column, profile);
        Require(current.Fingerprints.Count == reviewed.Fingerprints.Count && reviewed.Fingerprints.All(p => current.Fingerprints.GetValueOrDefault(p.Key) == p.Value), "Profile files changed since review. Reopen the dialog.");
        var settings = File.Exists(current.ProfileFile) ? Read(current.ProfileFile) : new JsonObject { ["schemaVersion"] = 1, ["id"] = profile, ["stringMode"] = profile == "standard" ? "standard" : "full", ["tableOverrides"] = new JsonArray(), ["assetOverrides"] = new JsonArray() };
        var relative = "studio-overrides/" + Hash(table.Name + ":" + table.Records[row].S("sourceId") + ":" + column)[..24] + ".json";
        var path = current.RuleFile ?? Inside(Path.GetDirectoryName(current.ProfileFile)!, relative);
        Require(current.RuleFile != null || !File.Exists(path), "An unreferenced override file occupies this path. Review it before editing.");
        var oldBytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var rule = oldBytes == null ? new JsonObject { ["table"] = table.Name, ["record"] = table.Records[row].S("sourceId"), ["changes"] = new JsonObject() } : Read(path);
        rule["reason"] = reason.Trim(); rule["changes"]![column] = new JsonObject { ["expect"] = reviewed.Shared, ["value"] = value };
        if (current.RuleFile == null)
        {
            settings["tableOverrides"] ??= new JsonArray(); ((JsonArray)settings["tableOverrides"]!).Add(relative);
        }
        var newBytes = Utf8.GetBytes(Json(rule)); AtomicWrite(path, newBytes, oldBytes == null ? null : Hash(oldBytes), oldBytes == null);
        try { AtomicWrite(current.ProfileFile, Utf8.GetBytes(Json(settings)), current.Fingerprints.GetValueOrDefault(current.ProfileFile), !current.Fingerprints.ContainsKey(current.ProfileFile)); }
        catch
        {
            if (File.Exists(path) && Hash(File.ReadAllBytes(path)) == Hash(newBytes)) { if (oldBytes == null) File.Delete(path); else AtomicWrite(path, oldBytes); }
            throw;
        }
        return path;
    }
}
