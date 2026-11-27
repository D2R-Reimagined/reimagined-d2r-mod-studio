using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

/// <summary>Profile inheritance, the editor's profile overlay, in-grid profile edits and the profile manager.</summary>
internal static class ProfileTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws, Action<string, string> write)
    {
        var native = Path.Combine(root, "profiles-native");
        write(Path.Combine(native, "global/excel/rows.txt"), "name\tvalue\tenabled\r\nOne\t1\t1\r\nTwo\t2\t1\r\nThree\t3\t1\r\n");
        var project = ProjectImporter.Import(native, Path.Combine(root, "profiles-project"), "Profiles").Project;
        var tableFile = TableData.FileFor(project, "tables", "rows"); var table = TableData.Load(tableFile);
        string Built(string profile) => File.ReadAllText(Inside(BuildService.Build(project, profile).Output, "Profiles.mpq/data/global/excel/rows.txt"));
        JsonObject Settings(string profile) => (JsonObject)Read(ProfileChain.SettingsPath(project.Root, profile));
        void Save(string profile, JsonObject settings) => WriteJson(ProfileChain.SettingsPath(project.Root, profile), settings);

        // Defaults: the importer writes each profile's runtime; older profiles without one keep the name-based behaviour.
        check(Settings("d2rl").S("runtime") == "loader" && Settings("standard").S("runtime") == "plain", "New projects write each profile's runtime");
        var legacy = Settings("d2rl"); legacy.Remove("runtime"); legacy.Remove("stringMode"); Save("d2rl", legacy);
        var d2rl = ProfileChain.Load(project.Root, "d2rl");
        check(d2rl.Runtime == "loader" && d2rl.StringMode == "full", "A d2rl profile without runtime or string mode keeps the loader runtime and full strings");

        // Profile manager: create, validate names.
        throws(() => ProfileManager.Create(project, "Ladder"), "Profile names are lowercase");
        throws(() => ProfileManager.Create(project, "d2rl"), "A profile name cannot be reused");
        throws(() => ProfileManager.Create(project, "ladder", basedOn: "missing"), "A new profile's parent must exist");
        ProfileManager.Create(project, "ladder", basedOn: "d2rl", color: "#3366CC");
        check(project.Profiles.Contains("ladder") && Settings("ladder").S("basedOn") == "d2rl", "A profile can be created based on another");
        var ladder = ProfileChain.Load(project.Root, "ladder");
        check(ladder.Layers.Select(l => l.Id).SequenceEqual(["d2rl", "ladder"]) && ladder.Runtime == "loader" && ladder.StringMode == "full" && ladder.Color == "#3366CC", "A child inherits its parent's runtime and string mode");

        // Inheritance in the build: the parent changes value of row 0; the child changes it again and another cell.
        var shared = Built("d2rl");
        var ladderOverlay = ProfileOverlay.Compute(project, table, "ladder");
        check(ladderOverlay.CellCount == 0 && ladderOverlay.Issues.Count == 0, "An empty child overlay has nothing to show");
        throws(() => ProfileEditing.Apply(project, table, "d2rl", [new SetProfileCell(0, "value", "10")], ""), "A new profile change needs a reason");
        var parentWrite = ProfileEditing.Apply(project, table, "d2rl", [new SetProfileCell(0, "value", "10")], "Loader balance");
        var studioSet = Inside(project.Root, "compatibility/d2rl/" + ProfileEditing.StudioReference("rows"));
        check(File.Exists(studioSet) && Settings("d2rl")["tableOverrides"]!.AsArray().Any(r => r!.GetValue<string>() == ProfileEditing.StudioReference("rows")), "A grid edit writes a Studio rule set per table and names it in profile.json");
        check(Built("d2rl").Contains("One\t10\t1") && Built("standard").Contains("One\t1\t1"), "A profile edit only changes that profile's build");
        check(Built("ladder").Contains("One\t10\t1"), "A child builds its parent's changes");

        ProfileEditing.Apply(project, table, "ladder", [new SetProfileCell(0, "value", "100"), new SetProfileCell(1, "enabled", "0")], "Ladder season");
        check(Built("ladder") == "name\tvalue\tenabled\r\nOne\t100\t1\r\nTwo\t2\t0\r\nThree\t3\t1\r\n", "A child's change applies on top of its parent's");
        var childRule = Read(Inside(project.Root, "compatibility/ladder/" + ProfileEditing.StudioReference("rows")))["rules"]!.AsArray();
        check(childRule.Any(r => r!["changes"]?["value"]?.S("expect") == "10"), "A child's change expects its parent's value, not the shared one");
        ladderOverlay = ProfileOverlay.Compute(project, table, "ladder");
        var cell = ladderOverlay.Cell(0, "value")!;
        check(cell.Layer == "ladder" && cell.Value == "100" && cell.Below?.Layer == "d2rl" && ladderOverlay.Inherited(0, "value") == "10" && ladderOverlay.Own(1, "enabled") != null, "The overlay shows each layer's change and what the child inherits");

        // A parent change underneath a child's change makes it stale; accepting the new value clears it.
        ProfileEditing.Apply(project, table, "d2rl", [new SetProfileCell(0, "value", "11")], "Loader balance");
        ladderOverlay = ProfileOverlay.Compute(project, table, "ladder");
        check(ladderOverlay.Cell(0, "value")!.Stale && ladderOverlay.StructuralIssues.Count() == 0, "A parent change under a child's change shows the child's change as stale");
        throws(() => Built("ladder"), "A stale child change blocks its build");
        ProfileEditing.Apply(project, table, "ladder", [new AcceptSharedValue(0, "value")], "");
        check(Built("ladder").Contains("One\t100\t1"), "Accepting the inherited value keeps the child's value and clears the stale change");

        // Setting the value a profile would get anyway drops its change; reverting does too.
        ProfileEditing.Apply(project, table, "ladder", [new SetProfileCell(0, "value", "11")], "");
        check(ProfileOverlay.Compute(project, table, "ladder").Own(0, "value") == null, "Typing the inherited value back removes the child's change");
        throws(() => ProfileEditing.Apply(project, table, "ladder", [new RevertProfileCell(0, "value")], ""), "A child cannot revert its parent's change");
        ProfileEditing.Apply(project, table, "ladder", [new RevertProfileCell(1, "enabled")], "");
        check(!File.Exists(Inside(project.Root, "compatibility/ladder/" + ProfileEditing.StudioReference("rows"))) && Settings("ladder")["tableOverrides"]!.AsArray().Count == 0, "Reverting a profile's last change removes its Studio rule set and reference");

        // Row removal: only the last rows; restore puts them back; changes on a removed row go.
        throws(() => ProfileEditing.Apply(project, table, "ladder", [new RemoveProfileRow(1)], "Not on ladder"), "A profile cannot leave out a row that kept rows follow");
        ProfileEditing.Apply(project, table, "ladder", [new SetProfileCell(2, "value", "33")], "Ladder season");
        var removal = ProfileEditing.Apply(project, table, "ladder", [new RemoveProfileRow(2)], "Not on ladder");
        check(Built("ladder") == "name\tvalue\tenabled\r\nOne\t11\t1\r\nTwo\t2\t1\r\n" && Built("d2rl").Contains("Three"), "Leaving out the last row drops it from the profile's build and its changes with it");
        throws(() => ProfileEditing.Apply(project, table, "ladder", [new SetProfileCell(2, "value", "4")], "x"), "A removed row's cells cannot be changed");
        ProfileEditing.Undo(removal);
        check(Built("ladder").Contains("Three\t33\t1"), "Undo restores a row and the changes it had");
        ProfileEditing.Apply(project, table, "ladder", [new RemoveProfileRow(2)], "Not on ladder");
        ProfileEditing.Apply(project, table, "ladder", [new RestoreProfileRow(2)], "");
        check(Built("ladder").Contains("Three\t3\t1"), "Restore puts a removed row back");
        ProfileEditing.Apply(project, table, "d2rl", [new RemoveProfileRow(2)], "Loader only");
        throws(() => ProfileEditing.Apply(project, table, "ladder", [new RestoreProfileRow(2)], ""), "A child cannot restore a row its parent leaves out");
        check(!Built("ladder").Contains("Three"), "A child leaves out what its parent leaves out");
        ProfileEditing.Apply(project, table, "d2rl", [new RestoreProfileRow(2)], "");

        // Undo refuses to overwrite a file changed after the edit.
        var undoable = ProfileEditing.Apply(project, table, "ladder", [new SetProfileCell(1, "value", "22")], "Ladder season");
        var ladderSet = Inside(project.Root, "compatibility/ladder/" + ProfileEditing.StudioReference("rows"));
        var edited = Read(ladderSet); edited["rules"]![0]!["reason"] = "Changed outside"; WriteJson(ladderSet, edited);
        throws(() => ProfileEditing.Undo(undoable), "Undo refuses when the file changed since the edit");

        // Generated rule sets and bank-specific rules stay locked.
        var generated = Inside(project.Root, "compatibility/standard/generated.json");
        WriteJson(generated, new JsonObject { ["generator"] = "scripts/x.py", ["rules"] = new JsonArray(new JsonObject { ["table"] = "rows", ["record"] = table.Records[1].S("sourceId"), ["reason"] = "gen", ["changes"] = new JsonObject { ["value"] = new JsonObject { ["expect"] = "2", ["value"] = "20" } } }) });
        var standardSettings = Settings("standard"); standardSettings["tableOverrides"] = new JsonArray("generated.json"); Save("standard", standardSettings);
        var standardOverlay = ProfileOverlay.Compute(project, table, "standard");
        check(standardOverlay.Cell(1, "value") is { Editable: false, Value: "20" }, "The overlay marks generated changes as locked");
        throws(() => ProfileEditing.Apply(project, table, "standard", [new SetProfileCell(1, "value", "21")], "x"), "Studio does not rewrite a generated rule set's change");
        ProfileEditing.Apply(project, table, "standard", [new SetProfileCell(1, "enabled", "0")], "Soft mod");
        check(Built("standard").Contains("Two\t20\t0"), "Studio changes beside a generated set land in its own rule set");

        // A child's asset override replaces the parent's for the same target; plain profiles cannot ship loader assets.
        write(Inside(project.Root, "compatibility/d2rl/parent.txt"), "parent"); write(Inside(project.Root, "compatibility/ladder/child.txt"), "child");
        var parentSettings = Settings("d2rl"); parentSettings["assetOverrides"] = new JsonArray(new JsonObject { ["scope"] = "mod", ["target"] = "d2rloader/assets/a.txt", ["source"] = "parent.txt", ["reason"] = "fixture" }); Save("d2rl", parentSettings);
        var built = BuildService.Build(project, "ladder");
        check(File.ReadAllText(Inside(built.Output, "d2rloader/assets/a.txt")) == "parent", "A child ships its parent's asset overrides, its source relative to the parent's folder");
        var childSettings = Settings("ladder"); childSettings["assetOverrides"] = new JsonArray(new JsonObject { ["scope"] = "mod", ["target"] = "d2rloader/assets/a.txt", ["source"] = "child.txt", ["reason"] = "fixture" }); Save("ladder", childSettings);
        built = BuildService.Build(project, "ladder");
        check(File.ReadAllText(Inside(built.Output, "d2rloader/assets/a.txt")) == "child", "A child's asset override replaces its parent's for the same target");
        ProfileManager.Create(project, "soft", copyFrom: "standard");
        var softSettings = Settings("soft"); softSettings["assetOverrides"] = new JsonArray(new JsonObject { ["scope"] = "mod", ["target"] = "d2rloader/assets/a.txt", ["source"] = "generated.json", ["reason"] = "fixture" }); Save("soft", softSettings);
        throws(() => BuildService.Build(project, "soft"), "A plain-runtime profile cannot ship d2rloader assets");
        check(File.Exists(Inside(project.Root, "compatibility/soft/generated.json")) && Settings("soft").S("id") == "soft", "Copying a profile copies its files and takes the new id");

        // Settings: a cycle or unknown parent is refused; modInfo is validated.
        throws(() => ProfileManager.Update(project, "d2rl", ProfileManager.Load(project, "d2rl") with { BasedOn = "ladder" }), "A profile cannot be based on its own child");
        throws(() => ProfileManager.Update(project, "ladder", ProfileManager.Load(project, "ladder") with { SavePath = "../x/" }), "Profile settings reject an unsafe save path");
        throws(() => ProfileManager.Update(project, "ladder", ProfileManager.Load(project, "ladder") with { Color = "blue" }), "Profile colors are #RRGGBB");
        ProfileManager.Update(project, "ladder", ProfileManager.Load(project, "ladder") with { ModName = "ProfilesLadder", SavePath = "ProfilesLadder/" });
        built = BuildService.Build(project, "ladder", modName: "ProfilesLadder");
        check(Read(Inside(built.Output, "ProfilesLadder.mpq/modinfo.json")).S("savepath") == "ProfilesLadder/", "A child's modInfo applies to its build");
        var colorOnly = ProfileChain.Load(project.Root, "ladder").ContentKey();
        ProfileManager.Update(project, "ladder", ProfileManager.Load(project, "ladder") with { Color = "#AA5500" });
        check(ProfileChain.Load(project.Root, "ladder").ContentKey() == colorOnly, "A profile's color does not change what it builds");

        // Rename carries children and run settings; delete refuses a parent and keeps a copy.
        new RunSettings(DeploymentDirectory: "C:/games/ladder").Save(project, "ladder");
        throws(() => ProfileManager.Delete(project, "d2rl"), "A profile others are based on cannot be deleted");
        ProfileManager.Rename(project, "d2rl", "loader");
        check(project.Profiles.Contains("loader") && !project.Profiles.Contains("d2rl") && Settings("loader").S("id") == "loader" && Settings("ladder").S("basedOn") == "loader", "Renaming a profile rewrites its id and its children's basedOn");
        check(Built("ladder").Contains("One\t11\t1"), "A renamed parent still builds into its children");
        ProfileManager.Rename(project, "ladder", "season");
        check(RunSettings.Load(project, "season").DeploymentDirectory == "C:/games/ladder", "Renaming a profile keeps its run settings");
        throws(() => ProfileManager.Rename(project, "season", "standard"), "Rename refuses a name in use");
        var trash = ProfileManager.Delete(project, "season");
        check(!project.Profiles.Contains("season") && File.Exists(Path.Combine(trash, "profile.json")) && File.Exists(Path.Combine(trash, "run-settings.json")), "Deleting a profile moves it and its run settings to .studio/deleted-profiles");

        // A missing or cyclic parent fails the load with a clear message.
        var orphan = Settings("soft"); orphan["basedOn"] = "gone"; Save("soft", orphan);
        throws(() => ProfileChain.Load(project.Root, "soft"), "A profile based on a missing profile is refused");
        orphan["basedOn"] = "soft"; Save("soft", orphan);
        throws(() => ProfileChain.Load(project.Root, "soft"), "A profile based on itself is refused");
    }
}
