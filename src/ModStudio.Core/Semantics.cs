using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record SemanticRule(string Table, string Column, string Type = "text", bool Required = false, decimal? Min = null, decimal? Max = null, string[]? Values = null, string[]? ReferenceTables = null, string ReferenceColumn = "code", string Severity = "Warning", bool Enabled = true);
public record ReferenceHit(string File, int Row, string Column, string Value, string? SourceId = null, string? CatalogId = null)
{
    public int FindRow(TableData table)
    {
        var row = SourceId is { Length: > 0 } ? Enumerable.Range(0, table.Records.Count).FirstOrDefault(r => table.Records[r]?["sourceId"]?.GetValue<string>() == SourceId, -1)
            : CatalogId != null ? Enumerable.Range(0, table.Records.Count).FirstOrDefault(r => table.Cell(r, "id") == CatalogId, -1) : Row;
        return row >= 0 && row < table.Records.Count && table.Cell(row, Column) == Value ? row : -1;
    }
    public override string ToString() => $"{Path.GetFileNameWithoutExtension(File)} · row {Row} · {Column} = {Value}";
}
public record SemanticReport(List<Diagnostic> Diagnostics, int Rules, int Cells);
public static class Semantics
{
    /// <summary>Reference-table name standing for every string catalog in the project (source/strings/*.json), matched on the given column ("Key").</summary>
    public const string StringCatalogs = "strings";
    public static List<SemanticRule> Rules(ModProject project)
    {
        var rules = new List<SemanticRule> {
            new("superuniques", "Class", ReferenceTables: ["monstats"], ReferenceColumn: "Id"),
            new("uniqueitems", "code", ReferenceTables: ["weapons", "armor", "misc"]),
            new("setitems", "item", ReferenceTables: ["weapons", "armor", "misc"]),
            // Item names are string keys; a renamed key with no catalog entry shows up in game as the raw key.
            new("uniqueitems", "index", ReferenceTables: [StringCatalogs], ReferenceColumn: "Key"),
            new("setitems", "index", ReferenceTables: [StringCatalogs], ReferenceColumn: "Key"),
            new("sets", "name", ReferenceTables: [StringCatalogs], ReferenceColumn: "Key"),
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
        if (CellReferences.Rule(table, column) is { Kind: "item-code" } pilot) return new(table, column, ReferenceTables: pilot.ReferenceTables);
        var entry = ColumnGuide.Find(table, column);
        if (entry?.RefFile == null) return null;
        return new(table, column, ReferenceTables: [entry.RefFile.ToLowerInvariant()], ReferenceColumn: entry.RefField ?? "code");
    }
    public static string TableFile(ModProject project, string name) => TableData.FileFor(project, "tables", name);
    /// <summary>The files a reference-table name stands for: one table file, or every string catalog for <see cref="StringCatalogs"/> (including unsaved open catalogs).</summary>
    public static string[] ReferenceFiles(ModProject project, string name, IReadOnlyDictionary<string, TableData>? buffers = null)
    {
        if (name != StringCatalogs) { var file = TableFile(project, name); return File.Exists(file) || buffers?.ContainsKey(file) == true ? [file] : []; }
        var folder = Path.Combine(project.Root, "source", "strings");
        return (Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : [])
            .Concat(buffers?.Where(b => b.Value.IsCatalog).Select(b => b.Key) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
    }
    public static List<ReferenceHit> References(ModProject project, SemanticRule rule, string value, IReadOnlyDictionary<string, TableData>? buffers = null, CancellationToken token = default)
    {
        var result = new List<ReferenceHit>();
        foreach (var name in rule.ReferenceTables ?? [])
            foreach (var file in ReferenceFiles(project, name, buffers))
            {
                token.ThrowIfCancellationRequested();
                var table = buffers?.GetValueOrDefault(file) ?? TableData.Load(file);
                Require(table.Validate(file).Count == 0, "Reference table is invalid: " + Path.GetFileNameWithoutExtension(file));
                var column = table.Columns.FirstOrDefault(c => c == rule.ReferenceColumn) ?? table.Columns.FirstOrDefault(c => c.Equals(rule.ReferenceColumn, StringComparison.OrdinalIgnoreCase));
                Require(column != null, $"Reference column {name}/{rule.ReferenceColumn} is missing.");
                for (int row = 0; row < table.Records.Count; row++) if (table.Cell(row, column!) == value && value.Length > 0) result.Add(new(file, row, column!, value, table.IsCatalog ? null : table.Records[row]?["sourceId"]?.GetValue<string>(), table.IsCatalog ? table.Cell(row, "id") : null));
            }
        return result;
    }
    // Checks shared authored values. Profile builds separately validate all runtime overrides and banks.
    public static SemanticReport Check(ModProject project, IReadOnlyDictionary<string, TableData>? buffers = null, CancellationToken token = default)
    {
        var diagnostics = new List<Diagnostic>(); int cells = 0, checkedRules = 0;
        var tables = new Dictionary<string, TableData?>(StringComparer.OrdinalIgnoreCase);
        TableData? LoadFile(string file)
        {
            if (tables.TryGetValue(file, out var existing)) return existing;
            var table = buffers?.GetValueOrDefault(file) ?? (File.Exists(file) ? TableData.Load(file) : null);
            if (table != null) { var errors = table.Validate(file); diagnostics.AddRange(errors); if (errors.Count > 0) table = null; }
            tables[file] = table; return table;
        }
        TableData? Load(string name) => LoadFile(TableFile(project, name));
        try
        {
            foreach (var rule in Rules(project))
            {
                token.ThrowIfCancellationRequested(); var file = TableFile(project, rule.Table); var table = Load(rule.Table); if (table == null) continue;
                if (!table.Columns.Contains(rule.Column)) { diagnostics.Add(new(file, "Semantic rule references a missing column: " + rule.Column, rule.Severity)); continue; }
                // A project without any string catalog has nothing to check keys against; the rule is skipped rather than reported as incomplete for every item table.
                if (rule.ReferenceTables?.Contains(StringCatalogs) == true && ReferenceFiles(project, StringCatalogs, buffers).Length == 0) continue;
                checkedRules++;
                var keys = new HashSet<string>(StringComparer.Ordinal); bool missingTargets = false;
                foreach (var name in rule.ReferenceTables ?? [])
                {
                    var files = ReferenceFiles(project, name, buffers); if (files.Length == 0) { missingTargets = true; continue; }
                    foreach (var targetFile in files)
                    {
                        var target = LoadFile(targetFile);
                        if (target == null || !target.Columns.Contains(rule.ReferenceColumn)) { missingTargets = true; continue; }
                        for (int r = 0; r < target.Records.Count; r++) keys.Add(target.Cell(r, rule.ReferenceColumn));
                    }
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
