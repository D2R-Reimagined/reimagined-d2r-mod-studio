using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class CellReferenceTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var native = Path.Combine(root, "reference-native");
        var excel = Path.Combine(native, "global/excel"); Directory.CreateDirectory(excel);
        foreach (var (name, text) in new[] {
            ("gamble", "name\tcode\nCap\tcap\n"), ("armor", "code\ncap\nshared\n"),
            ("weapons", "code\nhax\nshared\nshared\n"), ("misc", "code\nhp1\n") })
            File.WriteAllText(Path.Combine(excel, name + ".txt"), text, Utf8);
        var project = ProjectImporter.Import(native, Path.Combine(root, "reference-project"), "ReferenceTest").Project;
        var rule = CellReferences.Rule("gamble", "code")!;
        foreach (var (code, target) in new[] { ("cap", "armor"), ("hax", "weapons"), ("hp1", "misc") })
        {
            var result = CellReferences.Resolve(project, rule, code);
            check(result.Hits.Count == 1 && result.Hits[0].File == Semantics.TableFile(project, target) && result.Issues.Count == 0,
                "Gamble resolves " + target + " code");
        }
        check(CellReferences.Resolve(project, rule, "shared").Hits.Count == 3, "Reference resolver preserves duplicates within and across tables");
        check(CellReferences.Resolve(project, rule, "").Hits.Count == 0 && CellReferences.Resolve(project, rule, "missing").Hits.Count == 0,
            "Empty and unresolved references do not select an arbitrary item");
        check(CellReferences.Rule("automagic", "mod1code") != null && CellReferences.Rule("skills", "srvmissile") != null,
            "Guide reference families are enabled beyond Gamble");
        var weaponFile = Semantics.TableFile(project, "weapons"); var document = new Document(weaponFile);
        document.SetCells([(0, "code", "edited")]);
        var buffers = new Dictionary<string, TableData> { [weaponFile] = document.Table! };
        check(CellReferences.Resolve(project, rule, "edited", buffers).Hits.Count == 1 && CellReferences.Resolve(project, rule, "hax", buffers).Hits.Count == 0,
            "Reference navigation uses unsaved target values instead of disk");
        document.Undo();
        check(CellReferences.Resolve(project, rule, "hax", buffers).Hits.Count == 1, "Undo restores reference destination");
        var pending = CellReferences.Resolve(project, rule, "hax", pendingTables: new HashSet<string> { "weapons" });
        check(pending.Hits.Count == 0 && pending.Issues.Count == 1, "Pending target source never falls back to stale disk values");
        File.WriteAllText(Semantics.TableFile(project, "misc"), "broken", Utf8);
        var partial = CellReferences.Resolve(project, rule, "cap");
        check(partial.Hits.Count == 1 && partial.Issues.Count == 1, "Malformed target retains known matches with an incomplete-search explanation");
        File.Delete(Semantics.TableFile(project, "armor"));
        check(CellReferences.Resolve(project, rule, "cap").Issues.Count == 2, "Missing target tables are reported separately from unmatched values");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool stopped = false;
        try { CellReferences.Resolve(project, rule, "hax", token: canceled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "Reference lookup honors cancellation");
        Expanded(project, check);
    }

    private static void Expanded(ModProject project, Action<bool, string> check)
    {
        void Table(string name, string text) => TableData.Write(Semantics.TableFile(project, name), TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt"));
        Table("skills", "skill\t*Id\nAttack\t0\nFire Bolt\t36\n");
        Table("missiles", "Missile\t*ID\narrow\t0\nfirebolt\t1\n");
        Table("properties", "code\t*Id\tfunc1\tstat1\nskill\t0\t22\titem_singleskill\nstate\t1\t24\tstate\natt-mon%\t2\t24\tattack_vs_montype\nskilltab\t3\t10\titem_addskill_tab\nsock\t4\t14\t\ncharged\t5\t19\titem_charged_skill\nrandom\t6\t12\titem_singleskill\nvalue\t7\t17\titem_singleskill\n");
        Table("states", "state\t*ID\nfrozen\t1\n");
        Table("montype", "type\nnone\nundead\n");
        Table("itemstatcost", "Stat\tid\nstrength\t0\n");
        Table("itemtypes", "ItemType\tCode\nArmor\tarmo\n");
        Table("armor", "name\tcode\nCap\tcap\n");
        Table("misc", "name\tcode\nGold\tgld\nAmulet\tamu\n");
        Table("uniqueitems", "index\tcode\nSpecial Amulet\tamu\n");
        Table("setitems", "index\titem\nSet Amulet\tamu\n");
        Table("treasureclassex", "Treasure Class\tItem1\nNested\tNested\n");
        Table("automagic", "Name\tgroup\nFirst\t5\nSecond\t5\n");
        Table("levels", "Name\tId\nTown\t1\n");
        Table("monstats", "Id\nSkeleton\nZombie\n");
        Table("playerclass", "Player Class\tCode\nAmazon\tama\nSorceress\tsor\n");
        var index = new CellReferenceIndex();
        CellReferenceResult Resolve(string table, string column, string value, Dictionary<string, string>? fields = null) =>
            index.Resolve(project, CellReferences.Rule(table, column)!, value, source: fields);
        check(Resolve("skills", "srvmissilea", "firebolt").Hits.Single().Column == "Missile", "Lettered missile alias navigates by name");
        check(Resolve("runes", "Rune6", "amu").Hits.Single().Value == "amu", "Numbered runeword family resolves item codes");
        check(Resolve("automagic", "mod1code", "skill").Hits.Single().Column == "code", "Property codes open Properties without requiring optional PropertyGroups");
        check(Resolve("automagic", "mod1param", "36", new() { ["mod1code"] = "skill" }).Hits.Single().Column == "*Id", "Skill property numeric parameter uses ID metadata, not physical row");
        check(Resolve("gems", "weaponMod2Param", "Fire Bolt", new() { ["weaponMod2Code"] = "charged" }).Hits.Single().Value == "Fire Bolt", "Gem property parameter pairs its prefixed code");
        check(Resolve("setitems", "apar1b", "1", new() { ["aprop1b"] = "state" }).Hits.Single().Column == "*ID", "Set partial bonus resolves state parameter");
        check(Resolve("monprop", "par1 (N)", "1", new() { ["prop1 (N)"] = "att-mon%" }).Hits.Single().Value == "undead", "Difficulty parameter pairs its property and monster type slot");
        check(Resolve("cubemain", "b mod 1 param", "36", new() { ["b mod 1"] = "skill" }).Hits.Single().Value == "36", "Cube secondary output parameter finds its paired property");
        foreach (var property in new[] { "skilltab", "sock", "random", "value" })
            check(Resolve("automagic", "mod1param", "0", new() { ["mod1code"] = property }).Hits.Count == 0, "Literal or enum parameter does not become skill zero: " + property);
        check(Resolve("armor", "auto prefix", "5").Hits.Count == 2, "Grouped affixes retain every destination");
        check(Resolve("levels", "Vis0", "001").Hits.Single().Value == "1", "Numeric level references normalize integer spelling");
        check(Resolve("levels", "Vis0", "0").Hits.Count == 0 && Resolve("levels", "Warp0", "-1").Hits.Count == 0, "Level reference sentinels do not navigate");
        check(Resolve("states", "gfxclass", "1", new() { ["gfxtype"] = "1" }).Hits.Single().Value == "Zombie" &&
            Resolve("states", "gfxclass", "1", new() { ["gfxtype"] = "2" }).Hits.Single().Value == "Sorceress", "Graphics class chooses target from companion type");
        check(Resolve("skills", "calc1", "skill('Fire Bolt'.blvl)+stat('strength'.accr)+missile('firebolt'.len)").Hits.Count == 3, "Formula parser resolves named skill stat and missile operands");
        check(!CellReferences.CanNavigate(CellReferences.Rule("skills", "calc1")!, "par1+lvl*2"), "Ordinary formula literals have no arrow");
        check(Resolve("cubemain", "input 1", "armo,mag,qty=3").Hits.Single().Column == "Code", "Cube modifiers preserve item-type token");
        check(Resolve("cubemain", "output", "Special Amulet,uni").Hits.Single().Value == "Special Amulet", "Cube named unique item resolves correct table");
        check(Resolve("cubemain", "output", "useitem,mod").Hits.Count == 0, "Recipe operations never select an item row");
        check(Resolve("treasureclassex", "Item1", "Nested").Hits.Single().Value == "Nested", "Recursive treasure class opens one direct row without following cycles");
        check(Resolve("treasureclassex", "Item1", "gld,mul=1280").Hits.Single().Value == "gld", "Treasure class modifier is not part of item key");
        check(CellReferences.Rule("skills", "range") == null && CellReferences.Rule("lvlprest", "Dt1Mask") == null && CellReferences.Rule("armor", "component") == null,
            "Enums bitmasks and unverified encodings are excluded from row navigation");
        Resolve("skills", "srvmissile", "firebolt"); int loads = index.TableLoads, builds = index.IndexBuilds;
        for (int i = 0; i < 20; i++) Resolve("skills", "srvmissile", "arrow");
        check(index.TableLoads == loads && index.IndexBuilds == builds, "Repeated reference lookups reuse table parses and key indexes");
        Table("missiles", "Missile\t*ID\nnew missile\t5\n");
        check(Resolve("skills", "srvmissile", "new missile").Hits.Count == 1 && Resolve("skills", "srvmissile", "firebolt").Hits.Count == 0,
            "Disk changes invalidate the cached reference table");
        var hit = Resolve("runes", "Rune1", "amu").Hits.Single();
        var table = TableData.Load(hit.File); table.Records.Insert(0, table.NewRecord(new() { ["name"] = "Inserted", ["code"] = "new" }));
        check(hit.FindRow(table) == 2, "Stable source identity follows a destination after row insertion");
        table.SetCell(2, "code", "changed"); check(hit.FindRow(table) == -1, "Changed target value invalidates a previously found identity");
        var catalogFile = TableData.FileFor(project, "strings", "reference-strings");
        TableData.Write(catalogFile, new(new() { ["schemaVersion"] = 1, ["category"] = "reference-strings", ["locales"] = new System.Text.Json.Nodes.JsonArray("enUS") },
            new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = 1, ["Key"] = "strTown", ["translations"] = new System.Text.Json.Nodes.JsonObject { ["enUS"] = "Town" } })));
        var text = Resolve("levels", "LevelName", "strTown");
        check(text.Hits.Single().CatalogId == "1" && text.Hits[0].Column == "Key", "Localized names link to string catalog identities");
        check(index.Resolve(project, CellReferences.Rule("levels", "LevelName")!, "strTown", pendingTables: new HashSet<string> { "strings" }).Hits.Count == 0,
            "Pending string source cannot resolve through stale disk");
        Table("monstats", "Id\t*hcIdx\nSkeleton\t20\nExpansion\t\nZombie\t21\n");
        check(Resolve("hireling", "Class", "21").Hits.Single().Row == 2, "Monster numeric links use hcIdx across expansion markers");
        var file = Semantics.TableFile(project, "missiles");
        var buffer = TableData.Load(file); buffer.SetCell(0, "Missile", "unsaved");
        var buffers = new Dictionary<string, TableData> { [file] = buffer };
        var missileRule = CellReferences.Rule("skills", "srvmissile")!;
        check(index.Resolve(project, missileRule, "unsaved", buffers).Hits.Count == 1, "Reusable index prefers immutable unsaved snapshots over disk cache");
        var next = TableData.Load(file); next.SetCell(0, "Missile", "next revision"); buffers[file] = next;
        check(index.Resolve(project, missileRule, "next revision", buffers).Hits.Count == 1 && index.Resolve(project, missileRule, "unsaved", buffers).Hits.Count == 0,
            "Replacing an open snapshot invalidates its old key index");
        File.Delete(file);
        check(index.Resolve(project, missileRule, "next revision").Hits.Count == 0 && index.Resolve(project, missileRule, "next revision", buffers).Hits.Count == 1,
            "Deleted disk targets do not reuse cached data while valid open buffers remain navigable");
    }
}
