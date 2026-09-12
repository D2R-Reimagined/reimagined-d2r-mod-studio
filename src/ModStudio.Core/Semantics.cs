using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record SemanticRule(string Table, string Column, string Type = "text", bool Required = false, decimal? Min = null, decimal? Max = null, string[]? Values = null, string[]? ReferenceTables = null, string ReferenceColumn = "code", string Severity = "Warning", bool Enabled = true);
public record ReferenceHit(string File, int Row, string Column, string Value) { public override string ToString() => $"{Path.GetFileName(Path.GetDirectoryName(File))} · row {Row} · {Column} = {Value}"; }
public record SemanticReport(List<Diagnostic> Diagnostics, int Rules, int Cells);
public static class Semantics
{
    public static List<SemanticRule> Rules(ModProject project)
    {
        var rules = new List<SemanticRule> {
            new("superuniques", "Class", ReferenceTables: ["monstats"], ReferenceColumn: "Id"),
            new("uniqueitems", "code", ReferenceTables: ["weapons", "armor", "misc"]),
            new("setitems", "item", ReferenceTables: ["weapons", "armor", "misc"]),
            new("weapons", "type", ReferenceTables: ["itemtypes"], ReferenceColumn: "Code"),
            new("armor", "type", ReferenceTables: ["itemtypes"], ReferenceColumn: "Code"),
            new("misc", "type", ReferenceTables: ["itemtypes"], ReferenceColumn: "Code")
        };
        var file = Inside(project.Root, "source/semantics.json");
        if (File.Exists(file))
        {
            var node = Read(file); Require(node.I("schemaVersion") == 1, "Unsupported semantic rules version.");
            foreach (var item in (JsonArray)node["rules"]!)
            {
                var rule = item!.Deserialize<SemanticRule>(Pretty)!;
                Require(rule.Type is "text" or "integer" or "number" or "boolean" && rule.Severity is "Warning" or "Error", "Invalid semantic rule type or severity.");
                Require(!string.IsNullOrWhiteSpace(rule.Table) && !string.IsNullOrWhiteSpace(rule.Column), "Semantic rules need a table and column.");
                rules.RemoveAll(r => r.Table == rule.Table && r.Column == rule.Column); if (rule.Enabled) rules.Add(rule);
            }
        }
        return rules;
    }
    /// <summary>Navigation-only rule from the bundled data guide when the column is documented as a reference to another table. Not used by Check: guide targets can be enum pages or index-based.</summary>
    public static SemanticRule? GuideReference(string table, string column)
    {
        var entry = ColumnGuide.Find(table, column);
        if (entry?.RefFile == null) return null;
        return new(table, column, ReferenceTables: [entry.RefFile.ToLowerInvariant()], ReferenceColumn: entry.RefField ?? "code");
    }
    public static string TableFile(ModProject project, string name) => Inside(project.Root, $"source/tables/{name}/records.json");
    public static List<ReferenceHit> References(ModProject project, SemanticRule rule, string value, IReadOnlyDictionary<string, TableData>? buffers = null, CancellationToken token = default)
    {
        var result = new List<ReferenceHit>();
        foreach (var name in rule.ReferenceTables ?? [])
        {
            token.ThrowIfCancellationRequested(); var file = TableFile(project, name);
            if (!File.Exists(file)) continue;
            var table = buffers?.GetValueOrDefault(file) ?? TableData.Load(file);
            Require(table.Validate(file).Count == 0, "Reference table is invalid: " + name);
            var column = table.Columns.FirstOrDefault(c => c == rule.ReferenceColumn) ?? table.Columns.FirstOrDefault(c => c.Equals(rule.ReferenceColumn, StringComparison.OrdinalIgnoreCase));
            Require(column != null, $"Reference column {name}/{rule.ReferenceColumn} is missing.");
            for (int row = 0; row < table.Records.Count; row++) if (table.Cell(row, column!) == value && value.Length > 0) result.Add(new(file, row, column!, value));
        }
        return result;
    }
    // Checks shared authored values. Profile builds separately validate all runtime overrides and banks.
    public static SemanticReport Check(ModProject project, IReadOnlyDictionary<string, TableData>? buffers = null, CancellationToken token = default)
    {
        var diagnostics = new List<Diagnostic>(); int cells = 0, checkedRules = 0;
        var tables = new Dictionary<string, TableData?>();
        TableData? Load(string name)
        {
            if (tables.TryGetValue(name, out var existing)) return existing;
            var file = TableFile(project, name); var table = buffers?.GetValueOrDefault(file) ?? (File.Exists(file) ? TableData.Load(file) : null);
            if (table != null) { var errors = table.Validate(file); diagnostics.AddRange(errors); if (errors.Count > 0) table = null; }
            tables[name] = table; return table;
        }
        try
        {
            foreach (var rule in Rules(project))
            {
                token.ThrowIfCancellationRequested(); var file = TableFile(project, rule.Table); var table = Load(rule.Table); if (table == null) continue;
                if (!table.Columns.Contains(rule.Column)) { diagnostics.Add(new(file, "Semantic rule references a missing column: " + rule.Column, rule.Severity)); continue; }
                checkedRules++;
                var keys = new HashSet<string>(StringComparer.Ordinal); bool missingTargets = false;
                foreach (var name in rule.ReferenceTables ?? [])
                {
                    var target = Load(name);
                    if (target == null || !target.Columns.Contains(rule.ReferenceColumn)) { missingTargets = true; continue; }
                    for (int r = 0; r < target.Records.Count; r++) keys.Add(target.Cell(r, rule.ReferenceColumn));
                }
                if (missingTargets) diagnostics.Add(new(file, $"Reference check for {rule.Column} is incomplete: a target table/column is absent or invalid.", "Warning", Field: rule.Column));
                for (int row = 0; row < table.Records.Count; row++)
                {
                    token.ThrowIfCancellationRequested(); var value = table.Cell(row, rule.Column); cells++;
                    void Error(string message) => diagnostics.Add(new(file, message, rule.Severity, row, rule.Column));
                    if (value.Length == 0) { if (rule.Required) Error("A value is required."); continue; }
                    if (rule.Type is "integer" or "number")
                    {
                        var style = rule.Type == "integer" ? NumberStyles.AllowLeadingSign : NumberStyles.Float;
                        if (!decimal.TryParse(value, style, CultureInfo.InvariantCulture, out var number)) Error("Expected " + rule.Type + ".");
                        else if (rule.Min is decimal min && number < min || rule.Max is decimal max && number > max) Error($"Value is outside the configured range {rule.Min}…{rule.Max}.");
                    }
                    if (rule.Type == "boolean" && value is not ("0" or "1")) Error("Expected 0 or 1.");
                    if (rule.Values != null && !rule.Values.Contains(value, StringComparer.Ordinal)) Error("Value is not in the configured choices.");
                    if (rule.ReferenceTables is { Length: > 0 } && !missingTargets && !keys.Contains(value)) Error("Unresolved reference: " + value);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { diagnostics.Add(new(Inside(project.Root, "source/semantics.json"), e.Message)); }
        return new(diagnostics, checkedRules, cells);
    }
}
