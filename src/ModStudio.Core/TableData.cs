using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public sealed class TableData
{
    public JsonObject Schema { get; }
    public JsonArray Records { get; }
    public bool IsCatalog => Schema.ContainsKey("category");
    public string[] Columns { get; }
    private readonly Dictionary<string, int> columnIndex;
    public string Name => Schema.S(IsCatalog ? "category" : "name");
    public TableData(JsonObject schema, JsonArray records)
    {
        Schema = schema; Records = records;
        Columns = IsCatalog ? ["id", "Key", .. ((JsonArray)schema["locales"]!).Select(x => x!.GetValue<string>())] : ((JsonArray)schema["columns"]!).Select(x => x.S("key")).ToArray();
        Require(Columns.Distinct(StringComparer.Ordinal).Count() == Columns.Length, "Duplicate column keys.");
        columnIndex = new(StringComparer.Ordinal); for (int i = 0; i < Columns.Length; i++) columnIndex[Columns[i]] = i;
    }
    /// <summary>Position of a column key; -1 when the table has no such column.</summary>
    public int ColumnIndex(string column) => columnIndex.TryGetValue(column, out var index) ? index : -1;
    private static readonly Regex OriginalId = new("^row-([0-9]{5})$");
    /// <summary>Slot number of an imported row (its sourceId is row-NNNNN below protectedRows); -1 for rows added in Studio.</summary>
    public int OriginalSlot(int row)
    {
        if (IsCatalog || row < 0 || row >= Records.Count) return -1;
        var match = OriginalId.Match(Records[row].S("sourceId"));
        return match.Success && int.Parse(match.Groups[1].Value) < Schema.I("protectedRows") ? int.Parse(match.Groups[1].Value) : -1;
    }
    public bool IsOriginalRow(int row) => OriginalSlot(row) >= 0;
    /// <summary>Set by Validate when rows were inserted before imported rows. Advisory only: it matters for tables where the row number is the game ID.</summary>
    public bool RowOrderChanged { get; private set; }
    public const string RowOrderAdvice = "Rows were inserted before original rows. In tables where the row number is the game ID (skills, missiles, uniqueitems, setitems…) this shifts existing IDs; add rows at the bottom instead if that matters.";
    /// <summary>A blank table row with a fresh identity. IDs are random so rows added on different branches never collide; the physical slot is written by the caller.</summary>
    public JsonObject NewRecord(JsonObject? fields = null)
    {
        Require(!IsCatalog, "Adding string entries is not supported yet; add them in Source view.");
        var ordered = new JsonObject();
        foreach (var key in Columns) if (fields?[key] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0) ordered[key] = text;
        return new JsonObject { ["sourceId"] = "row-" + Guid.NewGuid().ToString("N")[..8], ["order"] = 0, ["columnCount"] = Columns.Length, ["fields"] = ordered };
    }
    public string Cell(int row, string column)
    {
        if (row < 0 || row >= Records.Count || Records[row] is not JsonObject r) return "";
        var value = IsCatalog ? column is "id" or "Key" ? r[column] : (r["translations"] as JsonObject)?[column] : (r["fields"] as JsonObject)?[column];
        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString(Compact) ?? "";
    }
    /// <summary>Columns whose values SetCell refuses to change: string identities, and the runtime identity columns of a game table.</summary>
    public bool IsIdentityColumn(string column) => IsCatalog ? column is "id" or "Key" : ((JsonArray?)Schema["identityColumns"] ?? []).Any(x => x!.GetValue<string>() == column);
    public void SetCell(int row, string column, string value)
    {
        Require(columnIndex.ContainsKey(column), $"Unknown column: {column}");
        if (IsCatalog)
        {
            Require(column is not ("id" or "Key"), "String identities are protected; use an explicit source migration.");
            Require(!value.Contains('\0'), "Translations cannot contain NUL.");
            Records[row]!["translations"]![column] = value;
        }
        else
        {
            Require(!IsIdentityColumn(column), $"Identity column is protected: {column}");
            Require(!value.Any(c => c is '\t' or '\r' or '\n'), "A TSV cell cannot contain tabs or newlines.");
            var fields = (JsonObject)Records[row]!["fields"]!;
            if (value.Length == 0) fields.Remove(column); else fields[column] = value;
            Records[row]!["columnCount"] = Math.Max(Records[row].I("columnCount"), columnIndex[column] + 1);
            var ordered = new JsonObject(); foreach (var key in Columns) if (fields.ContainsKey(key)) ordered[key] = fields[key]!.DeepClone();
            Records[row]!["fields"] = ordered;
        }
    }
    public IReadOnlyList<Diagnostic> Validate(string file)
    {
        var errors = new List<Diagnostic>();
        void Error(string message, int row = -1, string field = "") => errors.Add(new(file, message, "Error", row, field));
        try
        {
            Require(Schema.I("schemaVersion") == 1, "Unsupported schema version.");
            Require(Records.Count >= Schema.I("protectedRows"), "Original row slots cannot be removed.");
            var ids = new HashSet<int>(); var keys = new HashSet<string>(StringComparer.Ordinal); var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Records.Count; i++)
            {
                try { ValidateRow(i, ids, keys, sourceIds); }
                catch (Exception e) { Error(e.Message, i); }
            }
            if (!IsCatalog && errors.Count == 0)
            {
                var identityColumns = ((JsonArray?)Schema["identityColumns"] ?? []).Select(x => x!.GetValue<string>()).ToArray();
                // Every imported row must still exist; the identity hash covers them in their original numbering regardless of where rows were inserted.
                var originals = new JsonNode?[Schema.I("protectedRows")]; bool moved = false;
                for (int i = 0; i < Records.Count; i++) { var slot = OriginalSlot(i); if (slot < 0) continue; originals[slot] = Records[i]; moved |= slot != i; }
                var missing = Enumerable.Range(0, originals.Length).Where(i => originals[i] == null).ToArray();
                if (missing.Length > 0) throw new InvalidDataException($"Original rows were removed: row-{missing[0]:D5}{(missing.Length > 1 ? $" and {missing.Length - 1} more" : "")}. Only rows added in Studio can be deleted.");
                if (Schema.ContainsKey("identitySha256")) Require(IdentityHash(originals, identityColumns) == Schema.S("identitySha256"), "Protected runtime identities changed.");
                RowOrderChanged = moved;
                if (identityColumns.Length > 0)
                {
                    var groups = Enumerable.Range(0, Records.Count).Where(i => identityColumns.Any(k => Cell(i, k).Length > 0)).GroupBy(i => string.Join('\0', identityColumns.Select(k => Cell(i, k))));
                    foreach (var group in groups.Where(g => g.Count() > 1))
                    {
                        var first = group.First();
                        var tuple = new JsonArray(identityColumns.Select(k => (JsonNode?)JsonValue.Create(Cell(first, k))).ToArray());
                        var expected = identityColumns.Length == 1 ? Schema["legacyDuplicateIdentities"]?[identityColumns[0]]?[Cell(first, identityColumns[0])] : Schema["legacyDuplicateIdentityTuples"]?[tuple.ToJsonString(Compact)];
                        var actual = new JsonArray(group.Select(i => (JsonNode?)JsonValue.Create(Records[i].S("sourceId"))).ToArray());
                        Require(JsonNode.DeepEquals(expected, actual), "New duplicate runtime identity.");
                    }
                }
            }
        }
        catch (Exception e) { Error(e.Message); }
        return errors;
    }
    /// <summary>
    /// SHA-256 of the compact JSON array of [sourceId, identity values…] per original row, in original numbering. Written
    /// straight through Utf8JsonWriter: it is byte-identical to JsonArray.ToJsonString(Compact), which the import used, without
    /// allocating a JsonNode per row.
    /// </summary>
    private static string IdentityHash(JsonNode?[] originals, string[] identityColumns)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer, new() { Encoder = Compact.Encoder }))
        {
            writer.WriteStartArray();
            foreach (var r in originals)
            {
                writer.WriteStartArray(); writer.WriteStringValue(r.S("sourceId"));
                foreach (var k in identityColumns) writer.WriteStringValue(r!["fields"].S(k));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Hash(buffer.WrittenSpan.ToArray());
    }
    /// <summary>
    /// Checks the rows just edited, for a document whose last full validation passed. Cell edits cannot change identities,
    /// slots or the row set (SetCell protects them), so the table-level checks and the other rows' results still hold.
    /// </summary>
    public IReadOnlyList<Diagnostic> ValidateRows(string file, IEnumerable<int> rows)
    {
        var errors = new List<Diagnostic>();
        foreach (var i in rows)
        {
            try { Require(i >= 0 && i < Records.Count, "Invalid row."); ValidateRow(i, null, null, null); }
            catch (Exception e) { errors.Add(new(file, e.Message, "Error", i)); }
        }
        return errors;
    }
    /// <summary>One record's checks. Uniqueness sets are null when a single row is rechecked after a cell edit; identities cannot change then.</summary>
    private void ValidateRow(int i, HashSet<int>? ids, HashSet<string>? keys, HashSet<string>? sourceIds)
    {
        var r = Records[i] ?? throw new InvalidDataException("Null record.");
        Require(r.I("order", -1) == i, "Record order must match its physical slot. Do not sort source rows.");
        if (IsCatalog)
        {
            Require(r.I("id", -1) is >= 0 and <= 65535 && (ids?.Add(r.I("id")) ?? true), "Invalid or duplicate string ID.");
            Require(r.S("Key").Length > 0 && (keys?.Add(r.S("Key")) ?? true), "Missing or duplicate string key.");
            for (int c = 2; c < Columns.Length; c++)
            {
                var locale = Columns[c];
                var full = r["translations"]?[locale]?.GetValue<string>() ?? throw new InvalidDataException($"Missing locale {locale}");
                Require(!full.Contains('\0'), "NUL in translation.");
                if (r["standardTranslations"]?[locale] is JsonNode compact)
                {
                    Require(r["standardReviewedAgainst"].S(locale) == Hash(full), $"Compact {locale} requires review.");
                    Require(Placeholders(full).SequenceEqual(Placeholders(compact.GetValue<string>())), $"Compact {locale} changes placeholders.");
                }
            }
            foreach (var key in new[] { "translations", "standardTranslations", "standardReviewedAgainst" })
                if (r[key] is JsonObject o) foreach (var pair in o) Require(columnIndex.TryGetValue(pair.Key, out var c) && c >= 2, $"Unknown locale {pair.Key}");
        }
        else
        {
            // Source IDs are stable identities (profile overrides refer to them), not slots: rows can be inserted without renumbering.
            Require(r.S("sourceId").Length > 0 && (sourceIds?.Add(r.S("sourceId")) ?? true), "Missing or duplicate source ID.");
            var width = r.I("columnCount"); Require(width > 0 && width <= Columns.Length, "Invalid row width.");
            foreach (var pair in r["fields"]!.AsObject())
            {
                Require(columnIndex.TryGetValue(pair.Key, out var c) && c < width, $"Unknown/out-of-width column {pair.Key}.");
                Require(pair.Value is JsonValue v && v.TryGetValue<string>(out var text) && !text.Any(ch => ch is '\t' or '\r' or '\n'), $"Invalid cell {pair.Key}; values must be strings.");
            }
        }
    }
    public static string[] Placeholders(string text) => Regex.Matches(text, @"%%|%(?:\d+\$)?[-+#0 ]*(?:\d+|\*)?(?:\.(?:\d+|\*))?[hlL]*[diuoxXfFeEgGaAcspn]").Select(m => m.Value).Where(v => v != "%%").ToArray();
    public byte[] EncodeTsv()
    {
        var newline = Schema.S("newline", "\n"); Require(newline is "\n" or "\r\n" or "\r", "Invalid newline.");
        var lines = new List<string> { string.Join('\t', ((JsonArray)Schema["columns"]!).Select(c => c.S("header"))) };
        for (int i = 0; i < Records.Count; i++) lines.Add(string.Join('\t', Columns.Take(Records[i].I("columnCount")).Select(k => Cell(i, k))));
        return Utf8.GetBytes((Schema.B("bom") ? "\uFEFF" : "") + string.Join(newline, lines) + (Schema.B("finalNewline") ? newline : ""));
    }
    public byte[] EncodeCatalog(bool standard)
    {
        var rows = new JsonArray();
        foreach (var r in Records)
        {
            var output = new JsonObject { ["id"] = r!["id"]!.DeepClone(), ["Key"] = r["Key"]!.DeepClone() };
            foreach (var locale in Columns.Skip(2)) output[locale] = (standard ? r["standardTranslations"]?[locale] : null)?.DeepClone() ?? r["translations"]![locale]!.DeepClone();
            rows.Add(output);
        }
        var options = new System.Text.Json.JsonSerializerOptions(Pretty) { IndentSize = Schema.I("indent", 4) };
        return Utf8.GetBytes((Schema.B("bom") ? "\uFEFF" : "") + rows.ToJsonString(options).Replace("\n", Schema.S("newline", "\n")) + (Schema.B("finalNewline") ? Schema.S("newline", "\n") : ""));
    }
    public static TableData FromTsv(byte[] bytes, string name, string target)
    {
        var text = Utf8.GetString(bytes); bool bom = text.StartsWith('\uFEFF'); if (bom) text = text[1..];
        var endings = Regex.Matches(text, "\r\n|\r|\n").Select(m => m.Value).Distinct().ToArray(); Require(endings.Length <= 1, "Mixed line endings require explicit normalization.");
        var newline = endings.FirstOrDefault() ?? "\n"; bool final = text.EndsWith(newline);
        var lines = text.Split(newline).ToList(); if (final) lines.RemoveAt(lines.Count - 1);
        var headers = lines[0].Split('\t'); var keys = new HashSet<string>(); var cols = new JsonArray();
        for (int i = 0; i < headers.Length; i++) { var key = headers[i].Length > 0 ? headers[i] : $"column-{i + 1}"; while (!keys.Add(key)) key += $"#{i + 1}"; cols.Add(new JsonObject { ["key"] = key, ["header"] = headers[i] }); }
        var schema = new JsonObject { ["schemaVersion"] = 1, ["name"] = name, ["targets"] = new JsonArray(target), ["identityColumns"] = new JsonArray(), ["columns"] = cols, ["bom"] = bom, ["newline"] = newline, ["finalNewline"] = final };
        var records = new JsonArray();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split('\t'); Require(cells.Length <= cols.Count, $"Row {records.Count}: more cells than headers."); var fields = new JsonObject();
            for (int i = 0; i < cells.Length; i++) if (cells[i].Length > 0) fields[cols[i].S("key")] = cells[i];
            records.Add(new JsonObject { ["sourceId"] = $"row-{records.Count:D5}", ["order"] = records.Count, ["columnCount"] = cells.Length, ["fields"] = fields });
        }
        schema["protectedRows"] = records.Count;
        return new(schema, records);
    }
    public static TableData Load(string file) => new((JsonObject)Read(Path.Combine(Path.GetDirectoryName(file)!, "schema.json")), (JsonArray)Read(file));
}
