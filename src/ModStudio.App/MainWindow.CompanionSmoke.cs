using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless;
using ModStudio.Core;
using Reimagined.Integration;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private async Task SmokeCompanionAsync(string output)
    {
        string exe = IntegrationFiles.Option(Program.Arguments, "--level-editor-exe") ?? throw new InvalidOperationException("Pass --level-editor-exe.");
        string assets = IntegrationFiles.Option(Program.Arguments, "--game-data") ?? throw new InvalidOperationException("Pass --game-data.");
        var current = project!;
        foreach (var name in new[] { "levels", "lvlprest", "lvltypes", "lvlwarp" })
        {
            var text = File.ReadAllBytes(Path.Combine(assets, "global/excel", name + ".txt"));
            TableData.Write(TableData.FileFor(current, "tables", name), TableData.FromTsv(text, name, "global/excel/" + name + ".txt"));
        }
        var prefs = new StudioPreferences { GameDataFolder = assets }; prefs.Save(StudioPreferences.DefaultFile);
        var snapshot = LevelEditorWorkspace.Prepare(current, "standard");
        var scene = LevelEditorWorkspace.CopyBasePair(current, LevelEditorWorkspace.Scene(current, "standard", "act1/town/townw1.ds1", assets));
        var before = File.ReadAllBytes(scene.Preset);
        CompanionApps.SetOverride(CompanionApps.Level, exe);
        CompanionApps.SetOverride(CompanionApps.Studio, Environment.ProcessPath!);
        Require(Program.Integration != null, "Integration smoke needs an activation host.");
        await Program.Integration!.StartAsync(async request => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ReceiveCompanionAsync(request)));
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in new[] { "--integration-ui-smoke", output, "--settings-file", Path.Combine(output, "level-settings.json"), "--data", assets }) start.ArgumentList.Add(arg);
        using var level = Process.Start(start) ?? throw new InvalidOperationException("Level Editor did not start.");
        try
        {
            var readyFile = Path.Combine(output, "level-ready.txt");
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (!File.Exists(readyFile)) { Require(!level.HasExited && DateTime.UtcNow < deadline, "Level Editor did not become ready."); await Task.Delay(100); }
            await OpenLevelEditorAsync(scene.Preset);
            Require(File.Exists(Path.Combine(output, "level-receipt.json")), "Level Editor did not receive Studio navigation: " + Status.Text);
            using (var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "level-receipt.json"))))
            {
                Require(receipt.RootElement.GetProperty("projectId").GetString() == current.Id, "Level Editor opened the wrong project.");
                Require(receipt.RootElement.GetProperty("sceneSaved").GetBoolean(), "Scene save did not persist.");
            }
            Require(!File.ReadAllBytes(scene.Preset).SequenceEqual(before), "Scene editing did not change the project asset.");
            Require(Active?.Document.Table?.Name == "levels" && Active.SelectedColumn == "Vis0" && Active.Document.Table.Cell(Active.SelectedRow, "Id") == "1", "Return link did not select the exact Studio record/cell.");
            var pane = Active!; pane.Document.SetCells([(pane.SelectedRow, "MonLvlEx", "17")]);
            Require(await SaveAllAsync(), "Studio table save failed.");
            await RefreshCompanionSnapshotsAsync();
            var context = new EditorProject(current.Id, current.Root, "standard", assets, LevelEditorWorkspace.Pointer(current, "standard"));
            var target = new EditorTarget(Preset: Relative(current.Root, scene.Preset), Map: Relative(current.Root, scene.Map), LogicalMap: scene.LogicalMap);
            var reply = await CompanionApps.SendAsync(CompanionApps.Resolve(CompanionApps.Level), new("open-scene", context, target));
            Require(reply.Success, "Second activation failed: " + reply.Message);
            using (var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "level-receipt.json"))))
                Require(receipt.RootElement.GetProperty("areaLevel").GetString() == "17" && receipt.RootElement.GetProperty("pid").GetInt32() == level.Id, "Existing Level Editor did not reload the Studio snapshot.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using (var frame = this.CaptureRenderedFrame()) frame?.Save(Path.Combine(output, "studio-companion.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            File.WriteAllText(Path.Combine(output, "companion-smoke.txt"), "PASS Windows cross-process activation, exact project/preset/map, scene edit/save, return to levels Id=1 Vis0, Studio save and table snapshot refresh, repeat activation reuses the same Level Editor process, WPF and Avalonia screenshots.");
        }
        finally { if (!level.HasExited) { level.CloseMainWindow(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); try { await level.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { level.Kill(); } } }
    }
}
