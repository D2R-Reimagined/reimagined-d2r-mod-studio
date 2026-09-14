using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public sealed class Document
{
    private string diskHash;
    private string raw;
    /// <summary>The parsed file of a JSON table document: schema and records are edited in place and serialized back through it.</summary>
    private JsonObject? tableRoot;
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
    /// <summary>Rows touched by the latest change when it only replaced cell values; null after a structural or raw-text change, when views must rebuild.</summary>
    public IReadOnlyCollection<int>? LastChangedRows { get; private set; }
    /// <summary>Columns touched by the latest cell-value change (see LastChangedRows).</summary>
    public IReadOnlyCollection<string>? LastChangedColumns { get; private set; }
    public bool ExternalChange => !File.Exists(FilePath) || Hash(File.ReadAllBytes(FilePath)) != diskHash;
    public Document(string file, bool forceRaw = false)
    {
        FilePath = Path.GetFullPath(file); NoLinks(file); var bytes = File.ReadAllBytes(file); diskHash = Hash(bytes);
        rawMode = forceRaw;
        var detected = forceRaw ? (Encoding: System.Text.Encoding.Latin1, Preamble: 0) : TextFileEncoding.Detect(bytes); fileEncoding = detected.Encoding; filePreamble = bytes[..detected.Preamble];
        raw = fileEncoding.GetString(bytes, detected.Preamble, bytes.Length - detected.Preamble);
        Parse();
    }
    public string Text
    {
        get { if (modelChanged && Table != null) { raw = IsTsv ? Utf8.GetString(Table.EncodeTsv()) : Json(tableRoot!); modelChanged = false; } return raw; }
    }
    public bool IsTsv => FilePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && Table != null;
    public static System.Text.Json.JsonDocumentOptions SourceJsonOptions => new() { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip };
    private void Parse()
    {
        Diagnostics = []; Table = null; tableRoot = null;
        if (rawMode) { PendingSource = false; return; }
        try
        {
            if (FilePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && raw.Contains('\t')) Table = TableData.FromTsv(Utf8.GetBytes(raw), Path.GetFileNameWithoutExtension(FilePath), "global/excel/" + Path.GetFileName(FilePath));
            else if (FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var node = JsonNode.Parse(raw.TrimStart('\uFEFF'), documentOptions: SourceJsonOptions);
                if (TableData.IsTableFile(node)) { tableRoot = (JsonObject)node!; Table = TableData.FromFile(node!, FilePath); }
            }
            if (Table != null) Diagnostics.AddRange(Validated());
            PendingSource = false;
        }
        catch (Exception e) { Diagnostics.Add(new(FilePath, e.Message)); PendingSource = true; }
    }
    private void Notify() { Revision++; IsDirty = state != savedState; Changed?.Invoke(); }
    private void Record(Action reverse)
    {
        var previous = state; state = ++nextState; editGroupRows = null; // any other history entry ends the mergeable run
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
        raw = text; modelChanged = false; PendingSource = true; Diagnostics = [new(FilePath, "Source changed; apply or validate before table editing.", "Info")]; LastChangedRows = null; LastChangedColumns = null; Notify();
    }
    public void ApplySource() { if (modelChanged) _ = Text; Parse(); LastChangedRows = null; LastChangedColumns = null; Changed?.Invoke(); }
    private int editGroupDepth;
    private Dictionary<int, JsonNode>? editGroupRows;
    /// <summary>
    /// Starts an edit session: every SetCells until EndEditGroup collapses into one undo step that restores the rows as they
    /// were when the session began. Text editors push a change per keystroke; users expect undo per committed value.
    /// </summary>
    public void BeginEditGroup() => editGroupDepth++;
    public void EndEditGroup() { if (editGroupDepth > 0 && --editGroupDepth == 0) editGroupRows = null; }
    public bool InEditGroup => editGroupDepth > 0;
    public void SetCells(IEnumerable<(int Row, string Column, string Value)> changes)
    {
        Require(Table != null && !PendingSource, "Apply valid source before editing cells.");
        var edits = changes.Where(e => Table!.Cell(e.Row, e.Column) != e.Value).ToArray(); if (edits.Length == 0) return;
        Require(edits.All(e => !LockedRows.Contains(e.Row) && !LockedColumns.Contains(e.Column)), "A selected row or column is locked against edits.");
        // Validate every cell before mutating the live document.
        var before = edits.Select(e => e.Row).Distinct().ToDictionary(i => i, i => Table!.Records[i]!.DeepClone());
        try { foreach (var e in edits) Table!.SetCell(e.Row, e.Column, e.Value); }
        catch { foreach (var p in before) Table!.Records[p.Key] = p.Value; throw; }
        if (editGroupDepth > 0 && editGroupRows != null && !historyPlayback)
        {
            // Same edit session: widen the existing undo step instead of adding one. Rows first touched now keep their pre-session state.
            foreach (var p in before) editGroupRows.TryAdd(p.Key, p.Value);
        }
        else
        {
            Record(() => RestoreRows(before));
            if (editGroupDepth > 0 && !historyPlayback) editGroupRows = before;
        }
        CellsChanged(before.Keys, edits.Select(e => e.Column).Distinct(StringComparer.Ordinal).ToArray());
    }
    private List<Diagnostic> Validated()
    {
        var diagnostics = Table!.Validate(FilePath).ToList();
        if (Table!.RowOrderChanged) diagnostics.Add(new(FilePath, TableData.RowOrderAdvice, "Warning"));
        return diagnostics;
    }
    /// <summary>
    /// Finishes a change that replaced cell values in the given rows. A table that was valid before only needs those rows
    /// rechecked: SetCell keeps identities, slots and the row set, so the table-level checks cannot change. A full pass runs
    /// again in any error state, so diagnostics never go stale while the table is invalid.
    /// </summary>
    private void CellsChanged(IReadOnlyCollection<int> rows, IReadOnlyCollection<string> columns)
    {
        modelChanged = true;
        if (Diagnostics.Any(d => d.Severity == "Error") || Table!.ValidateRows(FilePath, rows).Count > 0) Diagnostics = Validated();
        LastChangedRows = rows; LastChangedColumns = columns; Notify();
    }
    /// <summary>Columns whose values differ between two versions of a record.</summary>
    private string[] ChangedColumns(JsonNode? before, JsonNode? after)
    {
        var key = Table!.IsCatalog ? "translations" : "fields"; var x = before?[key] as JsonObject; var y = after?[key] as JsonObject;
        return Table.Columns.Where(column => !JsonNode.DeepEquals(x?[column], y?[column])).ToArray();
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
    /// <summary>
    /// Moves rows so the block sits where <paramref name="target"/> pointed before the move (a slot in the current numbering,
    /// Records.Count meaning the end). Rows keep their identities; only slots change, so original rows raise the order advisory.
    /// </summary>
    public void MoveRows(IEnumerable<int> rows, int target)
    {
        Require(Table != null && !PendingSource, "Apply valid source before moving rows.");
        Require(!Table!.IsCatalog, "String entries keep their order; edit them in Source view.");
        var moving = rows.Distinct().Order().ToArray(); int count = Table.Records.Count;
        Require(moving.Length > 0 && moving.All(r => r >= 0 && r < count), "Invalid row.");
        Require(target >= 0 && target <= count, "Invalid row position.");
        Require(moving.All(r => !LockedRows.Contains(r)), "A selected row is locked against edits.");
        var order = MoveOrder(count, moving, target);
        if (order.Select((r, i) => r == i).All(same => same)) return;
        Permute(order);
    }
    /// <summary>Slot order after moving rows: order[newSlot] = old slot. Shared with views so their row-indexed state can follow.</summary>
    public static int[] MoveOrder(int count, IEnumerable<int> rows, int target)
    {
        var moving = rows.Distinct().Order().ToArray();
        var staying = Enumerable.Range(0, count).Where(r => Array.BinarySearch(moving, r) < 0).ToList();
        staying.InsertRange(target - moving.Count(r => r < target), moving);
        return staying.ToArray();
    }
    /// <summary>Reorders records so slot i holds what was at order[i]; locked rows follow their records. The inverse permutation is the undo step.</summary>
    private void Permute(int[] order)
    {
        var records = order.Select(i => Table!.Records[i]!).ToArray();
        Table!.Records.Clear(); foreach (var record in records) Table.Records.Add(record);
        var inverse = new int[order.Length]; for (int i = 0; i < order.Length; i++) inverse[order[i]] = i;
        var locked = LockedRows.Select(r => inverse[r]).ToArray(); LockedRows.Clear(); LockedRows.UnionWith(locked);
        Renumber(); Record(() => Permute(inverse)); Finish();
    }
    /// <summary>
    /// Moves a column to another position. Column order is part of the schema (and of the emitted TSV), so the schema file is
    /// written on the next save. Rows narrower than the affected span are widened to the full column count so no value can
    /// fall outside its row's width.
    /// </summary>
    public void MoveColumn(int from, int to) => MoveColumn(from, to, null);
    private void MoveColumn(int from, int to, Dictionary<int, int>? restoreWidths)
    {
        Require(Table != null && !PendingSource, "Apply valid source before moving columns.");
        Require(!Table!.IsCatalog, "String table columns are fixed.");
        var columns = (JsonArray)Table.Schema["columns"]!;
        Require(from >= 0 && from < columns.Count && to >= 0 && to < columns.Count, "Invalid column position.");
        if (from == to) return;
        var node = columns[from]!; columns.RemoveAt(from); columns.Insert(to, node);
        var widths = new Dictionary<int, int>();
        for (int i = 0; i < Table.Records.Count; i++)
        {
            var record = (JsonObject)Table.Records[i]!; int width = record.I("columnCount");
            if (restoreWidths != null) { if (restoreWidths.TryGetValue(i, out var previous)) { widths[i] = width; record["columnCount"] = previous; } }
            else if (width > Math.Min(from, to) && width < columns.Count) { widths[i] = width; record["columnCount"] = columns.Count; }
        }
        Table = new TableData(Table.Schema, Table.Records);
        foreach (var record in Table.Records.OfType<JsonObject>())
        {
            var fields = (JsonObject)record["fields"]!; var ordered = new JsonObject();
            foreach (var key in Table.Columns) if (fields.ContainsKey(key)) ordered[key] = fields[key]!.DeepClone();
            record["fields"] = ordered;
        }
        Record(() => MoveColumn(to, from, widths)); Finish();
    }
    private void Renumber() { for (int i = 0; i < Table!.Records.Count; i++) Table.Records[i]!["order"] = i; }
    private void Finish() { modelChanged = true; Diagnostics = Validated(); LastChangedRows = null; LastChangedColumns = null; Notify(); }
    private void RestoreRows(Dictionary<int, JsonNode> rows)
    {
        Require(Table != null && !PendingSource, "Apply source before undoing table changes.");
        var reverse = rows.Keys.ToDictionary(i => i, i => Table!.Records[i]!.DeepClone());
        var columns = rows.SelectMany(p => ChangedColumns(reverse[p.Key], p.Value)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var p in rows) Table!.Records[p.Key] = p.Value.DeepClone(); Record(() => RestoreRows(reverse));
        CellsChanged(rows.Keys, columns);
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
        Require(!ExternalChange, "File changed externally. Reload before saving; your edits are preserved in recovery.");
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
