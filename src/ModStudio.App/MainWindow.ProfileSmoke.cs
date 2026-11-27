using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// Profiles end to end in the editor: a new profile based on d2rl, its view of a table, typing, multi-cell edits,
    /// undo/redo and leaving a row out write its overrides (never shared source); the shared view marks what it changes.
    /// </summary>
    private async Task SmokeProfilesAsync(string root, string output)
    {
        void Screenshot(string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, name), PngBitmapEncoderOptions.Default);
        }
        async Task Wait(Func<bool> ready, string expectation)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(10)) { if (ready()) return; await Task.Delay(20); }
            throw new TimeoutException("Profile smoke: " + expectation);
        }
        var current = project!;
        ProfileManager.Create(current, "ladder", basedOn: "d2rl", color: ProfileColors.Palette[2]);
        var manager = new ProfileManagerWindow(current, Profile, ProfileFolderBlocker) { Width = 860, Height = 640 };
        manager.Show(this); await Task.Delay(150);
        using (var bitmap = new RenderTargetBitmap(new PixelSize(860, 640), new Vector(96, 96))) { bitmap.Render(manager); bitmap.Save(System.IO.Path.Combine(output, "profile-manager.png"), PngBitmapEncoderOptions.Default); }
        manager.Close(); Activate(); Focus(); await Task.Delay(100);

        var opened = await OpenDocumentAsync(System.IO.Path.Combine(root, "source/tables/sounds.json"));
        Require(opened != null, "Profile smoke could not open sounds: " + string.Join(" | ", tabs.Select(t => (t.Content as Avalonia.Controls.TextBlock)?.Text).OfType<string>()));
        var pane = opened!;
        RefreshProfileDependents();
        await Wait(() => pane.ProfileLensChoices.Contains("ladder"), "the view picker offers the new profile");
        var table = pane.Document.Table!; var column = table.Columns[1]; var shared = table.Cell(0, column);
        var ruleSet = Inside(current.Root, "compatibility/ladder/" + ProfileEditing.StudioReference(table.Name));

        pane.SetProfileLens("ladder");
        Require(pane.ProfileLens == "ladder", "Switching the view to a profile failed.");
        Require(ThrowsData(() => pane.InsertRows(0)), "A profile view must refuse to add rows.");
        pane.ProfileReason = "Ladder season";
        pane.Jump(0, column); await Task.Delay(100); UpdateLayout();
        pane.TableGrid.Focus(); this.KeyTextInput("777"); this.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Wait(() => File.Exists(ruleSet), "typing in the profile view writes the profile's rule set");
        Require(table.Cell(0, column) == shared && !pane.Document.IsDirty, "Typing in a profile view changed shared source.");
        var overlay = ProfileOverlay.Compute(current, table, "ladder");
        Require(overlay.Own(0, column) is { Value: "777", Reason: "Ladder season" } state && state.Expect == shared, "The typed override has the wrong value, reason or expectation.");
        Require(File.ReadAllText(ruleSet).Split("\"expect\"").Length == 2, "Typing wrote more than one override (each keystroke must not write).");
        await Wait(() => pane.CellText(0, column) == "777" && pane.ProfileLook(0, Array.IndexOf(table.Columns, column)).Tint != null, "the profile view shows and tints its change");
        UpdateLayout(); await Task.Delay(150); UpdateLayout();
        Screenshot("profile-view.png");

        pane.Undo();
        await Wait(() => !File.Exists(ruleSet) && pane.CellText(0, column) == shared, "undo removes the profile's override");
        pane.Redo();
        await Wait(() => File.Exists(ruleSet) && pane.CellText(0, column) == "777", "redo puts the override back");

        // Several selected cells take one value, in one write.
        pane.SelectCell(1, 2); pane.SelectCell(2, 2, shift: true);
        pane.ApplyToSelection("5", null);
        overlay = ProfileOverlay.Compute(current, table, "ladder");
        Require(overlay.Own(1, table.Columns[2])?.Value == "5" && overlay.Own(2, table.Columns[2])?.Value == "5" && table.Cell(1, table.Columns[2]) != "5", "A multi-cell edit in a profile view did not write overrides for every cell.");

        // Leave the last row out, then look at the shared view's marks.
        int last = table.Records.Count - 1;
        pane.WriteProfile([new RemoveProfileRow(last)]);
        Require(ProfileOverlay.Compute(current, table, "ladder").Removed(last) != null && pane.ProfileLook(last, 0).Struck, "Leaving the last row out is not shown struck through.");
        Require(ThrowsData(() => pane.WriteProfile([new RemoveProfileRow(0)])), "Leaving out a row that kept rows follow must be refused.");
        pane.SetProfileLens(null);
        await Wait(() => pane.ProfileLook(0, Array.IndexOf(table.Columns, column)).Marks?.Count > 0 && pane.ProfileLook(last, 0).Strikes?.Count > 0, "the shared view marks the profile's changes");
        Require(pane.ProfileTip(0, Array.IndexOf(table.Columns, column))?.Contains("ladder: '777'") == true, "The shared view's hover text does not name the profile's value.");
        Require(pane.CellText(0, column) == shared, "The shared view shows a profile value.");
        UpdateLayout(); await Task.Delay(150); UpdateLayout();
        Screenshot("profile-marks.png");

        // Shared edits still go to shared source and leave the profile alone.
        pane.Document.SetCells([(3, column, "shared-edit")]);
        Require(table.Cell(3, column) == "shared-edit", "A shared edit did not reach shared source.");
        pane.Document.Undo(); pane.Refresh();

        // The build applies the chain: d2rl, then ladder.
        var built = BuildService.Build(current, "ladder");
        var txt = File.ReadAllText(Inside(built.Output, "ExampleMod.mpq/data/global/excel/sounds.txt"));
        Require(txt.Contains("\t777\t") && !txt.Contains(table.Cell(last, table.Columns[0]) + "\t"), "The ladder build does not carry its overrides.");

        tabs.Clear(); previewTab = null;
        ProfileManager.Delete(current, "ladder");
        RefreshProfileDependents();
        Activate(); Focus(); await Task.Delay(100);
    }
    private static bool ThrowsData(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
}
