using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private async Task SmokeExternalEditorAsync(string output)
    {
        externalTimer.Stop();
        var source = TableData.FileFor(project!, "tables", "external-smoke");
        TableData.Write(source, TableData.FromTsv(Utf8.GetBytes("code\tvalue\tnote\na\t10\tfirst\nb\t20\tsecond\n"), "external-smoke", "global/excel/external-smoke.txt"));
        var pane = (await OpenDocumentAsync(source))!;
        var target = System.IO.Path.Combine(output, "deployment", project!.Name);
        var ownerFile = Inside(target, ".studio-owner.json");
        var previousOwner = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new DeploymentManifest("earlier-conversion", "old-build", Profile, []), Pretty);
        AtomicWrite(ownerFile, previousOwner);
        var replacedFile = Inside(target, project.Name + ".mpq/data/global/excel/external-smoke.txt");
        AtomicWrite(replacedFile, Utf8.GetBytes("previous external edits"));
        async Task<BuildResult> Reuse(bool approve)
        {
            var pending = controller.ExecuteAsync(project, Profile, new RunSettings(DeploymentDirectory: target), true, false, CancellationToken.None, Log, ReviewDeploymentOwnershipAsync);
            Window? dialog = null;
            for (int attempt = 0; attempt < 400 && dialog == null && !pending.IsCompleted; attempt++)
            {
                dialog = OwnedWindows.FirstOrDefault(w => w.Title == "Reuse existing deployment?");
                if (dialog == null) await Task.Delay(25);
            }
            Require(dialog != null, "Ownership conflict did not display the reuse dialog.");
            await Dispatcher.UIThread.InvokeAsync(dialog!.UpdateLayout, DispatcherPriority.Background);
            if (approve)
            {
                using var shot = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height), new Vector(96, 96));
                shot.Render(dialog); shot.Save(System.IO.Path.Combine(output, "deployment-reuse.png"), PngBitmapEncoderOptions.Default);
                dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Back up and reuse this folder").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else dialog.Close();
            return await pending;
        }
        try { await Reuse(false); throw new InvalidOperationException("Closing ownership review unexpectedly approved deployment."); }
        catch (OperationCanceledException) { Require(File.ReadAllBytes(ownerFile).SequenceEqual(previousOwner), "Cancel changed deployment ownership."); }
        var build = await Reuse(true);
        ExternalEditorSync.Begin(project!, build, target); WatchExternalSession();
        var txt = Inside(target, project.Name + ".mpq/data/global/excel/external-smoke.txt");
        void External(string value)
        {
            var table = TableData.FromTsv(File.ReadAllBytes(txt), "external-smoke", "global/excel/external-smoke.txt"); table.SetCell(0, "value", value); AtomicWrite(txt, table.EncodeTsv());
        }
        External("11");
        Require(await SyncExternalAsync(false), "UI external import failed: " + externalStatus);
        Require(pane.Document.Table!.Cell(0, "value") == "11" && !pane.Document.IsDirty && !pane.Document.ExternalChange, "Open Studio pane did not follow external import.");
        pane.Document.SetCells([(1, "note", "Studio edit")]); External("12");
        Require(!await SyncExternalAsync(false) && pane.Document.IsDirty && pane.Document.Table.Cell(0, "value") == "11", "Sync discarded or bypassed an unsaved buffer.");
        Require(await SaveAllAsync() && await SyncExternalAsync(false), "Saved buffer did not resume synchronization.");
        Require(pane.Document.Table.Cell(0, "value") == "12" && TableData.FromTsv(File.ReadAllBytes(txt), "external-smoke", "global/excel/external-smoke.txt").Cell(1, "note") == "Studio edit", "UI failed to merge saved Studio edits with external changes.");
        var tab = tabs.Single(t => t.Content == pane);
        Require(tab.ContextMenu!.Items.OfType<MenuItem>().Any(m => m.Header?.ToString() == "Open External Editor…"), "Document menu has no external editor action.");
        Require(((Control)tab.Header!).ContextMenu!.Items.OfType<MenuItem>().Any(m => m.Header?.ToString() == "Open External Editor…"), "Tab header menu has no external editor action.");
        Require(ContainerMenu(project.Root).OfType<MenuItem>().Any(m => m.Header?.ToString() == "Open External Editor Workspace…"), "Explorer root has no external workspace action.");
        External("13");
        pane.Document.SetCells([(0, "value", "14")]); Require(await SaveAllAsync(), "Conflict fixture save failed.");
        Require(!await SyncExternalAsync(false) && externalStatus.Contains("row 1") && pane.Document.Table.Cell(0, "value") == "14", "UI conflict status failed to preserve the open source.");
        // Restore the TXT baseline, keeping the Studio edit, then let the same normal sync path converge.
        External("12"); Require(await SyncExternalAsync(false), "UI conflict recovery did not converge.");
        externalTimer.Start();
        External("15");
        for (int attempt = 0; attempt < 200 && pane.Document.Table.Cell(0, "value") != "15"; attempt++) await Task.Delay(50);
        Require(pane.Document.Table.Cell(0, "value") == "15", "Filesystem watcher did not automatically import the external save.");
        externalTimer.Stop();
        var settingsTask = ExternalEditorSettingsAsync();
        await Dispatcher.UIThread.InvokeAsync(UpdateLayout, DispatcherPriority.Background);
        var settingsDialog = OwnedWindows.Single(w => w.Title == "External editor");
        settingsDialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Use TXTeditor arguments").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(settingsDialog.GetVisualDescendants().OfType<TextBox>().Any(b => b.Text == "{files}"), "TXTeditor preset did not configure workspace files.");
        using (var shot = new RenderTargetBitmap(new PixelSize(680, 620), new Vector(96, 96)))
        { shot.Render(settingsDialog); shot.Save(System.IO.Path.Combine(output, "external-editor-settings.png"), PngBitmapEncoderOptions.Default); }
        settingsDialog.Close(false); Require(!await settingsTask, "Canceling editor settings did not cancel.");
        foreach (var width in new[] { 1440, 1100 })
        {
            Width = width; Height = 920;
            await Dispatcher.UIThread.InvokeAsync(UpdateLayout, DispatcherPriority.Background);
            await Task.Delay(100);
            using var shot = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            shot.Render(this); shot.Save(System.IO.Path.Combine(output, $"external-editor-{width}.png"), PngBitmapEncoderOptions.Default);
        }
        ExternalEditorSync.End(project); WatchExternalSession();
        Require(!externalActive, "Ending external editing did not release the UI session.");
        File.WriteAllText(System.IO.Path.Combine(output, "external-editor-passed.json"), "{\"passed\":true}");
    }
}
