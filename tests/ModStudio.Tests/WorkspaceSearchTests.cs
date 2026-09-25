using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class WorkspaceSearchTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var native = Path.Combine(root, "search-native");
        var excel = Path.Combine(native, "global/excel"); Directory.CreateDirectory(excel);
        File.WriteAllText(Path.Combine(excel, "weapons.txt"), "name\tcode\ttype\nHand Axe\thax\taxe\nBattle Axe\tbax\taxe\nShort Sword\tssd\tswor\n", Utf8);
        File.WriteAllText(Path.Combine(excel, "armor.txt"), "name\tcode\nCap\tcap\nAxe Helm\taxh\n", Utf8);
        var strings = Path.Combine(native, "local/lng/strings"); Directory.CreateDirectory(strings);
        File.WriteAllText(Path.Combine(strings, "item-names.json"), "[{\"id\":1,\"Key\":\"hax\",\"enUS\":\"Hand Axe\",\"deDE\":\"Handaxt\"},{\"id\":2,\"Key\":\"cap\",\"enUS\":\"Cap\",\"deDE\":\"Kappe\"}]", Utf8);
        var layouts = Path.Combine(native, "global/ui/layouts"); Directory.CreateDirectory(layouts);
        File.WriteAllText(Path.Combine(layouts, "panel.json"), "{\r\n  \"name\": \"AxePanel\",\r\n  \"width\": 12\r\n}\r\n", Utf8);
        File.WriteAllBytes(Path.Combine(native, "global/ui/axe.dds"), [0, 1, 2, 3, (byte)'a', (byte)'x', (byte)'e']);
        File.WriteAllBytes(Path.Combine(native, "global/ui/notes.bin"), [(byte)'a', (byte)'x', (byte)'e', 0, 0, 7]);
        var project = ProjectImporter.Import(native, Path.Combine(root, "search-project"), "SearchTest").Project;
        var panel = Path.GetFullPath(Path.Combine(project.Root, "data/global/ui/layouts/panel.json"));
        var weapons = Semantics.TableFile(project, "weapons"); var armor = Semantics.TableFile(project, "armor"); var catalog = TableData.FileFor(project, "strings", "item-names");

        var all = WorkspaceSearch.Run(project, new("axe", Scope: "source"));
        var cells = all.Hits.Where(h => h.IsCell).ToArray(); var lines = all.Hits.Where(h => !h.IsCell).ToArray();
        check(cells.Select(h => (Path.GetFileName(h.File), h.Row, h.Column)).SequenceEqual([("item-names.json", 0, "enUS"), ("armor.json", 1, "name"), ("weapons.json", 0, "name"), ("weapons.json", 0, "type"), ("weapons.json", 1, "name"), ("weapons.json", 1, "type")]),
            "Search finds table and string cells in file, row and column order");
        check(all.Hits.All(h => h.File != panel) && lines.Length == 0, "Source scope skips data files");
        check(cells.First(h => h.File == weapons).Label == "Hand Axe" && cells.First(h => h.File == catalog).Label == "hax", "Cell hits are labelled with the row's name or key");
        check(cells.First(h => h.File == weapons && h.Column == "name").Start == 5 && cells.First(h => h.File == weapons && h.Column == "name").Length == 3, "Cell hits locate the match inside the value");
        check(all.Tables.ContainsKey(weapons) && all.Tables.ContainsKey(catalog) && all.Tables.Count == 3, "Parsed tables with hits are returned for previews");

        var whole = WorkspaceSearch.Run(project, new("axe"));
        var line = whole.Hits.Single(h => !h.IsCell);
        check(line.File == panel && line.Line == 2 && line.Text.Contains("AxePanel") && line.Offset == 11 && line.Length == 3,
            "Whole-project scope searches other files line by line with match offsets");
        check(whole.Hits.All(h => !h.File.EndsWith(".dds") && !h.File.EndsWith(".bin")), "Binary files are never searched");
        check(whole.Files > all.Files && whole.MatchedFiles == 4, "Result counts cover scanned and matched files");

        check(whole.Hits.Count == 7 && WorkspaceSearch.Run(project, new("Axe", MatchCase: true, Scope: "source")).Hits.Count == 4, "Match case restricts hits");
        check(WorkspaceSearch.Run(project, new("axe", WholeWord: true)).Hits.Count == 6, "Whole word excludes 'Axe' inside 'AxePanel' but keeps cells that are exactly 'axe'");
        check(WorkspaceSearch.Run(project, new("^b.*axe$", Regex: true)).Hits.Single().Row == 1, "Regular expressions match against whole cell values");
        throws(() => WorkspaceSearch.Run(project, new("(", Regex: true)), "Invalid regular expression is reported");
        check(WorkspaceSearch.Run(project, new("axe", FileMask: "armor*")).Hits.All(h => h.File == armor), "File mask narrows by file name");
        check(WorkspaceSearch.Run(project, new("axe", FileMask: "*.txt; *.md")).Hits.Count == 0, "Unmatched file masks find nothing");
        check(WorkspaceSearch.Run(project, new("axe", Scope: "source/strings")).Hits.All(h => h.File == catalog), "Scope restricts the folder searched");
        check(WorkspaceSearch.Run(project, new("")).Hits.Count == 0 && WorkspaceSearch.Run(project, new("")).Files == 0, "Empty query searches nothing");

        var document = new Document(weapons); document.SetCells([(2, "name", "Broad Axe")]);
        var buffered = WorkspaceSearch.Run(project, new("broad axe"), new Dictionary<string, TableData> { [weapons] = document.Table! });
        check(buffered.Hits.Single().Row == 2 && ReferenceEquals(buffered.Tables[weapons], document.Table), "Unsaved table buffers are searched instead of disk and returned for the preview");
        var raw = WorkspaceSearch.Run(project, new("axe raw"), texts: new Dictionary<string, string> { [weapons] = "line one\nHand Axe raw\n" });
        check(raw.Hits.Single() is { IsCell: false, Line: 2 }, "Raw source under edit is searched as text");

        // A file's timestamp must be over two seconds old before the cache trusts it, so age the fixture.
        foreach (var entry in project.SourceEntries()) File.SetLastWriteTimeUtc(entry.FullName, DateTime.UtcNow.AddMinutes(-10));
        var cache = new SearchCache(); cache.Warm(project);
        SearchQuery[] queries = [new("axe"), new("axe", Scope: "source"), new("Axe", MatchCase: true), new("axe", WholeWord: true), new("^b.*axe$", Regex: true), new("a.e", Regex: true), new("AXE", FileMask: "armor*"), new("hax"), new("panel")];
        static string Shape(SearchResult r) => string.Join("|", r.Hits.Select(h => $"{h.File},{h.Row},{h.Column},{h.Line},{h.Text},{h.Start},{h.Length},{h.Label},{h.Offset}")) + $"#{r.Files},{r.MatchedFiles},{r.Truncated}";
        check(cache.Count == project.SourceEntries().Count(f => !f.FullName.EndsWith(".dds") && !f.FullName.EndsWith(".bin")), "Warming caches every searchable file");
        check(queries.All(q => Shape(WorkspaceSearch.Run(project, q, cache: cache)) == Shape(WorkspaceSearch.Run(project, q))), "Cached search returns exactly the hits of a fresh search in every mode");
        check(WorkspaceSearch.Run(project, new("axe"), cache: cache).Tables.Count == 0 && WorkspaceSearch.ReadTable(project, weapons)!.Cell(0, "name") == "Hand Axe" && WorkspaceSearch.ReadTable(project, panel) == null,
            "Warm searches parse no tables; previews read the ones they need");
        var weaponsTable = TableData.Load(weapons); weaponsTable.SetCell(1, "name", "Battle Hammer"); TableData.Write(weapons, weaponsTable);
        File.SetLastWriteTimeUtc(weapons, DateTime.UtcNow.AddMinutes(-5));
        var changed = WorkspaceSearch.Run(project, new("hammer"), cache: cache);
        check(changed.Hits.Single() is { Row: 1, Column: "name" } && changed.Tables.ContainsKey(weapons), "A file whose write time changed is read again");
        weaponsTable.SetCell(1, "name", "Battle Axe"); TableData.Write(weapons, weaponsTable);
        check(WorkspaceSearch.Run(project, new("battle axe"), cache: cache).Hits.Count == 1 && WorkspaceSearch.Run(project, new("battle axe"), cache: cache).Tables.Count == 1, "A file written in the last two seconds is never trusted from the cache");
        File.Delete(panel); File.SetLastWriteTimeUtc(weapons, DateTime.UtcNow.AddMinutes(-1)); cache.Warm(project);
        check(cache.Count == project.SourceEntries().Count(f => !f.FullName.EndsWith(".dds") && !f.FullName.EndsWith(".bin")), "Warming drops files that no longer exist");

        File.WriteAllText(armor, "{ not a table", Utf8);
        var broken = WorkspaceSearch.Run(project, new("table", Scope: "source/tables"));
        check(broken.Hits.Single() is { IsCell: false, Line: 1 } && broken.Hits[0].File == armor, "A table that fails to parse is searched as text");

        var big = TableData.FromTsv(Utf8.GetBytes("name\n" + string.Concat(Enumerable.Range(0, WorkspaceSearch.MaxHits + 50).Select(i => $"many {i}\n"))), "many", "global/excel/many.txt");
        TableData.Write(Semantics.TableFile(project, "many"), big);
        var capped = WorkspaceSearch.Run(project, new("many"));
        check(capped.Truncated && capped.Hits.Count == WorkspaceSearch.MaxHits, "Hits stop at the cap and report truncation");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool stopped = false;
        try { WorkspaceSearch.Run(project, new("axe"), token: canceled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "Search honors cancellation");

        var hits = new List<SearchHit>();
        WorkspaceSearch.SearchText("f", "{" + new string('x', 500) + "needle" + new string('y', 500) + "}", WorkspaceSearch.Matcher(new("needle")), hits);
        check(hits.Single() is { Line: 1, Offset: 501 } trimmed && trimmed.Text.StartsWith('…') && trimmed.Text.EndsWith('…') && trimmed.Text.Substring(trimmed.Start, trimmed.Length) == "needle", "Long lines are trimmed around the match with the snippet offsets kept accurate");
    }
}
