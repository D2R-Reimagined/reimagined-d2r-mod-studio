using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public sealed class Document
{
    private string diskHash;
    private string? schemaHash;
    private string raw;
    private readonly System.Text.Encoding fileEncoding;
    private readonly byte[] filePreamble;
    private readonly bool rawMode;
    private bool modelChanged;
    private readonly Stack<Action> undo = new();
    private readonly Stack<Action> redo = new();
    private bool replaying;
    private bool historyPlayback;
    private long state, savedState, nextState;
    public string FilePath { get; }
    public TableData? Table { get; private set; }
    public bool PendingSource { get; private set; }
    public bool IsDirty { get; private set; }
    public int Revision { get; private set; }
    public List<Diagnostic> Diagnostics { get; private set; } = [];
    public event Action? Changed;
    public HashSet<int> LockedRows { get; } = [];
    public HashSet<string> LockedColumns { get; } = new(StringComparer.Ordinal);
    public bool HasEditLocks => LockedRows.Count > 0 || LockedColumns.Count > 0;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool ExternalChange => !File.Exists(FilePath) || Hash(File.ReadAllBytes(FilePath)) != diskHash || (schemaHash != null && (!File.Exists(SchemaFile) || Hash(File.ReadAllBytes(SchemaFile)) != schemaHash));
    private string SchemaFile => Path.Combine(Path.GetDirectoryName(FilePath)!, "schema.json");
    public Document(string file, bool forceRaw = false)
    {
        FilePath = Path.GetFullPath(file); NoLinks(file); var bytes = File.ReadAllBytes(file); diskHash = Hash(bytes);
        rawMode = forceRaw;
        var detected = forceRaw ? (Encoding: System.Text.Encoding.Latin1, Preamble: 0) : TextFileEncoding.Detect(bytes); fileEncoding = detected.Encoding; filePreamble = bytes[..detected.Preamble];
        raw = fileEncoding.GetString(bytes, detected.Preamble, bytes.Length - detected.Preamble);
        if (Path.GetFileName(file) == "records.json" && File.Exists(SchemaFile)) schemaHash = Hash(File.ReadAllBytes(SchemaFile));
        Parse();
    }
    public string Text
    {
        get { if (modelChanged && Table != null) { raw = IsTsv ? Utf8.GetString(Table.EncodeTsv()) : Json(Table.Records); modelChanged = false; } return raw; }
    }
    public bool IsTsv => FilePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && Table != null;
    private void Parse()
    {
        Diagnostics = []; Table = null;
        if (rawMode) { PendingSource = false; return; }
        try
        {
            if (schemaHash != null) Table = new((JsonObject)Read(SchemaFile), (JsonArray)(JsonNode.Parse(raw.TrimStart('\uFEFF')) ?? throw new InvalidDataException("Empty JSON.")));
            else if (FilePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && raw.Contains('\t')) Table = TableData.FromTsv(Utf8.GetBytes(raw), Path.GetFileNameWithoutExtension(FilePath), "global/excel/" + Path.GetFileName(FilePath));
            else if (FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) JsonNode.Parse(raw.TrimStart('\uFEFF'));
            if (Table != null) Diagnostics.AddRange(Validated());
            PendingSource = false;
        }
        catch (Exception e) { Diagnostics.Add(new(FilePath, e.Message)); PendingSource = true; }
    }
    private void Notify() { Revision++; IsDirty = state != savedState; Changed?.Invoke(); }
    private void Record(Action reverse)
    {
        var previous = state; state = ++nextState;
        (replaying ? redo : undo).Push(() => { reverse(); state = previous; IsDirty = state != savedState; Changed?.Invoke(); });
        if (!replaying) redo.Clear();
    }
    public void SetRaw(string text)
    {
        var before = Text; if (text == before) return;
        Require(!HasEditLocks || historyPlayback, "Unlock protected rows/columns before editing raw source.");
        int start = 0; while (start < before.Length && start < text.Length && before[start] == text[start]) start++;
        int suffix = 0; while (suffix < before.Length - start && suffix < text.Length - start && before[^(suffix + 1)] == text[^(suffix + 1)]) suffix++;
        var removed = before.Substring(start, before.Length - start - suffix); var inserted = text.Substring(start, text.Length - start - suffix);
        Record(() => SetRaw(Text.Remove(start, inserted.Length).Insert(start, removed)));
        raw = text; modelChanged = false; PendingSource = true; Diagnostics = [new(FilePath, "Source changed; apply or validate before table editing.", "Info")]; Notify();
    }
    public void ApplySource() { if (modelChanged) _ = Text; Parse(); Changed?.Invoke(); }
    public void SetCells(IEnumerable<(int Row, string Column, string Value)> changes)
    {
        Require(Table != null && !PendingSource, "Apply valid source before editing cells.");
        var edits = changes.Where(e => Table!.Cell(e.Row, e.Column) != e.Value).ToArray(); if (edits.Length == 0) return;
        Require(edits.All(e => !LockedRows.Contains(e.Row) && !LockedColumns.Contains(e.Column)), "A selected row or column is locked against edits.");
        // Validate every cell before mutating the live document.
        var affected = edits.Select(e => e.Row).Distinct().ToDictionary(i => i, i => Table!.Records[i]!.DeepClone());
        try { foreach (var e in edits) Table!.SetCell(e.Row, e.Column, e.Value); }
        catch { foreach (var p in affected) Table!.Records[p.Key] = p.Value; throw; }
        Record(() => RestoreRows(affected)); modelChanged = true; Diagnostics = Validated(); Notify();
    }
    private List<Diagnostic> Validated()
    {
        var diagnostics = Table!.Validate(FilePath).ToList();
        if (Table!.RowOrderChanged) diagnostics.Add(new(FilePath, TableData.RowOrderAdvice, "Warning"));
        return diagnostics;
    }
    /// <summary>Inserts blank rows (optionally pre-filled) at a physical slot. Later rows keep their identities; only their slot numbers move.</summary>
    public void InsertRows(int index, int count = 1, IReadOnlyList<JsonObject?>? fields = null)
    {
        Require(Table != null && !PendingSource, "Apply valid source before adding rows.");
        Require(index >= 0 && index <= Table!.Records.Count && count > 0, "Invalid row position.");
        Require(!LockedRows.Contains(index) || index == Table!.Records.Count, "The row below the insertion point is locked against edits.");
        var rows = Enumerable.Range(0, count).Select(i => (JsonNode)Table!.NewRecord(fields != null && i < fields.Count ? fields[i] : null)).ToArray();
        Splice(index, 0, rows);
    }
    /// <summary>Deletes rows added in Studio. Imported rows are protected: the game data contract keeps their slots.</summary>
    public void DeleteRows(IEnumerable<int> rows)
    {
        Require(Table != null && !PendingSource, "Apply valid source before deleting rows.");
        var targets = rows.Distinct().OrderByDescending(r => r).ToArray(); if (targets.Length == 0) return;
        Require(targets.All(r => r >= 0 && r < Table!.Records.Count), "Invalid row.");
        Require(targets.All(r => !Table!.IsOriginalRow(r)), "Original imported rows cannot be deleted; clear their cells instead.");
        Require(targets.All(r => !LockedRows.Contains(r)), "A selected row is locked against edits.");
        RemoveRows(targets);
    }
    private void RemoveRows(int[] descendingRows)
    {
        var removed = descendingRows.Select(r => (Row: r, Node: Table!.Records[r]!.DeepClone())).ToArray();
        foreach (var r in descendingRows) Table!.Records.RemoveAt(r);
        Renumber(); LockedRows.Clear();
        Record(() => Reinsert(removed)); Finish();
    }
    private void Reinsert((int Row, JsonNode Node)[] removed)
    {
        foreach (var (row, node) in removed.Reverse()) Table!.Records.Insert(row, node.DeepClone());
        Renumber();
        Record(() => RemoveRows(removed.Select(x => x.Row).ToArray())); Finish();
    }
    private void Splice(int index, int remove, JsonNode[] insert)
    {
        var removed = Enumerable.Range(index, remove).Select(i => Table!.Records[i]!.DeepClone()).ToArray();
        for (int i = 0; i < remove; i++) Table!.Records.RemoveAt(index);
        for (int i = 0; i < insert.Length; i++) Table!.Records.Insert(index + i, insert[i]);
        Renumber();
        var shifted = LockedRows.Where(r => r >= index).ToArray(); LockedRows.ExceptWith(shifted); LockedRows.UnionWith(shifted.Select(r => r + insert.Length - remove).Where(r => r >= index + insert.Length));
        Record(() => Splice(index, insert.Length, removed));
        Finish();
    }
    private void Renumber() { for (int i = 0; i < Table!.Records.Count; i++) Table.Records[i]!["order"] = i; }
    private void Finish() { modelChanged = true; Diagnostics = Validated(); Notify(); }
    private void RestoreRows(Dictionary<int, JsonNode> rows)
    {
        Require(Table != null && !PendingSource, "Apply source before undoing table changes.");
        var reverse = rows.Keys.ToDictionary(i => i, i => Table!.Records[i]!.DeepClone());
        foreach (var p in rows) Table!.Records[p.Key] = p.Value.DeepClone(); Record(() => RestoreRows(reverse)); modelChanged = true; Diagnostics = Validated(); Notify();
    }
    public void Undo()
    {
        if (!undo.TryPop(out var action)) return; replaying = true; historyPlayback = true;
        try { action(); } finally { replaying = false; historyPlayback = false; } if (PendingSource) ApplySource();
    }
    public void Redo()
    {
        if (!redo.TryPop(out var action)) return;
        var remaining = redo.ToArray(); replaying = false; historyPlayback = true;
        try { action(); } finally { historyPlayback = false; }
        redo.Clear(); foreach (var a in remaining.Reverse()) redo.Push(a);
        if (PendingSource) ApplySource();
    }
    public void Save()
    {
        if (PendingSource) ApplySource(); Require(Diagnostics.All(d => d.Severity != "Error"), "Fix document errors before saving. Recovery retains invalid source.");
        Require(!ExternalChange, "File or schema changed externally. Reload before saving; your edits are preserved in recovery.");
        var encoder = (System.Text.Encoding)fileEncoding.Clone(); encoder.EncoderFallback = System.Text.EncoderFallback.ExceptionFallback;
        var bytes = filePreamble.Concat(encoder.GetBytes(Text)).ToArray(); AtomicWrite(FilePath, bytes, diskHash); diskHash = Hash(bytes); savedState = state; IsDirty = false; Changed?.Invoke();
    }
    public void Recover(string recoveryFile)
    {
        WriteJson(recoveryFile, new JsonObject { ["schemaVersion"] = 1, ["file"] = FilePath, ["diskHash"] = diskHash, ["text"] = Text });
    }
    public void RestoreRecovery(string recoveryFile)
    {
        var recovery = Read(recoveryFile); Require(recovery.S("file") == FilePath && recovery.S("diskHash") == diskHash, "Recovery belongs to an older disk version. Open it as text to reconcile manually."); SetRaw(recovery.S("text")); ApplySource();
    }
}
