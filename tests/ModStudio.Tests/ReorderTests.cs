using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class ReorderTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var dir = Path.Combine(root, "reorder/source/tables"); Directory.CreateDirectory(dir);
        var table = TableData.FromTsv(Utf8.GetBytes("name\tvalue\textra\r\nA\t1\r\nB\t2\t\r\nC\t3\tz\r\nD\t4\r\n"), "example", "global/excel/example.txt");
        TableData.Write(Path.Combine(dir, "example.json"), table);
        var doc = new Document(Path.Combine(dir, "example.json"));
        bool Clean() => doc.Diagnostics.All(d => d.Severity != "Error");
        string Names() => string.Concat(Enumerable.Range(0, doc.Table!.Records.Count).Select(i => doc.Table.Cell(i, "name")));
        bool Ordered() => doc.Table!.Records.Select((r, i) => r!["order"]!.GetValue<int>() == i).All(x => x);

        doc.MoveRows([0], 3);
        check(Names() == "BCAD" && Ordered() && doc.Table!.Records[2].S("sourceId") == "row-00000" && Clean(), "Moving a row down places it before the targeted slot and keeps its identity");
        check(doc.Diagnostics.Any(d => d.Severity == "Warning" && d.Message == TableData.RowOrderAdvice), "Moving original rows raises the order advisory");
        doc.Undo(); check(Names() == "ABCD" && Ordered() && !doc.Diagnostics.Any(d => d.Severity == "Warning"), "Undo restores the previous row order");
        doc.Redo(); check(Names() == "BCAD", "Redo re-applies the move"); doc.Undo();
        doc.MoveRows([1, 3], 0); check(Names() == "BDAC" && Ordered() && Clean(), "Non-contiguous rows move together to the top in their original order");
        doc.Undo(); check(Names() == "ABCD", "Undo restores a multi-row move");
        doc.MoveRows([3], 0); check(Names() == "DABC", "Moving a row up inserts before the target"); doc.Undo();
        doc.LockedRows.Add(2); doc.MoveRows([0], 4);
        check(Names() == "BCDA" && doc.LockedRows.SetEquals([1]), "Locked rows follow their records when others move");
        doc.Undo(); check(Names() == "ABCD" && doc.LockedRows.SetEquals([2]), "Undo moves the lock back");
        throws(() => doc.MoveRows([2], 0), "Locked rows cannot be moved"); doc.LockedRows.Clear();
        doc.Save(); doc.MoveRows([1], 1); doc.MoveRows([1], 2);
        check(!doc.IsDirty && Names() == "ABCD", "Dropping a row where it already sits changes nothing");

        doc.MoveColumn(2, 0);
        check(doc.Table!.Columns.SequenceEqual(["extra", "name", "value"]) && Clean(), "Moving a column reorders the schema");
        check(Utf8.GetString(doc.Table.EncodeTsv()) == "extra\tname\tvalue\r\n\tA\t1\r\n\tB\t2\r\nz\tC\t3\r\n\tD\t4\r\n", "Rows narrower than the moved span widen so values keep their columns");
        check(((JsonObject)doc.Table.Records[2]!["fields"]!).Select(p => p.Key).SequenceEqual(["extra", "name", "value"]), "Record fields follow the new column order");
        doc.Save();
        var reloaded = new Document(doc.FilePath);
        check(!doc.ExternalChange && reloaded.Table!.Columns.SequenceEqual(["extra", "name", "value"]) && reloaded.Diagnostics.All(d => d.Severity != "Error"), "Saving writes the reordered schema alongside the records");
        doc.Undo();
        check(doc.Table!.Columns.SequenceEqual(["name", "value", "extra"]) && doc.IsDirty && doc.Table.Records[0].I("columnCount") == 2 && Utf8.GetString(doc.Table.EncodeTsv()).Contains("\r\nA\t1\r\n"), "Undo restores the column order and the original row widths");
        doc.Redo(); check(doc.Table.Columns.SequenceEqual(["extra", "name", "value"]) && !doc.IsDirty, "Redo returns to the saved column order");
        doc.MoveColumn(0, 2); doc.Save();
        check(new Document(doc.FilePath).Table!.Columns.SequenceEqual(["name", "value", "extra"]), "Moving the column back saves the original schema");
        throws(() => doc.MoveColumn(0, 3), "Column targets are bounded");

        var catalogDir = Path.Combine(root, "reorder/source/strings"); Directory.CreateDirectory(catalogDir);
        TableData.Write(Path.Combine(catalogDir, "ui.json"), new TableData(new JsonObject { ["schemaVersion"] = 1, ["category"] = "ui", ["locales"] = new JsonArray("enUS") }, new JsonArray(new JsonObject { ["order"] = 0, ["id"] = 1, ["Key"] = "k", ["translations"] = new JsonObject { ["enUS"] = "v" } }, new JsonObject { ["order"] = 1, ["id"] = 2, ["Key"] = "j", ["translations"] = new JsonObject { ["enUS"] = "w" } })));
        var catalog = new Document(Path.Combine(catalogDir, "ui.json"));
        throws(() => catalog.MoveRows([0], 2), "String catalogs refuse row moves"); throws(() => catalog.MoveColumn(0, 1), "String catalogs refuse column moves");
    }
}
