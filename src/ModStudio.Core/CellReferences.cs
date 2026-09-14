using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModStudio.Core;

public sealed record CellReferenceResult(IReadOnlyList<ReferenceHit> Hits, IReadOnlyList<string> Issues);
public sealed record CellReferenceRule(string Table, string Field, string[] Columns, string Kind, string[] Targets)
{
    public string Column { get; init; } = Field;
    public string[] ReferenceTables => Targets.Select(t => t.Split('.')[0])
        .Concat(Kind == "conditional-parameter" ? ["properties"] : Array.Empty<string>())
        .Concat(Kind == "calculation" ? ["skills", "missiles", "itemstatcost"] : Array.Empty<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>Navigation only: the audited guide registry does not impose validation rules.</summary>
public static class CellReferences
{
    private static readonly CellReferenceRule[] Rules = Load();
    private static readonly ConcurrentDictionary<(string, string), CellReferenceRule?> Matches = new();
    private static CellReferenceRule[] Load()
    {
        using var stream = typeof(CellReferences).Assembly.GetManifestResourceStream("ModStudio.Core.Assets.cell-reference-rules.json")!;
        return JsonSerializer.Deserialize<CellReferenceRule[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    public static CellReferenceRule? Rule(string table, string column) => Matches.GetOrAdd((table.ToLowerInvariant(), column), key =>
    {
        var guide = ColumnGuide.Find(key.Item1, key.Item2);
        return Rules.FirstOrDefault(r => r.Table == key.Item1 && (r.Columns.Contains(key.Item2, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(r.Field, guide?.Name, StringComparison.OrdinalIgnoreCase))) is { } rule ? rule with { Column = key.Item2 } : null;
    });

    public static CellReferenceRule FromSemantic(SemanticRule rule) => new(rule.Table, rule.Column, [rule.Column], "structured-key",
        (rule.ReferenceTables ?? []).Select(t => t + "." + rule.ReferenceColumn).ToArray());

    internal static readonly Regex Operands = new(@"\b(skill|stat|missile)\s*\(\s*'([^']+)'\s*\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool CanNavigate(CellReferenceRule rule, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-1") return false;
        if (rule.Kind == "calculation") return Operands.IsMatch(value);
        if (rule.Kind == "item-code" && value == "xxx") return false;
        if (value == "0" && (rule.Kind == "group-key" || rule.Table == "levels" && (rule.Field is "Depend" or "Vis#" or "ObjGrp#") ||
            rule.Table == "lvlprest" && rule.Field == "LevelId" || rule.Table == "superuniques" && rule.Field == "Mod#")) return false;
        return true;
    }

    public static CellReferenceResult Resolve(ModProject project, CellReferenceRule rule, string value,
        IReadOnlyDictionary<string, TableData>? buffers = null, IReadOnlySet<string>? pendingTables = null,
        CancellationToken token = default, IReadOnlyDictionary<string, string>? source = null) =>
        new CellReferenceIndex().Resolve(project, rule, value, buffers, pendingTables, token, source);
}

/// <summary>Reusable immutable table snapshots and key indexes. Open buffers must be revision snapshots.</summary>
public sealed class CellReferenceIndex
{
    private sealed class Entry(TableData table)
    {
        public TableData Table { get; } = table;
        public Dictionary<string, Dictionary<string, List<int>>> Keys { get; } = new(StringComparer.Ordinal);
    }
    private readonly object gate = new();
    private readonly Dictionary<string, (DateTime Stamp, long Length, Entry Entry)> disk = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TableData, Entry> snapshots = new();
    public int TableLoads { get; private set; }
    public int IndexBuilds { get; private set; }
    public void Clear() { lock (gate) { disk.Clear(); snapshots.Clear(); } }

    public CellReferenceResult Resolve(ModProject project, CellReferenceRule rule, string value,
        IReadOnlyDictionary<string, TableData>? buffers = null, IReadOnlySet<string>? pendingTables = null,
        CancellationToken token = default, IReadOnlyDictionary<string, string>? source = null)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var hits = new List<ReferenceHit>(); var issues = new List<string>();
            if (!CellReferences.CanNavigate(rule, value)) return new(hits, issues);
            Entry? Read(string name, string? catalog = null, bool optional = false)
            {
                token.ThrowIfCancellationRequested();
                if (pendingTables?.Contains(name) == true || catalog != null && pendingTables?.Contains("strings") == true)
                { issues.Add($"{name}: apply or fix its pending source first."); return null; }
                string file = catalog ?? Semantics.TableFile(project, name);
                try
                {
                    if (buffers?.TryGetValue(file, out var buffer) == true)
                        return snapshots.GetValue(buffer, t => Validated(t, file));
                    var info = new FileInfo(file);
                    if (!info.Exists) { if (!optional) issues.Add($"{name}: table is not in this project."); return null; }
                    if (disk.TryGetValue(file, out var cached) && cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length) return cached.Entry;
                    var entry = Validated(TableData.Load(file), file); TableLoads++;
                    if (disk.Count >= 128) disk.Clear();
                    disk[file] = (info.LastWriteTimeUtc, info.Length, entry); return entry;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { issues.Add($"{name}: {ex.Message}"); return null; }
            }
            Entry Validated(TableData table, string file)
            {
                token.ThrowIfCancellationRequested();
                var errors = table.Validate(file);
                if (errors.Count > 0) throw new InvalidDataException("Reference table is invalid: " + errors[0].Message);
                return new(table);
            }
            void Add(Entry entry, string file, int row, string column)
            {
                var t = entry.Table;
                hits.Add(new(file, row, column, t.Cell(row, column), t.Records[row]?["sourceId"]?.GetValue<string>(), t.IsCatalog ? t.Cell(row, "id") : null));
            }
            void Find(string target, string key, bool numeric = false)
            {
                token.ThrowIfCancellationRequested();
                int dot = target.IndexOf('.'); if (dot < 1) return;
                string name = target[..dot], requested = target[(dot + 1)..];
                if (name == "strings")
                {
                    var dir = Path.Combine(project.Root, "source", "strings");
                    var files = (Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json") : [])
                        .Concat(buffers?.Where(b => b.Value.IsCatalog).Select(b => b.Key) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
                    if (files.Length == 0) issues.Add("No string catalogs are in this project.");
                    foreach (var file in files) if (Read(Path.GetFileNameWithoutExtension(file), file) is { } entry) Lookup(entry, file, requested, key, false);
                    return;
                }
                // propertygroups is an optional extension; its absence should not slow down ordinary property links with a chooser.
                if (Read(name, optional: name == "propertygroups") is { } e) Lookup(e, Semantics.TableFile(project, name), requested, key, numeric);
            }
            void Lookup(Entry entry, string file, string requested, string key, bool numeric)
            {
                var table = entry.Table;
                if (requested == "[slot]")
                {
                    if (!int.TryParse(key, out int slot) || slot < 0) return;
                    string? id = table.Name.ToLowerInvariant() switch
                    {
                        "monstats" => "hcIdx",
                        "missiles" or "monumod" or "objects" or "levels" => "Id",
                        _ => null
                    };
                    if (id != null && table.Columns.Any(c => c.TrimStart('*').Equals(id, StringComparison.OrdinalIgnoreCase)))
                    { Lookup(entry, file, id, key, true); return; }
                    // Slot references use the exported physical order, including appended custom rows.
                    // Only tables without expansion markers can be mapped without a table-specific compiler.
                    if (Enumerable.Range(0, table.Records.Count).Any(r => table.Cell(r, table.Columns[0]).Equals("Expansion", StringComparison.OrdinalIgnoreCase)))
                    { issues.Add($"{table.Name}: numeric slot needs expansion-marker interpretation."); return; }
                    if (slot < table.Records.Count) Add(entry, file, slot, table.Columns[0]);
                    return;
                }
                var column = table.Columns.FirstOrDefault(c => c == requested) ?? table.Columns.FirstOrDefault(c => c.Equals(requested, StringComparison.OrdinalIgnoreCase));
                // Guide ID spelling predates the comment prefix in newer TXT headers.
                column ??= table.Columns.FirstOrDefault(c => c.TrimStart('*').Equals(requested.TrimStart('*'), StringComparison.OrdinalIgnoreCase));
                if (column == null) { issues.Add($"{table.Name}: reference column {requested} is missing."); return; }
                string Normalize(string text) => numeric && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n.ToString(CultureInfo.InvariantCulture) : text;
                string indexKey = column + (numeric ? "\0numeric" : "");
                if (!entry.Keys.TryGetValue(indexKey, out var index))
                {
                    index = new(StringComparer.Ordinal);
                    for (int r = 0; r < table.Records.Count; r++)
                    {
                        token.ThrowIfCancellationRequested(); var v = Normalize(table.Cell(r, column)); if (v.Length == 0) continue;
                        if (!index.TryGetValue(v, out var rows)) index[v] = rows = []; rows.Add(r);
                    }
                    entry.Keys[indexKey] = index; IndexBuilds++;
                }
                if (index.TryGetValue(Normalize(key), out var matches)) foreach (int row in matches) Add(entry, file, row, column);
            }
            string Source(string column) => source?.FirstOrDefault(p => p.Key.Equals(column, StringComparison.OrdinalIgnoreCase)).Value ?? "";
            void StatParameter(string stat)
            {
                string? target = stat.ToLowerInvariant() switch
                {
                    "state" => "states.state",
                    "attack_vs_montype" or "damage_vs_montype" => "montype.type",
                    "item_singleskill" or "item_nonclassskill" or "item_nonclassskill_display" or "item_charged_skill" => "skills.skill",
                    _ when stat.StartsWith("item_skillon", StringComparison.OrdinalIgnoreCase) => "skills.skill",
                    _ => null
                };
                if (target == null) return;
                if (int.TryParse(value, out _)) target = target == "montype.type" ? "montype.[slot]" : target.Split('.')[0] + ".Id";
                Find(target, value, numeric: int.TryParse(value, out _));
            }
            switch (rule.Kind)
            {
                case "conditional-parameter":
                    if (source == null) { issues.Add("Select a source row to interpret this property parameter."); break; }
                    string companion = Regex.Replace(rule.Column, "param", "code", RegexOptions.IgnoreCase);
                    if (rule.Table == "cubemain") companion = Regex.Replace(rule.Column, " param$", "", RegexOptions.IgnoreCase);
                    if (rule.Table is "uniqueitems" or "setitems" or "monprop") companion = Regex.Replace(rule.Column, "par", "prop", RegexOptions.IgnoreCase);
                    if (rule.Table == "monpet") { StatParameter(Source(rule.Column.Replace("par", "stat"))); break; }
                    string property = Source(companion);
                    if (property.Length == 0) break;
                    if (Read("properties") is not { } props) break;
                    for (int r = 0; r < props.Table.Records.Count; r++)
                    {
                        token.ThrowIfCancellationRequested(); if (props.Table.Cell(r, "code") != property) continue;
                        for (int f = 1; f <= 7; f++)
                        {
                            var func = props.Table.Cell(r, "func" + f);
                            if (func is "9" or "11" or "19") Find(int.TryParse(value, out _) ? "skills.Id" : "skills.skill", value, true);
                            else if (func is "22" or "24") StatParameter(props.Table.Cell(r, "stat" + f));
                            else if (func == "25") Find("properties.Id", value, true);
                        }
                    }
                    if (hits.Count == 0 && issues.Count == 0) issues.Add("This property's parameter has no supported record reference; it may be a number, enum or formula.");
                    break;
                case "conditional-index":
                    var type = Source("gfxtype");
                    if (type is "1" or "2") Find(type == "1" ? "monstats.[slot]" : "playerclass.[slot]", value);
                    else issues.Add("gfxclass references a row only for monster (gfxtype 1) or player (gfxtype 2) graphics.");
                    break;
                case "calculation":
                    foreach (Match operand in CellReferences.Operands.Matches(value))
                        Find(operand.Groups[1].Value.ToLowerInvariant() switch { "skill" => "skills.skill", "stat" => "itemstatcost.Stat", _ => "missiles.Missile" }, operand.Groups[2].Value);
                    break;
                case "expression":
                    if (rule.Table == "skills") { if (int.TryParse(value, out _)) Find("monumod.[slot]", value); else issues.Add("Computed monster modifier: no single destination until evaluated by the game."); break; }
                    if (rule.Table == "monstats2") { foreach (var part in value.Split(',')) Find("compcode.code", part.Trim()); break; }
                    string first = value.Split(',')[0].Trim().Trim('"');
                    if (first is "useitem" or "usetype" or "cow portal" or "Pandemonium Portal" or "Pandemonium Finale")
                    { issues.Add("This recipe token is an operation, not a stored item row."); break; }
                    foreach (var target in rule.Targets) Find(target, first);
                    if (hits.Count == 0) issues.Add("No stored row matches this token; generated treasure classes and recipe operations have no direct row.");
                    break;
                default:
                    foreach (var target in rule.Targets) Find(target, value, rule.Kind == "numeric-key");
                    break;
            }
            return new(hits.Distinct().ToList(), issues.Distinct().ToList());
        }
    }
}
