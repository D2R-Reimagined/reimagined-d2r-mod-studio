using System.Text.Json.Nodes;
using ModStudio.Core;

internal static class GuideAndDetectionTests
{
    public static void Run(string root, Action<bool, string> check, Action<string, string> write)
    {
        // Column guide (bundled d2rdoc data)
        var uniques = ColumnGuide.File("uniqueitems");
        check(uniques != null && uniques.Title.Equals("UniqueItems.txt", StringComparison.OrdinalIgnoreCase) && uniques.Fields.Length > 20, "Column guide bundles the unique items page");
        check(ColumnGuide.File("global/excel/UniqueItems.txt") == uniques && ColumnGuide.File("MagicSuffix") != null, "Column guide resolves targets and mixed-case file names");
        var code = ColumnGuide.Find("uniqueitems", "code");
        check(code != null && code.Description.Length > 10 && !code.Description.Contains("$!") && !code.Description.Contains('<'), "Column descriptions are plain text without guide link markup");
        check(ColumnGuide.Find("uniqueitems", "prop7") is { Name: "prop#" } && ColumnGuide.Find("uniqueitems", "MIN12") is { Name: "min#" }, "Numbered columns map to their documented family");
        check(ColumnGuide.Find("uniqueitems", "index#2") != null && ColumnGuide.Find("uniqueitems", "no such column") == null && ColumnGuide.Find("not-a-table", "code") == null, "Duplicate-header suffixes are ignored and unknown columns/tables return nothing");
        var descfunc = ColumnGuide.Find("itemstatcost", "descfunc");
        check(descfunc is { Table.Length: > 5 } && descfunc.Summary().Contains("Type:") && descfunc.Summary(3).Contains("more rows"), "Reference tables are bundled and summaries are bounded");
        check(ColumnGuide.Find("armor", "invfile") != null && ColumnGuide.Find("weapons", "1or2handed") != null, "Shared item fields are appended to armor/weapons pages");
        check(ColumnGuide.Url("uniqueitems", "prop3") == "https://eezstreet.github.io/d2rdoc/files/uniqueitems.html#prop3" && ColumnGuide.Url("magicsuffix").Contains("/files/MagicSuffix.html"), "Guide links keep the site's page casing and field anchors");

        // Guide-derived reference navigation
        var guideRule = Semantics.GuideReference("skills", "srvmissilea");
        check(guideRule is { ReferenceTables: ["missiles"], ReferenceColumn: "Missile" } && Semantics.GuideReference("skills", "skilldesc") is { ReferenceTables: ["skilldesc"] }, "Data guide reference types become navigation rules");
        check(Semantics.GuideReference("skills", "reqlevel") == null && Semantics.GuideReference("nope", "x") == null, "Non-reference columns produce no guide rule");
        var refProject = new ModProject(Path.Combine(root, "guide-refs"), "refs", "Refs"); Directory.CreateDirectory(refProject.Root);
        var missiles = TableData.FromTsv(Storage.Utf8.GetBytes("missile\tvelocity\narrow\t10\nfirebolt\t20\n"), "missiles", "global/excel/missiles.txt");
        write(Path.Combine(refProject.Root, "source/tables/missiles/schema.json"), Storage.Json(missiles.Schema)); write(Path.Combine(refProject.Root, "source/tables/missiles/records.json"), Storage.Json(missiles.Records));
        var hits = Semantics.References(refProject, guideRule!, "firebolt");
        check(hits.Count == 1 && hits[0].Row == 1 && hits[0].Column == "missile", "References match the guide's column name case-insensitively against the project's header");

        // Project locales
        var project = new ModProject(Path.Combine(root, "locales"), "loc", "Loc"); Directory.CreateDirectory(project.Root);
        check(project.Locales().SequenceEqual(ModProject.GameLocales) && ModProject.GameLocales[0] == "enUS", "Projects without catalogs offer the game's locales");
        write(Path.Combine(project.Root, "source/strings/ui/schema.json"), new JsonObject { ["locales"] = new JsonArray("deDE", "frFR") }.ToJsonString());
        write(Path.Combine(project.Root, "source/strings/item-names/schema.json"), new JsonObject { ["locales"] = new JsonArray("enUS", "deDE") }.ToJsonString());
        check(project.Locales().SequenceEqual(["enUS", "deDE", "frFR"]), "Catalogs are sorted by path before locales are merged in declaration order");

        // Steam library parsing
        var steam = Path.Combine(root, "steam"); var library = Path.Combine(root, "library");
        write(Path.Combine(steam, "steamapps/libraryfolders.vdf"), "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + library.Replace("\\", "\\\\") + "\"\n\t}\n}\n");
        var libraries = GameInstallDetector.ParseLibraryFolders(Path.Combine(steam, "steamapps/libraryfolders.vdf"));
        check(libraries.Count == 2 && libraries[1] == library, "Steam libraryfolders.vdf paths are unescaped");
        check(GameInstallDetector.ParseLibraryFolders(Path.Combine(root, "missing.vdf")).Count == 0, "Missing Steam library file yields no libraries");

        // Detection with an injected registry
        var game = Path.Combine(root, "registry-game"); write(Path.Combine(game, "D2R.exe"), "fixture"); write(Path.Combine(game, "D2RLoader.exe"), "fixture");
        var steamGame = Path.Combine(library, "steamapps", "common", GameInstallDetector.GameFolder); write(Path.Combine(steamGame, "D2R.exe"), "fixture");
        var empty = Path.Combine(root, "empty-game"); Directory.CreateDirectory(empty);
        string? Registry(string key) => key.Contains("Uninstall\\Diablo II Resurrected|") && key.StartsWith("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node") ? game
            : key.Contains("Steam App 2181070") ? empty : key.EndsWith("|SteamPath") ? steam : null;
        check(GameInstallDetector.SteamLibraries(Registry).Contains(library), "Injected Steam registry paths expand configured libraries on every platform");
        check(GameInstallDetector.SteamLibraries(key => key.EndsWith("|InstallPath") ? steam : null).Contains(library), "Machine Steam registry paths expand configured libraries on every platform");
        var found = GameInstallDetector.Detect(Registry);
        check(found.Count >= 1 && found[0].Directory == game && found[0].Source.Contains("registry") && found[0].Executables.SequenceEqual(["D2R.exe", "D2RLoader.exe"]), "Registry install location is reported first with its launchers");
        check(found.Any(f => f.Directory == steamGame && f.Source.Contains("Steam")) && found.All(f => f.Directory != empty), "Configured Steam libraries are searched and folders without executables are skipped");
        check(found.Select(f => f.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count() == found.Count, "Detected installations are unique");

        // Defaults never replace explicit settings
        var filled = new RunSettings().WithDetectedDefaults(project, found);
        check(filled.GameDirectory == game && filled.LaunchTarget == "D2R.exe" && filled.DeploymentDirectory == Path.Combine(game, "mods", "Loc"), "Empty run settings receive the detected game and mods folder");
        var loader = new RunSettings(LaunchTarget: "D2RLoader.exe").WithDetectedDefaults(project, found);
        check(loader.LaunchTarget == "D2RLoader.exe", "A preferred launcher is kept when the detected install has it");
        var explicitSettings = new RunSettings(DeploymentDirectory: "X:/mods/Loc", GameDirectory: "X:/game");
        check(explicitSettings.WithDetectedDefaults(project, found) == explicitSettings && new RunSettings().WithDetectedDefaults(project, []) == new RunSettings(), "Explicit paths and missing detections leave settings unchanged");
    }
}
