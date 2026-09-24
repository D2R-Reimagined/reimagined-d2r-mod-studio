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

        var clone = new Document(doc.FilePath);
        var originalRows = Json(clone.Table!.Records);
        check(clone.CloneRows([2, 0, 2], append: true) == 3 && clone.Table.Records.Count == 5 &&
            clone.Table.Cell(3, "name") == "A" && clone.Table.Cell(4, "value") == "3",
            "Clone and Append copies distinct selected rows in table order to the end");
        check(clone.Table.Records.Select(r => r.S("sourceId")).Distinct().Count() == 5 && !clone.Table.IsOriginalRow(3),
            "Clones receive fresh editable Studio identities");
        clone.Undo(); check(Json(clone.Table.Records) == originalRows, "One undo removes the entire row clone operation");
        clone.Redo(); check(clone.Table.Cell(4, "name") == "C", "Redo restores cloned rows and their values"); clone.Undo();
        check(clone.CloneRows([0], append: false) == 1 && clone.Table.Cell(1, "value") == "1" && clone.Table.Cell(2, "name") == "B",
            "Clone and Insert puts copies immediately after the last selected row");
        clone.SetCells([(1, "value", "independent")]); check(clone.Table.Cell(0, "value") == "1", "Editing a clone leaves its source unchanged");
        clone.Undo(); clone.Undo();
        throws(() => clone.CloneRows([], true), "Cloning requires a source selection");
        throws(() => clone.CloneRows([-1], true), "The add-row placeholder cannot be cloned");
        clone.LockedRows.Add(1); throws(() => clone.CloneRows([0], false), "Clone insertion respects a locked destination row");
        check(Json(clone.Table.Records) == originalRows, "Rejected clones leave the table unchanged");

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

        var beforeDelete = Json(doc.Table.Records);
        doc.DeleteRows([0, 2]);
        check(doc.Table.Records.Count == 4 && doc.Table.Cell(0, "name") == "Inserted" && doc.Table.Cell(1, "name") == "C" && Orders().SequenceEqual([0, 1, 2, 3]) && Clean(),
            "Original rows can be deleted and later rows shift up");
        check(doc.Diagnostics.Any(d => d.Severity == "Warning" && d.Message == TableData.RowOrderAdvice), "Deleting original rows before others raises the order advisory");
        doc.Undo(); check(Json(doc.Table.Records) == beforeDelete, "Undo restores deleted original rows at their slots");
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
        var trimmed = new TableData((JsonObject)doc.Table.Schema.DeepClone(), broken);
        check(trimmed.Validate("x").Count == 0 && trimmed.RowOrderChanged, "Tables with removed original rows validate, with the order advisory");
        var catalogDir = Path.Combine(root, "row-editing/source/strings"); Directory.CreateDirectory(catalogDir);
        TableData.Write(Path.Combine(catalogDir, "ui.json"), new TableData(new JsonObject { ["schemaVersion"] = 1, ["category"] = "ui", ["locales"] = new JsonArray("enUS") }, new JsonArray(new JsonObject { ["order"] = 0, ["id"] = 1, ["Key"] = "k", ["translations"] = new JsonObject { ["enUS"] = "v" } })));
        var strings = new Document(Path.Combine(catalogDir, "ui.json"));
        strings.InsertRows(1);
        check(strings.Table!.Records.Count == 2 && strings.Table.Cell(1, "id") == "2" && strings.Table.Cell(1, "Key") == "" && strings.Table.Cell(1, "enUS") == "" && !strings.Table.IsOriginalRow(1) && strings.Table.IsOriginalRow(0), "New string entries get the next free ID, empty key and translations, and are not original");
        check(strings.Diagnostics.Any(d => d.Severity == "Error" && d.Row == 1 && d.Message.Contains("key")), "A new string entry is flagged until it has a key");
        strings.SetCells([(1, "Key", "Added"), (1, "enUS", "Added text"), (1, "id", "40")]);
        check(strings.Table.Cell(1, "Key") == "Added" && strings.Table.Cell(1, "id") == "40" && strings.Diagnostics.Count == 0, "Studio-added entries can set their key and ID and become valid");
        throws(() => strings.SetCells([(0, "Key", "Renamed")]), "Imported string keys stay protected"); throws(() => strings.SetCells([(1, "id", "x")]), "String IDs must be integers");
        strings.DeleteRows([0]); check(strings.Table.Records.Count == 1 && strings.Table.Cell(0, "Key") == "Added" && strings.Diagnostics.Count == 0, "Imported string entries can be deleted"); strings.Undo();
        strings.DeleteRows([1]); check(strings.Table.Records.Count == 1, "Added string entries can be deleted");
        strings.Undo(); check(strings.Table.Records.Count == 2 && strings.Table.Cell(1, "Key") == "Added", "Undo restores a deleted string entry");
        var encoded = System.Text.Json.Nodes.JsonNode.Parse(Utf8.GetString(strings.Table.EncodeCatalog(false)).TrimStart('\uFEFF'))!.AsArray();
        check(encoded.Count == 2 && encoded[1]!["id"]!.GetValue<int>() == 40 && encoded[1]!["Key"]!.GetValue<string>() == "Added" && encoded[1]!["sourceId"] == null, "Added entries build into the game catalog without Studio metadata");
        strings.CloneRows([0, 1], true);
        check(strings.Table.Cell(2, "id") == "41" && strings.Table.Cell(3, "id") == "42" &&
            strings.Table.Cell(2, "Key") == "k" && strings.Table.Cell(3, "enUS") == "Added text",
            "Cloned string rows retain keys and translations with distinct new numeric IDs");
        strings.Undo();

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
        // Hand-edited source may insert or drop entries without renumbering "order": array position wins and the numbers are repaired on load.
        var renumber = JsonNode.Parse(Json(tracked.Table!.ToFile()))!.AsObject(); renumber["records"]![1]!["order"] = 7;
        File.WriteAllText(tracked.FilePath, Json(renumber)); var repaired = new Document(tracked.FilePath);
        check(repaired.Diagnostics.All(d => d.Severity != "Error") && repaired.Table!.Records[1]!["order"]!.GetValue<int>() == 1 && repaired.Text.Contains("\"order\": 1"), "Stale slot numbers are corrected on open instead of reported");
        var invalid = JsonNode.Parse(Json(tracked.Table!.ToFile()))!.AsObject(); invalid["records"]![1]!["sourceId"] = invalid["records"]![0]!["sourceId"]!.GetValue<string>();
        File.WriteAllText(tracked.FilePath, Json(invalid)); var erroneous = new Document(tracked.FilePath);
        check(erroneous.Diagnostics.Any(d => d.Severity == "Error" && d.Row == 1), "Duplicate source IDs are reported on open");
        erroneous.SetCells([(0, "value", "12")]);
        check(erroneous.Diagnostics.Any(d => d.Severity == "Error" && d.Row == 1) && erroneous.LastChangedRows?.SequenceEqual([0]) == true, "Editing an invalid table keeps its existing diagnostics");
        File.WriteAllText(tracked.FilePath, original);
    }
}
