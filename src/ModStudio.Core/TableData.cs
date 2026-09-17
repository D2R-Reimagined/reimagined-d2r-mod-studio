using System.Text;
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
    /// <summary>Spreadsheet letter of a physical column: 0 → A, 25 → Z, 26 → AA. It follows the TXT column order, not the order columns are shown in.</summary>
    public static string ColumnLetter(int index)
    {
        var letters = ""; for (int n = index; n >= 0; n = n / 26 - 1) letters = (char)('A' + n % 26) + letters;
        return letters;
    }
    private static readonly Regex OriginalId = new("^row-([0-9]{5})$");
    /// <summary>Slot number of an imported row (its sourceId is row-NNNNN below protectedRows); -1 for rows added in Studio.</summary>
    public int OriginalSlot(int row)
    {
        if (IsCatalog || row < 0 || row >= Records.Count) return -1;
        var match = OriginalId.Match(Records[row].S("sourceId"));
        return match.Success && int.Parse(match.Groups[1].Value) < Schema.I("protectedRows") ? int.Parse(match.Groups[1].Value) : -1;
    }
    /// <summary>Imported rows: table rows with a slot identity, and string entries without a Studio sourceId. Their identities and slots are protected.</summary>
    public bool IsOriginalRow(int row) => IsCatalog ? row >= 0 && row < Records.Count && Records[row].S("sourceId").Length == 0 : OriginalSlot(row) >= 0;
    /// <summary>Set by Validate when rows were inserted before imported rows. Advisory only: it matters for tables where the row number is the game ID.</summary>
    public bool RowOrderChanged { get; private set; }
    /// <summary>Recomputes the row-order advisory alone, for changes that cannot touch any other table-level check.</summary>
    public void RefreshRowOrder()
    {
        bool moved = false;
        for (int i = 0; i < Records.Count && !moved; i++) { var slot = OriginalSlot(i); moved = slot >= 0 && slot != i; }
        RowOrderChanged = moved;
    }
    public const string RowOrderAdvice = "Rows were inserted before original rows. In tables where the row number is the game ID (skills, missiles, uniqueitems, setitems…) this shifts existing IDs; add rows at the bottom instead if that matters.";
    /// <summary>A blank table row with a fresh identity. IDs are random so rows added on different branches never collide; the physical slot is written by the caller.</summary>
    public JsonObject NewRecord(JsonObject? fields = null)
    {
        if (IsCatalog)
        {
            // A new string entry gets the next free ID and an empty key/translations; Studio-added entries carry a sourceId so their id and Key stay editable.
            var translations = new JsonObject(); foreach (var locale in Columns.Skip(2)) translations[locale] = fields?[locale] is JsonValue t && t.TryGetValue<string>(out var text) ? text : "";
            int nextId = Records.Count == 0 ? 0 : Records.Max(r => r.I("id", -1)) + 1;
            return new JsonObject { ["sourceId"] = "str-" + Guid.NewGuid().ToString("N")[..8], ["order"] = 0, ["id"] = nextId, ["Key"] = fields.S("Key"), ["translations"] = translations };
        }
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
            Require(!value.Contains('\0'), "Translations cannot contain NUL.");
            if (column is "id" or "Key")
            {
                Require(!IsOriginalRow(row), "String identities are protected; use an explicit source migration.");
                if (column == "id") { Require(int.TryParse(value, out var id) && id >= 0, "String IDs are non-negative integers."); Records[row]!["id"] = id; }
                else Records[row]!["Key"] = value;
            }
            else
            {
                Records[row]!["translations"]![column] = value;
                if (Records[row]!["nullTranslations"] is JsonArray nulls)
                {
                    for (int i = nulls.Count - 1; i >= 0; i--) if (nulls[i]?.GetValue<string>() == column) nulls.RemoveAt(i);
                    if (nulls.Count == 0) Records[row]!.AsObject().Remove("nullTranslations");
                }
            }
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
            // Imported catalogs can contain repeated IDs. Reserve all of their IDs up front so a new Studio entry
            // cannot reuse one, even when it was inserted before the imported entries.
            var ids = IsCatalog ? Records.Where((_, i) => IsOriginalRow(i)).Select(r => r.I("id", -1)).ToHashSet() : new HashSet<int>();
            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Records.Count; i++)
            {
                try { ValidateRow(i, ids, sourceIds); }
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
    /// <summary>Repeated IDs already present in an imported catalog are preserved, but should be reviewed.</summary>
    public IReadOnlyList<Diagnostic> DuplicateIdWarnings(string file)
    {
        if (!IsCatalog) return [];
        var warnings = new List<Diagnostic>();
        var groups = Enumerable.Range(0, Records.Count)
            .Where(i => Records[i].I("id", -1) >= 0)
            .GroupBy(i => Records[i].I("id"))
            .Where(group => group.Count() > 1 && group.All(IsOriginalRow));
        foreach (var group in groups)
        {
            var rows = group.ToArray();
            foreach (var row in rows)
            {
                var peer = rows.First(i => i != row);
                warnings.Add(new(file, $"String ID {group.Key} is shared by {rows.Length} entries, including row {peer} ({Records[peer].S("Key")}). Review these entries; edit an imported ID in Source view if the duplication is unintended.", "Warning", row, "id"));
            }
        }
        return warnings;
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
            try { Require(i >= 0 && i < Records.Count, "Invalid row."); ValidateRow(i, null, null); }
            catch (Exception e) { errors.Add(new(file, e.Message, "Error", i)); }
        }
        return errors;
    }
    /// <summary>One record's checks. Uniqueness sets are null when a single row is rechecked after a cell edit; identities cannot change then.</summary>
    private void ValidateRow(int i, HashSet<int>? ids, HashSet<string>? sourceIds)
    {
        var r = Records[i] ?? throw new InvalidDataException("Null record.");
        if (IsCatalog)
        {
            // Existing catalog IDs are imported identities. New Studio entries need IDs unused by those entries and each other.
            Require(r.I("id", -1) >= 0 && (IsOriginalRow(i) || (ids?.Add(r.I("id")) ?? true)), "Invalid or duplicate string ID.");
            Require(r.S("Key").Length > 0, "Missing string key.");
            Require(r["translations"] is null or JsonObject, "Invalid translations block.");
            var translations = r["translations"] as JsonObject;
            for (int c = 2; c < Columns.Length; c++)
            {
                var locale = Columns[c];
                var translation = translations?[locale];
                if (translations?.ContainsKey(locale) == true)
                    Require(translation is JsonValue value && value.TryGetValue<string>(out _), $"Invalid translation {locale}; values must be strings.");
                var full = translation?.GetValue<string>() ?? "";
                Require(!full.Contains('\0'), "NUL in translation.");
                if (r["standardTranslations"]?[locale] is JsonNode compact)
                {
                    Require(r["standardReviewedAgainst"].S(locale) == Hash(full), $"Compact {locale} requires review.");
                    Require(Placeholders(full).SequenceEqual(Placeholders(compact.GetValue<string>())), $"Compact {locale} changes placeholders.");
                }
            }
            foreach (var key in new[] { "translations", "standardTranslations", "standardReviewedAgainst" })
                if (r[key] is JsonObject o) foreach (var pair in o) Require(columnIndex.TryGetValue(pair.Key, out var c) && c >= 2, $"Unknown locale {pair.Key}");
            if (r["nullTranslations"] is JsonArray nulls)
                foreach (var locale in nulls.Select(x => x?.GetValue<string>()))
                    Require(locale != null && columnIndex.TryGetValue(locale, out var c) && c >= 2 && translations?.ContainsKey(locale) != true, $"Invalid null translation {locale}");
            else Require(r["nullTranslations"] is null, "Invalid null translations block.");
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
    private static Encoding TsvEncoding(JsonObject schema)
    {
        var encoding = schema.S("encoding", "utf-8");
        Encoding value = encoding switch
        {
            "utf-8" => (Encoding)Utf8.Clone(),
            "utf-16-le" => new UnicodeEncoding(false, false, true),
            "utf-16-be" => new UnicodeEncoding(true, false, true),
            "latin1" => (Encoding)Encoding.Latin1.Clone(),
            _ => throw new InvalidDataException($"Unsupported table encoding: {encoding}")
        };
        value.EncoderFallback = EncoderFallback.ExceptionFallback;
        value.DecoderFallback = DecoderFallback.ExceptionFallback;
        return value;
    }
    public byte[] EncodeTsv()
    {
        var newline = Schema.S("newline", "\n"); Require(newline is "\n" or "\r\n" or "\r", "Invalid newline.");
        var lines = new List<string> { string.Join('\t', ((JsonArray)Schema["columns"]!).Select(c => c.S("header"))) };
        for (int i = 0; i < Records.Count; i++) lines.Add(string.Join('\t', Columns.Take(Records[i].I("columnCount")).Select(k => Cell(i, k))));
        try { return TsvEncoding(Schema).GetBytes((Schema.B("bom") ? "\uFEFF" : "") + string.Join(newline, lines) + (Schema.B("finalNewline") ? newline : "")); }
        catch (EncoderFallbackException e) { throw new InvalidDataException($"Table text cannot be represented in its {Schema.S("encoding", "utf-8")} encoding.", e); }
    }
    public byte[] EncodeCatalog(bool standard)
    {
        var rows = new JsonArray();
        foreach (var r in Records)
        {
            var output = new JsonObject { ["id"] = r!["id"]!.DeepClone(), ["Key"] = r["Key"]!.DeepClone() };
            foreach (var locale in Columns.Skip(2))
            {
                var explicitNull = r["nullTranslations"] is JsonArray nulls && nulls.Any(x => x?.GetValue<string>() == locale);
                var compact = standard ? r["standardTranslations"]?[locale] : null;
                output[locale] = compact?.DeepClone() ?? (explicitNull ? null : r["translations"]?[locale]?.DeepClone() ?? JsonValue.Create(""));
            }
            rows.Add(output);
        }
        var options = new System.Text.Json.JsonSerializerOptions(Pretty) { IndentSize = Schema.I("indent", 4) };
        return Utf8.GetBytes((Schema.B("bom") ? "\uFEFF" : "") + rows.ToJsonString(options).Replace("\n", Schema.S("newline", "\n")) + (Schema.B("finalNewline") ? Schema.S("newline", "\n") : ""));
    }
    public static TableData FromTsv(byte[] bytes, string name, string target)
    {
        Require(TextFileEncoding.LooksLikeText(bytes), "Table is not valid text.");
        var (encoding, preamble) = TextFileEncoding.Detect(bytes);
        var text = encoding.GetString(bytes, preamble, bytes.Length - preamble); bool bom = preamble > 0 || text.StartsWith('\uFEFF'); if (text.StartsWith('\uFEFF')) text = text[1..];
        var endings = Regex.Matches(text, "\r\n|\r|\n").Select(m => m.Value).Distinct().ToArray(); Require(endings.Length <= 1, "Mixed line endings require explicit normalization.");
        var newline = endings.FirstOrDefault() ?? "\n"; bool final = text.EndsWith(newline);
        var lines = text.Split(newline).ToList(); if (final) lines.RemoveAt(lines.Count - 1);
        var headers = lines[0].Split('\t'); var keys = new HashSet<string>(); var cols = new JsonArray();
        for (int i = 0; i < headers.Length; i++) { var key = headers[i].Length > 0 ? headers[i] : $"column-{i + 1}"; while (!keys.Add(key)) key += $"#{i + 1}"; cols.Add(new JsonObject { ["key"] = key, ["header"] = headers[i] }); }
        var encodingName = encoding.CodePage switch { 1200 => "utf-16-le", 1201 => "utf-16-be", 28591 => "latin1", _ => "utf-8" };
        var schema = new JsonObject { ["schemaVersion"] = 1, ["name"] = name, ["targets"] = new JsonArray(target), ["identityColumns"] = new JsonArray(), ["columns"] = cols, ["bom"] = bom, ["newline"] = newline, ["finalNewline"] = final };
        if (encodingName != "utf-8") schema["encoding"] = encodingName;
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
    /// <summary>An empty table whose columns come from the data guide. Rows added later are not protected, since nothing here came from the game.</summary>
    public static TableData FromGuide(ColumnGuideFile guide, string name, string? target = null)
    {
        var cols = new JsonArray(); foreach (var header in guide.Headers()) cols.Add(new JsonObject { ["key"] = header, ["header"] = header });
        var schema = new JsonObject { ["schemaVersion"] = 1, ["name"] = name, ["targets"] = new JsonArray(target ?? guide.Target), ["identityColumns"] = new JsonArray(), ["columns"] = cols, ["bom"] = false, ["newline"] = "\r\n", ["finalNewline"] = true, ["protectedRows"] = 0 };
        return new(schema, new JsonArray());
    }
    /// <summary>Table name derived from a native file name the way import does: lower-case stem, unsupported characters replaced by hyphens.</summary>
    public static string NameFor(string file) => Regex.Replace(Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), "[^a-z0-9-]", "-");
    /// <summary>Writes a new source table into a project. Fails if a table with that name already exists.</summary>
    public static string Create(ModProject project, TableData table)
    {
        var name = table.Schema.S("name"); Require(Regex.IsMatch(name, "^[a-z0-9-]{1,80}$"), "Table names use lower-case letters, digits and hyphens.");
        var file = FileFor(project, "tables", name); Require(!File.Exists(file) && !Directory.Exists(Path.ChangeExtension(file, null)), $"A table named {name} already exists.");
        Write(file, table); return file;
    }
    /// <summary>Source file of a table (kind "tables") or string catalog (kind "strings"): schema and records live together in one JSON document.</summary>
    public static string FileFor(ModProject project, string kind, string name) => Inside(project.Root, $"source/{kind}/{name}.json");
    public static bool IsTableFile(JsonNode? node) => node is JsonObject o && o["schema"] is JsonObject && o["records"] is JsonArray;
    /// <summary>The document form; the caller owns the copies, so an in-memory table stays attached to its own file root.</summary>
    public JsonObject ToFile() => new() { ["schema"] = Schema.DeepClone(), ["records"] = Records.DeepClone() };
    public static void Write(string file, TableData table) => WriteJson(file, table.ToFile());
    /// <summary>
    /// Loads a table file. Array position is the physical slot; "order" is only a convenience for readers and is rewritten to
    /// match, so entries inserted or removed by hand in Source view (or an external editor) load cleanly. Returns whether any
    /// slot number was corrected, so the caller can treat the model as changed.
    /// </summary>
    public static TableData FromFile(JsonNode node, string file, out bool renumbered)
    {
        Require(IsTableFile(node), "Not a table file: " + file);
        var records = (JsonArray)node["records"]!; renumbered = false;
        for (int i = 0; i < records.Count; i++)
            if (records[i] is JsonObject r && r.I("order", -1) != i) { r["order"] = i; renumbered = true; }
        return new((JsonObject)node["schema"]!, records);
    }
    public static TableData FromFile(JsonNode node, string file) => FromFile(node, file, out _);
    public static TableData Load(string file) => FromFile(Read(file), file);
}
