using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>
/// UI Designer core: the lossless reader of the game's relaxed JSON, minimal text edits, basedOn merging, profile variables,
/// widget geometry, and the edits the designer writes (overrides of inherited widgets, duplicates, deletes, draw order).
/// </summary>
internal static class UiLayoutTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var project = new ModProject(Path.Combine(root, "ui-layouts"), "test", "Test");
        var layouts = Directory.CreateDirectory(Path.Combine(project.Root, "data", "global", "ui", "layouts")).FullName;
        void Write(string name, string text) { var path = Path.Combine(layouts, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, Utf8); }

        // ── Reader ─────────────────────────────────────────────────────────────────────────────────
        var relaxed = "{\r\n    // comment, \"with quotes\"\r\n    \"a\": 1, /* block */ \"b\": [ 1, 2, ],\r\n    \"s\": \"x\\\\y \\\"q\\\"\",\r\n}\r\n";
        var parsed = UiJsonParser.Parse(relaxed);
        check(parsed["a"]!.Number == 1 && parsed["b"]!.Items.Count == 2 && parsed["s"]!.String == "x\\y \"q\"", "UI JSON reads comments, trailing commas and escapes");
        check(relaxed[parsed["b"]!.Start..parsed["b"]!.End] == "[ 1, 2, ]", "UI JSON nodes remember their exact source span");
        throws(() => UiJsonParser.Parse("{ \"a\": 1 \"b\": 2 }"), "UI JSON rejects a missing comma");
        try { UiJsonParser.Parse("{\n  \"a\": [1,\n}"); check(false, "unreachable"); }
        catch (UiJsonException e) { check(e.Line == 3, "UI JSON errors name their line: " + e.Message); }

        // ── Minimal edits ──────────────────────────────────────────────────────────────────────────
        var multi = "{\r\n    \"x\": 1, // keep me\r\n    \"y\": 2,\r\n}";
        var obj = UiJsonParser.Parse(multi);
        check(UiJsonEditor.Apply(multi, UiJsonEditor.SetMember(multi, obj, "x", "5")) == multi.Replace("\"x\": 1", "\"x\": 5"), "Replacing a value touches only its characters");
        var added = UiJsonEditor.Apply(multi, UiJsonEditor.SetMember(multi, obj, "z", "3"));
        check(added == "{\r\n    \"x\": 1, // keep me\r\n    \"y\": 2,\r\n    \"z\": 3,\r\n}", "A new member follows the object's indentation, line endings and trailing commas: " + added);
        var single = "{ \"type\": \"A\", \"name\": \"b\" }";
        check(UiJsonEditor.Apply(single, UiJsonEditor.SetMember(single, UiJsonParser.Parse(single), "x", "1")) == "{ \"type\": \"A\", \"name\": \"b\", \"x\": 1 }", "A one-line object stays on one line");
        var commented = "{\n  \"a\": 1 // last\n}";
        check(UiJsonEditor.Apply(commented, UiJsonEditor.SetMember(commented, UiJsonParser.Parse(commented), "b", "2")) == "{\n  \"a\": 1, // last\n  \"b\": 2\n}", "A comment after the last member stays on its line");
        var removed = UiJsonEditor.Apply(multi, [UiJsonEditor.RemoveMember(multi, obj, "y")]);
        check(removed == "{\r\n    \"x\": 1, // keep me\r\n}", "Removing a member takes its whole line: " + removed);
        check(UiJsonEditor.Literal(System.Text.Json.Nodes.JsonNode.Parse("{\"x\":1,\"y\":0.5,\"s\":\"a\\\\b\"}")) == "{ \"x\": 1, \"y\": 0.5, \"s\": \"a\\\\b\" }", "Literals are written in the game files' one-line style");

        // ── Resolving: profile, basedOn, geometry ──────────────────────────────────────────────────
        Write("_profilehd.json", """
            {
                // Named globals.
                "RightPanelAnchor": { "x": 1.0, "y": 0.5 },
                "RightPanelRect": { "x": -1200, "y": -700, "width": 1162, "height": 1400 },
                "FontColorGold": { "r": 199, "g": 179, "b": 119, "a": 255 },
                "TitleColor": "$FontColorGold",
                "StyleTitle": { "fontColor": "$TitleColor", "pointSize": 38, "alignment": { "h": "center", "v": "center" } },
            }
            """);
        Write("_profilelv.json", "{ \"basedOn\": \"HD\", \"RightPanelRect\": { \"x\": -1300, \"y\": -800, \"width\": 1162, \"height\": 1400, \"scale\": 1.16 } }");
        Write("basepanelhd.json", """
            {
                "type": "TestPanel", "name": "Base",
                "fields": { "rect": "$RightPanelRect", "anchor": "$RightPanelAnchor" },
                "children": [
                    { "type": "ImageWidget", "name": "background", "fields": { "filename": "PANEL\\Test\\Background" } },
                    { "type": "TextBoxWidget", "name": "title", "fields": { "rect": { "x": 100, "y": 50, "width": 900, "height": 70 }, "style": "$StyleTitle", "text": "@strTitle" } },
                    { "type": "Widget", "name": "group", "fields": { "rect": { "x": 10, "y": 20, "width": 200, "height": 100, "scale": 2 } },
                      "children": [ { "type": "ButtonWidget", "name": "ok", "fields": { "anchor": { "x": 0.5 }, "rect": { "x": 5, "y": 6 }, "filename": "PANEL\\ok" } } ] },
                ]
            }
            """);
        // A children list replaces the parent's: the parent's widgets it keeps are listed by type and name.
        var derivedText = "{\n    \"basedOn\": \"BasePanelHD.json\",\n    \"type\": \"TestPanel\", \"name\": \"Derived\",\n    \"children\": [\n        { \"type\": \"ImageWidget\", \"name\": \"background\" },\n        {\n            \"type\": \"TextBoxWidget\", \"name\": \"title\",\n            \"fields\": {\n                \"text\": \"Hello\",\n            },\n        },\n        { \"type\": \"Widget\", \"name\": \"group\" },\n        {\n            \"type\": \"ImageWidget\", \"name\": \"extra\",\n            \"fields\": { \"rect\": { \"x\": 1, \"y\": 2 } },\n        },\n    ]\n}\n";
        Write("derivedpanelhd.json", derivedText);
        var derivedPath = Path.Combine(layouts, "derivedpanelhd.json");
        var sources = new UiLayoutSources(project, [], derivedPath);
        UiScene Resolve(string text, UiLayoutMode? mode = null) => UiLayout.Resolve(new UiLayoutSources(project, [], derivedPath), derivedPath, text, mode ?? UiLayoutMode.PcHd, UiScreen.All[0], f => f.Contains("Background") ? (1162, 1400) : f.Contains("ok") ? (50, 40) : null);

        check(UiLayoutSources.IsLayout(derivedPath) && !UiLayoutSources.IsLayout(Path.Combine(project.Root, "source", "tables", "x.json")), "Layouts are recognised by their global/ui/layouts folder");
        check(UiLayoutMode.For("derivedpanelhd.json") == UiLayoutMode.PcHd && UiLayoutMode.For("controller/xhd.json") == UiLayoutMode.ControllerHd && UiLayoutMode.For("hudpanel.json") == UiLayoutMode.Sd, "A layout's folder and name pick its default profile");
        var scene = Resolve(derivedText);
        check(scene.Issues.Count == 0, "A based-on layout resolves cleanly: " + string.Join(" | ", scene.Issues));
        check(scene.Root.Name == "Derived" && scene.Root.Children.Select(c => c.Name).SequenceEqual(["background", "title", "group", "extra"]) && scene.Find("Derived/group/ok") != null && scene.Find("Derived/background")!.String("filename") == "PANEL\\Test\\Background",
            "Entries named like the parent's widgets take them on whole (fields and children)");
        var partial = "{ \"basedOn\": \"BasePanelHD.json\", \"type\": \"TestPanel\", \"name\": \"Derived\", \"children\": [ { \"type\": \"Widget\", \"name\": \"group\" }, { \"type\": \"TextBoxWidget\", \"name\": \"title\" } ] }";
        check(Resolve(partial).Root.Children.Select(c => c.Name).SequenceEqual(["group", "title"]), "A children list replaces the parent's: widgets it leaves out are dropped, and its order wins (as controller layouts drop close buttons)");
        var noList = "{\n    \"basedOn\": \"BasePanelHD.json\",\n    \"type\": \"TestPanel\", \"name\": \"Derived\",\n    \"fields\": {\n        \"anchor\": { \"x\": 1.0, \"y\": 0.5 },\n    },\n}\n";
        check(Resolve(noList).Root.Children.Select(c => c.Name).SequenceEqual(["background", "title", "group"]), "A file with no children list keeps the parent's children");
        var title = scene.Find("Derived/title")!;
        check(title.String("text") == "Hello" && title.Fields["text"].InDocument && !title.Fields["rect"].InDocument && title.Inherited && title.InDocument, "Merged fields remember which file wrote them");
        check(UiLayout.Color((title.Value("style") as System.Text.Json.Nodes.JsonObject)?["fontColor"]) == (199, 179, 119, 255), "Profile $variables resolve through other variables inside objects");
        check(scene.Root.Fields["rect"].Variable == "$RightPanelRect" && scene.Root.Fields["rect"].VariableEntry?.File.Name == "_profilehd.json", "A field that is a $variable names the profile entry");
        check(scene.Root.X == 3840 - 1200 && scene.Root.Y == 1080 - 700 && scene.Root.Bounds.Width == 1162, "The root sits at its screen anchor plus its rect offset");
        check(title.X == scene.Root.X + 100 && title.Y == scene.Root.Y + 50, "Children are placed from their parent's top left");
        var ok = scene.Find("Derived/group/ok")!; var group = scene.Find("Derived/group")!;
        check(group.Scale == 2 && group.Bounds.Width == 400 && ok.Scale == 2 && ok.X == group.X + (0.5 * 200 + 5) * 2 && ok.Bounds.Width == 100, "Anchors are fractions of the parent; offsets and sizes follow the parent's scale");
        check(scene.Find("Derived/background")!.Bounds.Height == 1400, "A picture with no size of its own takes its sprite's size");
        var large = Resolve(derivedText, UiLayoutMode.PcLargeFont);
        check(large.Root.Scale == 1.16 && large.Root.X == 3840 - 1300 && large.ProfileFiles.Select(f => f.Name).SequenceEqual(["_profilehd.json", "_profilelv.json"]), "The large-font profile is based on HD and overrides its variables");
        var wide = UiLayout.Resolve(sources, derivedPath, derivedText, UiLayoutMode.PcHd, UiScreen.All.First(s => s.Label == "21:9"), null);
        check(wide.Width == 5040 && wide.Root.X == 5040 - 1200, "A wider screen moves right-anchored panels with its edge");
        check(scene.AtOffset(derivedText.IndexOf("\"extra\"", StringComparison.Ordinal))?.Name == "extra", "A source offset finds the widget written there");
        var broken = Resolve(derivedText.Replace("\"Hello\"", "\"$NoSuchVariable\""));
        check(broken.Issues.Any(i => i.Contains("$NoSuchVariable")), "An undefined $variable is reported");

        // ── Designer edits ─────────────────────────────────────────────────────────────────────────
        // Moving a widget this file writes patches its rect members in place.
        var extra = scene.Find("Derived/extra")!;
        var moved = UiLayoutEdit.SetMembers(derivedText, extra, "rect", [("x", 40), ("y", 60)]);
        check(moved == derivedText.Replace("{ \"x\": 1, \"y\": 2 }", "{ \"x\": 40, \"y\": 60 }"), "Moving a widget rewrites only its rect numbers");
        var sized = UiLayoutEdit.SetMembers(derivedText, extra, "rect", [("width", 300), ("height", 20)]);
        check(sized.Contains("{ \"x\": 1, \"y\": 2, \"width\": 300, \"height\": 20 }"), "Resizing adds the missing members to the same rect: " + sized);
        // Moving an inherited widget the file lists without a rect writes the rect into its fields.
        var titleMoved = UiLayoutEdit.SetMembers(derivedText, title, "rect", [("x", 110)]);
        var titleScene = Resolve(titleMoved);
        check(titleScene.Find("Derived/title")!.Fields["rect"].InDocument && titleScene.Find("Derived/title")!.X == scene.Root.X + 110 && titleScene.Find("Derived/title")!.Bounds.Width == 900,
            "Moving an inherited widget writes its whole rect here, keeping the parent file's size");
        check(titleMoved.Contains("\"text\": \"Hello\",\n                \"rect\": { \"x\": 110, \"y\": 50, \"width\": 900, \"height\": 70 },"), "The override goes into the widget's fields in the file's style: " + titleMoved);
        // A widget the file does not list at all gets an entry, and so do its parents.
        var okMoved = UiLayoutEdit.SetMembers(derivedText, ok, "rect", [("y", 9)]);
        var okScene = Resolve(okMoved);
        check(okScene.Issues.Count == 0 && okScene.Find("Derived/group/ok")!.Fields["rect"].InDocument && okScene.Find("Derived/group")!.InDocument && okScene.Find("Derived/group")!.Bounds.Width == 400,
            "Overriding a nested inherited widget gives its parent's entry a children list naming it");
        check(okScene.Root.Children.Select(c => c.Name).SequenceEqual(["background", "title", "group", "extra"]) && okScene.Find("Derived/group")!.Children.Count == 1, "Override entries take on the parent's widgets rather than adding new ones");
        // In a file that keeps the parent's children, the first override writes the whole list, or the others would disappear.
        var noListScene = Resolve(noList);
        var noListMoved = UiLayoutEdit.SetMembers(noList, noListScene.Find("Derived/title")!, "rect", [("x", 120)]);
        var noListMovedScene = Resolve(noListMoved);
        check(noListMovedScene.Root.Children.Select(c => c.Name).SequenceEqual(["background", "title", "group"]) && noListMovedScene.Find("Derived/title")!.X == noListScene.Root.X + 120 && noListMovedScene.Find("Derived/group/ok") != null,
            "Overriding a widget in a file without a children list lists every inherited child, so none is dropped");
        check(noListMoved.Contains("    \"children\": [\n        { \"type\": \"ImageWidget\", \"name\": \"background\" },\n"), "The list is written one entry per line in the file's style: " + noListMoved);
        // The root's rect is a profile variable: moving it writes a literal here, leaving the profile alone.
        var rootMoved = UiLayoutEdit.SetMembers(derivedText, scene.Root, "rect", [("x", -1000)]);
        var rootScene = Resolve(rootMoved);
        check(rootScene.Root.X == 3840 - 1000 && rootScene.Root.Fields["rect"].Variable == null && File.ReadAllText(Path.Combine(layouts, "_profilehd.json")).Contains("\"x\": -1200"), "Moving a panel whose rect is a $variable writes its own rect and leaves the profile unchanged");
        // Removing an override brings the parent's value back.
        var reverted = UiLayoutEdit.RemoveField(titleMoved, titleScene.Find("Derived/title")!, "rect");
        check(Resolve(reverted).Find("Derived/title")!.X == scene.Root.X + 100, "Removing this file's value restores the inherited one");
        // Duplicate, delete, reorder, add.
        var duplicated = UiLayoutEdit.Duplicate(derivedText, extra, UiLayoutEdit.FreeName(scene.Root, "extra_copy"));
        var dupScene = Resolve(duplicated);
        check(dupScene.Root.Children.Select(c => c.Name).SequenceEqual(["background", "title", "group", "extra", "extra_copy"]) && dupScene.Find("Derived/extra_copy")!.X == extra.X, "Duplicating copies a widget beside it under a new name");
        check(duplicated.Contains("        {\n            \"type\": \"ImageWidget\", \"name\": \"extra_copy\",\n            \"fields\": { \"rect\": { \"x\": 1, \"y\": 2 } },\n        },"), "The copy is written with the original's layout: " + duplicated);
        var dupInherited = UiLayoutEdit.Duplicate(derivedText, ok, "ok_copy");
        var dupInheritedScene = Resolve(dupInherited);
        check(dupInheritedScene.Find("Derived/group/ok_copy") is { } okCopy && okCopy.X == ok.X && okCopy.Fields["anchor"].InDocument, "Duplicating an inherited widget writes out its merged fields");
        var baseText = File.ReadAllText(Path.Combine(layouts, "basepanelhd.json"));
        check(Resolve(UiLayoutEdit.Delete(derivedText, title)).Find("Derived/title") == null && File.ReadAllText(Path.Combine(layouts, "basepanelhd.json")) == baseText,
            "Deleting an inherited widget leaves it out of this layout; the parent layout keeps it");
        check(Resolve(UiLayoutEdit.Delete(noList, noListScene.Find("Derived/background")!)).Root.Children.Select(c => c.Name).SequenceEqual(["title", "group"]), "Deleting from a file without a children list keeps the other inherited children");
        check(Resolve(UiLayoutEdit.Delete(duplicated, dupScene.Find("Derived/extra_copy")!)).Root.Children.Count == 4 && UiLayoutEdit.Delete(duplicated, dupScene.Find("Derived/extra_copy")!) == derivedText, "Deleting an added widget removes exactly its text");
        var reordered = UiLayoutEdit.Move(derivedText, extra, -1);
        check(UiJsonParser.Parse(reordered)["children"]!.Items[2]["name"]!.String == "extra" && Resolve(reordered).Root.Children[2].Name == "extra", "Draw order swaps a widget with its sibling in this file");
        var withChild = UiLayoutEdit.AddChild(derivedText, extra, "TextBoxWidget", "label", [("text", "\"Hi\"")]);
        check(Resolve(withChild).Find("Derived/extra/label")?.String("text") == "Hi", "A child can be added to a widget with no children yet");
        var renamed = UiLayoutEdit.Rename(derivedText, extra, "bonus");
        check(Resolve(renamed).Find("Derived/bonus") != null, "A widget this file adds can be renamed");
        throws(() => UiLayoutEdit.Rename(derivedText, title, "heading"), "An inherited widget cannot be renamed");
        check(UiLayoutEdit.FreeName(scene.Root, "title") == "title_2", "New names avoid siblings' names");

        // ── Pages ──────────────────────────────────────────────────────────────────────────────────
        // A stash-like panel: a tab bar whose tabs name the containers beside it, and one background per tab.
        var stashText = """
            {
                "type": "BankPanel", "name": "Stash",
                "fields": { "rect": { "x": 0, "y": 0, "width": 1162, "height": 1507 }, "backgroundFile": [ "PANEL\\A", "PANEL\\B", "PANEL\\C" ] },
                "children": [
                    { "type": "ImageWidget", "name": "background", "fields": { "filename": "PANEL\\A" } },
                    { "type": "TabBarWidget", "name": "BankTabs", "fields": { "rect": { "x": 82, "y": 146 }, "tabCount": 3, "tabSize": { "x": 195, "y": 72 }, "tabPadding": { "x": 5, "y": 0 }, "textStrings": [ "@personal", "@shared", "@gems" ] } },
                    { "type": "Widget", "name": "PreviousSeasonToggleDisplay", "children": [ { "type": "TextBoxWidget", "name": "label", "fields": { "text": "x" } } ] },
                    { "type": "Widget", "name": "basicstash_container", "children": [
                        { "type": "InventoryGridWidget", "name": "grid", "fields": { "rect": { "x": 89, "y": 219 }, "cellCount": { "x": 10, "y": 10 }, "cellSize": { "x": 98, "y": 98 } } },
                        { "type": "Widget", "name": "SharedStashTabContainer", "children": [ { "type": "ButtonWidget", "name": "next", "fields": { "rect": { "x": 1, "y": 1, "width": 10, "height": 10 } } } ] } ] },
                    { "type": "Widget", "name": "advancedstash_gems", "children": [ { "type": "AdvancedStashSlotWidget", "name": "gcw", "fields": { "rect": { "x": 164, "y": 232, "width": 98, "height": 98 } } } ] },
                ]
            }
            """;
        Write("teststashhd.json", stashText);
        var stashPath = Path.Combine(layouts, "teststashhd.json");
        var stash = UiLayout.Resolve(new UiLayoutSources(project, [], stashPath), stashPath, stashText, UiLayoutMode.PcHd, UiScreen.All[0], null);
        check(stash.Find("Stash/BankTabs")!.Bounds is { Width: 595, Height: 72 }, "A tab bar is as wide as its tabs and the gaps between them");
        var stashPages = UiPages.Find(stash, key => key == "personal" ? "Personal" : null);
        check(stashPages.Count == 1 && stashPages[0].Preferred && stashPages[0].Pages.Select(p => p.Label).SequenceEqual(["Personal", "shared", "gems"]), "A tab bar naming the containers beside it makes one page per tab, labelled with its localized tab name");
        var (personal, shared, gems) = (stashPages[0].Pages[0], stashPages[0].Pages[1], stashPages[0].Pages[2]);
        check(personal.Hidden.SetEquals(["Stash/basicstash_container/SharedStashTabContainer", "Stash/advancedstash_gems"]) && shared.Hidden.SetEquals(["Stash/advancedstash_gems"]) && gems.Hidden.SetEquals(["Stash/basicstash_container", "Stash/basicstash_container/SharedStashTabContainer"]),
            "Each stash tab hides the other tabs' containers: the shared navigation shows only on Shared, containers named for no tab always show");
        check(personal.Sprites["Stash/background"] == "PANEL\\A" && gems.Sprites["Stash/background"] == "PANEL\\C" && gems.TabBar == "Stash/BankTabs" && gems.Tab == 2, "Each tab shows the panel's background for it and selects its tab");
        var overlapping = """
            { "type": "Panel", "name": "Quest", "children": [
                { "type": "Widget", "name": "Tab0", "children": [ { "type": "ImageWidget", "name": "a", "fields": { "rect": { "x": 10, "y": 10, "width": 300, "height": 300 } } } ] },
                { "type": "Widget", "name": "Tab1", "children": [ { "type": "ImageWidget", "name": "b", "fields": { "rect": { "x": 20, "y": 20, "width": 300, "height": 300 } } } ] },
                { "type": "TableWidget", "name": "Options", "children": [
                    { "type": "TableRowWidget", "name": "Row A", "children": [ { "type": "TextBoxWidget", "name": "t", "fields": { "rect": { "width": 100, "height": 40 }, "text": "a" } } ] },
                    { "type": "TableRowWidget", "name": "Row B", "children": [ { "type": "TextBoxWidget", "name": "t", "fields": { "rect": { "width": 100, "height": 40 }, "text": "b" } } ] } ] }
            ] }
            """;
        var overlapScene = UiLayout.Resolve(new UiLayoutSources(project, [], stashPath), stashPath, overlapping, UiLayoutMode.PcHd, UiScreen.All[0], null);
        var overlapPages = UiPages.Find(overlapScene);
        check(overlapPages.Count == 1 && !overlapPages[0].Preferred && overlapPages[0].Pages.Select(p => p.Label).SequenceEqual(["Tab0", "Tab1"]) && overlapPages[0].Pages[0].Hidden.SetEquals(["Quest/Tab1"]),
            "Sibling containers covering the same area are offered as pages, opt-in; table rows placed at runtime are not");
        var table = """
            { "type": "Panel", "name": "Opts", "children": [
                { "type": "TableWidget", "name": "T", "fields": { "rect": { "x": 70, "y": 150 }, "rowHeight": 70, "cellPadding": { "top": 5 },
                    "columns": [ { "width": 1000, "alignment": { "h": "fit", "v": "fit" } }, { "width": 700, "alignment": { "h": "center", "v": "center" } } ] },
                  "children": [
                    { "type": "TableRowWidget", "name": "Row", "children": [ { "type": "TextBoxWidget", "name": "a", "fields": { "text": "a" } } ] },
                    { "type": "TableRowWidget", "name": "Row", "children": [
                        { "type": "TextBoxWidget", "name": "b", "fields": { "rect": { "x": 40 }, "text": "b" } },
                        { "type": "ButtonWidget", "name": "c", "fields": { "rect": { "width": 100, "height": 40 } } } ] } ] }
            ] }
            """;
        var tableScene = UiLayout.Resolve(new UiLayoutSources(project, [], stashPath), stashPath, table, UiLayoutMode.PcHd, UiScreen.All[0], null);
        check(tableScene.Find("Opts/T/Row/a")!.Bounds == new UiRect(70, 155, 1000, 65) && tableScene.Find("Opts/T/Row/b")!.Bounds == new UiRect(110, 225, 960, 65),
            "Table rows sit rowHeight apart inside cellPadding, and a fit column stretches its widget over the cell less its offset");
        check(tableScene.Find("Opts/T/Row/c")!.Bounds == new UiRect(70 + 1000 + 300, 225 + 12.5, 100, 40) && tableScene.Find("Opts/T")!.Bounds is { Width: 1700, Height: 140 },
            "A row's second widget goes in the second column, centred there; the table is as big as its rows");

        // ── Assets ─────────────────────────────────────────────────────────────────────────────────
        var sprites = Directory.CreateDirectory(Path.Combine(project.Root, "data", "hd", "global", "ui", "panel", "test")).FullName;
        // A 195 wide atlas of four 48 pixel frames: frames start at floor(i × 195 / 4).
        var sprite = new byte[40 + 195 * 2 * 4];
        BitConverter.GetBytes(0x31417053).CopyTo(sprite, 0); BitConverter.GetBytes((ushort)31).CopyTo(sprite, 4); BitConverter.GetBytes((ushort)48).CopyTo(sprite, 6);
        BitConverter.GetBytes(195).CopyTo(sprite, 8); BitConverter.GetBytes(2).CopyTo(sprite, 12); BitConverter.GetBytes(4).CopyTo(sprite, 20);
        for (int x = 0; x < 195; x++) for (int y = 0; y < 2; y++) sprite[40 + (y * 195 + x) * 4] = (byte)x;
        File.WriteAllBytes(Path.Combine(sprites, "background.sprite"), sprite);
        var assets = new UiAssets(project, [], derivedPath);
        var info = assets.Sprite("PANEL\\Test\\Background");
        check(info is { FrameWidth: 48, Height: 2, Frames: 4, InProject: true, LowEnd: false }, "UI sprites are found from a layout's filename, case and slashes aside");
        check(UiAssets.Decode(info!, 3).Rgba[0] == 146 && UiAssets.Decode(info!, 2).Rgba[0] == 97, "Frames of an uneven atlas are cut at floor(i × width / frames)");
        File.WriteAllBytes(Path.Combine(sprites, "small.lowend.sprite"), sprite);
        check(assets.Sprite("panel/test/small") is { LowEnd: true, Width: 96 }, "A low-end sprite stands in at twice its size");
        check(assets.Sprite("PANEL\\Test\\Missing") == null && assets.Sprite("$Variable") == null, "Missing sprites and unresolved variables are not found");
        var catalogs = Directory.CreateDirectory(Path.Combine(project.Root, "source", "strings")).FullName;
        File.WriteAllText(Path.Combine(catalogs, "ui.json"), "{ \"schema\": {}, \"records\": [ { \"Key\": \"strTitle\", \"translations\": { \"enUS\": \"ÿc4Inventory\" } } ] }");
        check(new UiAssets(project, [], derivedPath).Localize("strtitle") == "Inventory", "@strings come from the project's catalogs, any case, colour codes dropped");

        // ── Real data (MODSTUDIO_GAME_DATA): every vanilla layout resolves, and a no-op edit round-trips ──
        if (Environment.GetEnvironmentVariable("MODSTUDIO_GAME_DATA") is { Length: > 0 } game && HdAppearance.LocateFolder(game, "data/global/ui/layouts") is { } vanilla)
        {
            int count = 0, sprited = 0, found = 0; var failures = new List<string>(); var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var gameAssets = new UiAssets(null, [game]);
            foreach (var file in Directory.EnumerateFiles(vanilla, "*.json", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith('_')))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var relative = UiLayoutSources.RelativeName(file);
                    var resolved = UiLayout.Resolve(new UiLayoutSources(null, [game], file), file, text, UiLayoutMode.For(relative), UiScreen.All[0], f => gameAssets.Sprite(f) is { } s ? (s.Width, s.DrawHeight) : null);
                    // Legacy (SD) layouts draw DC6 art, which the designer outlines instead of drawing.
                    if (!resolved.Mode.Legacy)
                        foreach (var w in resolved.Widgets) if (UiLayout.SpriteField(w) is { } f && w.String(f) is { } name && !name.StartsWith('$')) { sprited++; if (gameAssets.Sprite(name) != null) found++; else missing.Add(name); }
                    // A no-op edit on the first widget with a literal rect writes back the same text.
                    if (resolved.Widgets.FirstOrDefault(w => w.Fields.TryGetValue("rect", out var r) && r.InDocument && r.Raw.IsObject && r.Raw.Members.Any(m => m.Key == "x" && m.Value.Kind == UiJsonKind.Number)) is { } literal)
                    {
                        var x = literal.Fields["rect"].Raw["x"]!.Number!.Value;
                        if (UiLayoutEdit.SetMembers(text, literal, "rect", [("x", x)]) != text && literal.Fields["rect"].Raw["x"]!.Text == UiJsonEditor.Number(x)) failures.Add($"{relative}: no-op edit changed the text");
                    }
                    count++;
                }
                catch (Exception e) { failures.Add($"{Path.GetFileName(file)}: {e.Message}"); }
            }
            check(failures.Count == 0, $"Every vanilla layout resolves ({count}): " + string.Join(" | ", failures.Take(5)));
            foreach (var bank in new[] { "bankexpansionlayouthd.json", "controller/bankexpansionlayouthd.json" })
            {
                var file = Path.Combine(vanilla, bank.Replace('/', Path.DirectorySeparatorChar));
                var bankScene = UiLayout.Resolve(new UiLayoutSources(null, [game], file), file, File.ReadAllText(file), UiLayoutMode.For(bank), UiScreen.All[0], f => gameAssets.Sprite(f) is { } s ? (s.Width, s.DrawHeight) : null);
                var tabs = UiPages.Find(bankScene, k => gameAssets.Localize(k)).SingleOrDefault(s => s.Preferred);
                check(tabs != null && tabs.Pages.Select(p => p.Label).SequenceEqual(["Personal", "Shared", "Gems", "Materials", "Runes"]) && tabs.Pages.All(p => p.Sprites.Count == 1 && gameAssets.Sprite(p.Sprites.Values.Single()) != null)
                    && bankScene.Root.Children.All(c => c.Name is not ("grid" or "gold_amount")), $"The vanilla {bank} has its five tabs, each with its background, and no classic stash grid");
            }
            check(found >= sprited * 0.95, $"Vanilla HD layouts find their sprites ({found} of {sprited}; missing {string.Join(", ", missing.Take(12))})");
        }
    }
}
