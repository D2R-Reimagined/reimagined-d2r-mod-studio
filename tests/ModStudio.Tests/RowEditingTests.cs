using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class RowEditingTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var dir = Path.Combine(root, "row-editing/source/tables/example"); Directory.CreateDirectory(dir);
        var table = TableData.FromTsv(Utf8.GetBytes("name\tvalue\r\nA\t1\r\nB\t2\r\nC\t3\r\n"), "example", "global/excel/example.txt");
        File.WriteAllText(Path.Combine(dir, "schema.json"), Json(table.Schema)); File.WriteAllText(Path.Combine(dir, "records.json"), Json(table.Records));
        var doc = new Document(Path.Combine(dir, "records.json"));
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

        doc.LockedRows.Add(1); throws(() => doc.InsertRows(1), "Inserting above a locked row is refused"); doc.LockedRows.Clear();
        doc.InsertRows(doc.Table.Records.Count); check(!doc.Diagnostics.Any(d => d.Severity == "Warning"), "Appending at the bottom never triggers the order advisory");
        var edited = Json(doc.Table.Records); var broken = JsonNode.Parse(edited)!.AsArray(); broken.RemoveAt(0); for (int i = 0; i < broken.Count; i++) broken[i]!["order"] = i;
        check(new TableData((JsonObject)doc.Table.Schema.DeepClone(), broken).Validate("x").Any(d => d.Message.Contains("Original rows were removed")), "Validation names removed original rows");
        var catalogDir = Path.Combine(root, "row-editing/source/strings/ui"); Directory.CreateDirectory(catalogDir);
        File.WriteAllText(Path.Combine(catalogDir, "schema.json"), new JsonObject { ["schemaVersion"] = 1, ["category"] = "ui", ["locales"] = new JsonArray("enUS") }.ToJsonString());
        File.WriteAllText(Path.Combine(catalogDir, "records.json"), new JsonArray(new JsonObject { ["order"] = 0, ["id"] = 1, ["Key"] = "k", ["translations"] = new JsonObject { ["enUS"] = "v" } }).ToJsonString());
        throws(() => new Document(Path.Combine(catalogDir, "records.json")).InsertRows(1), "String catalogs refuse row insertion with a clear message");
    }
}
