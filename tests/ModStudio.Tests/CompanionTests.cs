using System.Text.Json.Nodes;
using ModStudio.Core;
using Reimagined.Integration;
using static ModStudio.Core.Storage;

internal static class CompanionTests
{
    public static async Task Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var folder = Path.Combine(root, "Companion ü project"); Directory.CreateDirectory(folder);
        var project = new ModProject(folder, "companion-tests", "Companion");
        WriteJson(Inside(folder, "mod-project.json"), new JsonObject { ["schemaVersion"] = 2, ["id"] = project.Id, ["name"] = project.Name });
        var levels = TableData.FromTsv(Utf8.GetBytes("Name\tId\tMonLvlEx\tVis0\nFirst\t137\t85\t0\nSecond\t138\t86\t137\n"), "levels", "global/excel/levels.txt");
        var tableFile = TableData.FileFor(project, "tables", "levels"); TableData.Write(tableFile, levels);
        var standard = Inside(folder, "compatibility/standard/profile.json");
        WriteJson(standard, new JsonObject { ["schemaVersion"] = 1, ["id"] = "standard", ["stringMode"] = "standard", ["tableOverrides"] = new JsonArray("level.json") });
        var ruleFile = Inside(folder, "compatibility/standard/level.json");
        WriteJson(ruleFile, new JsonObject { ["table"] = "levels", ["reason"] = "test", ["record"] = levels.Records[0].S("sourceId"), ["changes"] = new JsonObject { ["MonLvlEx"] = new JsonObject { ["expect"] = "85", ["value"] = "90" } } });
        var sourceBefore = File.ReadAllBytes(tableFile);
        var snapshot = LevelEditorWorkspace.Prepare(project, "standard");
        check(LevelEditorWorkspace.Table(snapshot, "levels", null)!.Cell(0, "MonLvlEx") == "90", "Companion snapshot applies profile overrides");
        check(File.ReadAllBytes(tableFile).SequenceEqual(sourceBefore), "Snapshot never modifies authored source");
        check(snapshot.Tables["global/excel/levels.txt"].SourceIds[0] == levels.Records[0].S("sourceId"), "Snapshot retains stable row identity");
        check(LevelEditorWorkspace.TableSource(project, new(project.Id, folder), "levels") == tableFile, "Return navigation resolves authored source");
        levels.SetCell(1, "MonLvlEx", "95"); TableData.Write(tableFile, levels);
        var updated = LevelEditorWorkspace.Prepare(project, "standard");
        check(updated.Revision != snapshot.Revision && LevelEditorWorkspace.Table(snapshot, "levels", null)!.Cell(1, "MonLvlEx") == "86", "Snapshot refresh is immutable and revisioned");
        levels.SetCell(0, "MonLvlEx", "84"); TableData.Write(tableFile, levels);
        throws(() => LevelEditorWorkspace.Prepare(project, "standard"), "Stale profile overrides fail instead of showing vanilla data");
        check(IntegrationFiles.Read<IntegrationSnapshot>(LevelEditorWorkspace.Pointer(project, "standard")).Revision == updated.Revision, "Failed snapshot preserves the last complete revision");
        var native = new ModProject(Path.Combine(root, "native-companion"), "native", "Native"); Directory.CreateDirectory(native.Root);
        AtomicWrite(Inside(native.Root, "data/global/excel/levels.txt"), sourceBefore.Length > 0 ? levels.EncodeTsv() : []);
        var nativeSnapshot = LevelEditorWorkspace.Prepare(native, "standard");
        check(nativeSnapshot.Tables["global/excel/levels.txt"].Source == "data/global/excel/levels.txt", "Native TXT projects remain native");
        string presetFile = Inside(folder, "data/hd/env/preset/custom.json"), customMap = Inside(folder, "data/global/tiles/custom-map.ds1");
        AtomicWrite(presetFile, Utf8.GetBytes("{}")); AtomicWrite(customMap, [1, 2, 3]);
        WriteJson(presetFile + ".rle-project.json", new JsonObject { ["Map"] = "data/global/tiles/custom-map.ds1" });
        check(LevelEditorWorkspace.SceneFile(project, "standard", presetFile, null).Map == customMap, "Scene launch respects authored level metadata instead of guessing a DS1 name");
        check(LevelEditorWorkspace.SceneFile(project, "standard", customMap, null).Preset == presetFile, "DS1 explorer launch finds its authored preset pair");
        var overrideSource = Inside(folder, "compatibility/standard/custom-scene.json"); AtomicWrite(overrideSource, Utf8.GetBytes("{\"override\":true}"));
        var settings = Read(standard); settings["assetOverrides"] = new JsonArray(new JsonObject { ["scope"] = "data", ["target"] = "hd/env/preset/custom.json", ["source"] = "custom-scene.json", ["expectSha256"] = Hash(File.ReadAllBytes(presetFile)), ["reason"] = "test" }); WriteJson(standard, settings);
        check(LevelEditorWorkspace.SceneFile(project, "standard", presetFile, null).Preset == overrideSource, "Scene edits target the actual profile asset source");
        settings["assetOverrides"]![0]!["transform"] = "json-template"; WriteJson(standard, settings);
        throws(() => LevelEditorWorkspace.SceneFile(project, "standard", presetFile, null), "Generated scene transforms cannot be mistaken for editable assets");
        throws(() => new EditorRequest("open-scene", new(project.Id, folder), new(Preset: "../outside.json")).Validate(), "Navigation rejects project path traversal");
        throws(() => new EditorRequest(ProtocolVersion: 2).Validate(), "Navigation rejects unsupported protocol versions");
        var app = new EditorInstallation(CompanionApps.Studio, Path.Combine(folder, "current", "ModStudio.App.exe"), "1", "velopack", "test");
        var start = CompanionApps.StartInfo(app, "--integration-request", Path.Combine(folder, "request ü.json"));
        check(start.FileName.EndsWith("Update.exe") && start.ArgumentList.SequenceEqual(new[] { "start", "ModStudio.App.exe", "--", "--integration-request", Path.Combine(folder, "request ü.json") }), "Velopack launch preserves Unicode paths as structured arguments");
        var oldHome = Environment.GetEnvironmentVariable("REIMAGINED_INTEGRATION_HOME");
        Environment.SetEnvironmentVariable("REIMAGINED_INTEGRATION_HOME", Path.Combine(root, "integration-home"));
        try
        {
            check(ActivationHost.Initialize(CompanionApps.Studio, [], out var host) && host != null, "Activation host acquires its installation lease");
            using (host)
            {
                int count = 0;
                await host!.StartAsync(r => { count++; return Task.FromResult(new EditorReply("opened", r.Project?.Root ?? "focused")); });
                var current = CompanionApps.Current(CompanionApps.Studio);
                var request = new EditorRequest(Project: new(project.Id, folder), RequestId: "repeat");
                var reply = await ActivationHost.TrySendAsync(current, request);
                check(reply?.Success == true && reply.Message == folder, "Running instance receives the exact project through current-user IPC");
                await ActivationHost.TrySendAsync(current, request);
                check(count == 1, "Repeated navigation requests do not execute twice");
                host.ClaimProject(folder);
                CompanionApps.SetOverride(CompanionApps.Level, Path.Combine(folder, "missing.exe"));
                throws(() => CompanionApps.Resolve(CompanionApps.Level), "Missing manual override never silently selects another application");
            }
        }
        finally { Environment.SetEnvironmentVariable("REIMAGINED_INTEGRATION_HOME", oldHome); }
    }
}
