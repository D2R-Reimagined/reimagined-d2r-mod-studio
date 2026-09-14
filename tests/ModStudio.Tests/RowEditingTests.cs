using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class RowEditingTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var dir = Path.Combine(root, "row-editing/source/tables"); Directory.CreateDirectory(dir);
        var table = TableData.FromTsv(Utf8.GetBytes("name\tvalue\r\nA\t1\r\nB\t2\r\nC\t3\r\n"), "example", "global/excel/example.txt");
        TableData.Write(Path.Combine(dir, "example.json"), table);
        var doc = new Document(Path.Combine(dir, "example.json"));
        bool Clean() => doc.Diagnostics.All(d => d.Severity != "Error");
        int[] Orders() => doc.Table!.Records.Select(r => r!["order"]!.GetValue<int>()).ToArray();

        doc.InsertRows(1);
        check(doc.Table!.Records.Count == 4 && Orders().SequenceEqual([0, 1, 2, 3]) && doc.Table.Records[2].S("sourceId") == "row-00001", "Inserting a row keeps later identities and renumbers slots");
        check(!doc.Table.IsOriginalRow(1) && doc.Table.IsOriginalRow(2) && doc.Table.Cell(1, "name") == "" && Clean(), "New rows are blank, non-original and valid");
        check(doc.Diagnostics.Any(d => d.Severity == "Warning" && d.Message == TableData.RowOrderAdvice), "Inserting before original rows is advised, not blocked");
        doc.SetCells([(1, "name", "Inserted"), (1, "value", "9")]);
        check(Utf8.GetString(doc.Table.EncodeTsv()) == "name\tvalue\r\nA\t1\r\nInserted\t9\r\nB\t2\r\nC\t3\r\n", "Inserted rows encode into the TSV in slot order");
        doc.Save(); check(!doc.IsDirty && new Document(doc.FilePath).Table!.Records.Count == 4, "Documents with inserted rows save and reload");
        doc.Undo(); doc.Undo();
        check(doc.Table.Records.Count == 3 && doc.Table.Cell(1, "name") == "B" && !doc.Diagnostics.Any(d => d.Severity == "Warning"), "Undo removes the inserted row and its advisory");
        doc.Redo(); check(doc.Table.Records.Count == 4 && doc.Table.Cell(1, "name") == "", "Redo restores the inserted row"); doc.Redo(); check(doc.Table.Cell(1, "name") == "Inserted", "Redo restores the cell edit after the insert");

        doc.InsertRows(doc.Table.Records.Count, 2, [new JsonObject { ["name"] = "Tail", ["value"] = "" }, null]);
        check(doc.Table.Records.Count == 6 && doc.Table.Cell(4, "name") == "Tail" && doc.Table.Cell(5, "name") == "" && Orders().SequenceEqual([0, 1, 2, 3, 4, 5]), "Appending pre-filled rows fills only non-empty cells");
        check(doc.Table.Records.Select(r => r.S("sourceId")).Distinct().Count() == 6, "Every row has a unique identity");

        throws(() => doc.DeleteRows([0]), "Original rows cannot be deleted");
        throws(() => doc.DeleteRows([2]), "Original rows keep protection after being shifted");
        doc.DeleteRows([1, 5]);
        check(doc.Table.Records.Count == 4 && doc.Table.Cell(1, "name") == "B" && doc.Table.Cell(3, "name") == "Tail" && Orders().SequenceEqual([0, 1, 2, 3]) && Clean(), "Deleting non-contiguous added rows renumbers the rest");
        doc.Undo(); check(doc.Table.Records.Count == 6 && doc.Table.Cell(1, "name") == "Inserted" && doc.Table.Cell(5, "name") == "", "Undo restores deleted rows at their slots");
        doc.Redo(); check(doc.Table.Records.Count == 4, "Redo deletes them again");

        // Keystroke-level edits inside one edit session collapse into a single undo step.
        doc.Save(); doc.BeginEditGroup();
        foreach (var partial in new[] { "s", "so", "som", "some", "somet", "something" }) doc.SetCells([(0, "value", partial)]);
        doc.SetCells([(3, "name", "grouped")]);
        doc.EndEditGroup();
        check(doc.Table.Cell(0, "value") == "something" && doc.IsDirty, "Grouped edits apply immediately");
        doc.Undo(); check(doc.Table.Cell(0, "value") == "1" && doc.Table.Cell(3, "name") == "Tail" && !doc.IsDirty, "One undo restores everything typed during the edit session");
        doc.Redo(); check(doc.Table.Cell(0, "value") == "something" && doc.Table.Cell(3, "name") == "grouped", "One redo re-applies the whole session");
        doc.Undo();
        doc.BeginEditGroup(); doc.SetCells([(0, "value", "a")]); doc.InsertRows(doc.Table.Records.Count); doc.SetCells([(0, "value", "ab")]); doc.EndEditGroup();
        doc.Undo(); check(doc.Table.Cell(0, "value") == "a", "A row insertion inside a session starts a new undo step"); doc.Undo(); doc.Undo();
        check(doc.Table.Cell(0, "value") == "1" && doc.Table.Records.Count == 4, "Steps around the insertion undo in order");
        doc.SetCells([(0, "value", "outside")]); doc.SetCells([(0, "value", "outside2")]); doc.Undo();
        check(doc.Table.Cell(0, "value") == "outside", "Edits outside a session still undo individually"); doc.Undo();
        doc.LockedRows.Add(1); throws(() => doc.InsertRows(1), "Inserting above a locked row is refused"); doc.LockedRows.Clear();
        doc.InsertRows(doc.Table.Records.Count); check(!doc.Diagnostics.Any(d => d.Severity == "Warning"), "Appending at the bottom never triggers the order advisory");
        var edited = Json(doc.Table.Records); var broken = JsonNode.Parse(edited)!.AsArray(); broken.RemoveAt(0); for (int i = 0; i < broken.Count; i++) broken[i]!["order"] = i;
        check(new TableData((JsonObject)doc.Table.Schema.DeepClone(), broken).Validate("x").Any(d => d.Message.Contains("Original rows were removed")), "Validation names removed original rows");
        var catalogDir = Path.Combine(root, "row-editing/source/strings"); Directory.CreateDirectory(catalogDir);
        TableData.Write(Path.Combine(catalogDir, "ui.json"), new TableData(new JsonObject { ["schemaVersion"] = 1, ["category"] = "ui", ["locales"] = new JsonArray("enUS") }, new JsonArray(new JsonObject { ["order"] = 0, ["id"] = 1, ["Key"] = "k", ["translations"] = new JsonObject { ["enUS"] = "v" } })));
        throws(() => new Document(Path.Combine(catalogDir, "ui.json")).InsertRows(1), "String catalogs refuse row insertion with a clear message");

        // Views refresh in place when a change only replaced cell values, and rebuild after anything structural.
        var original = File.ReadAllText(doc.FilePath); var tracked = new Document(doc.FilePath);
        tracked.SetCells([(0, "value", "10"), (2, "value", "30")]);
        check(tracked.LastChangedRows?.Order().SequenceEqual([0, 2]) == true && tracked.LastChangedColumns?.SequenceEqual(["value"]) == true && tracked.Diagnostics.Count == 0, "Cell edits report the rows and columns they touched");
        tracked.Undo();
        check(tracked.LastChangedRows?.Order().SequenceEqual([0, 2]) == true && tracked.LastChangedColumns?.SequenceEqual(["value"]) == true, "Undo reports the rows and columns it restored");
        tracked.InsertRows(1); check(tracked.LastChangedRows == null && tracked.LastChangedColumns == null, "Row insertion reports a structural change");
        tracked.Undo(); check(tracked.LastChangedRows == null, "Undoing a row insertion reports a structural change");
        tracked.SetRaw(tracked.Text.Replace("\"value\": \"1\"", "\"value\": \"11\"")); tracked.ApplySource();
        check(tracked.LastChangedRows == null && tracked.Table!.Cell(0, "value") == "11", "Source edits report a structural change");
        // A table with an existing error keeps validating fully, so its diagnostics never go stale after a cell edit.
        var invalid = JsonNode.Parse(Json(tracked.Table!.ToFile()))!.AsObject(); invalid["records"]![1]!["order"] = 7;
        File.WriteAllText(tracked.FilePath, Json(invalid)); var erroneous = new Document(tracked.FilePath);
        check(erroneous.Diagnostics.Any(d => d.Severity == "Error" && d.Row == 1), "Invalid slot numbering is reported on open");
        erroneous.SetCells([(0, "value", "12")]);
        check(erroneous.Diagnostics.Any(d => d.Severity == "Error" && d.Row == 1) && erroneous.LastChangedRows?.SequenceEqual([0]) == true, "Editing an invalid table keeps its existing diagnostics");
        File.WriteAllText(tracked.FilePath, original);
    }
}
