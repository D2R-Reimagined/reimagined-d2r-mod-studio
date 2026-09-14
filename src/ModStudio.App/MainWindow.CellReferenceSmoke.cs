using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private async Task SmokeCellReferencesAsync(string output)
    {
        int previousOutput = Output.Text?.Length ?? 0;
        void Table(string name, string text) => TableData.Write(Semantics.TableFile(project!, name), TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt"));
        Table("gamble", "name\tcode\nCap\tcap\nHand Axe\thax\nHealing Potion\thp1\nUnknown\tmissing\nEmpty\t\nDuplicate\tshared\nAmulet\tamu\n");
        Table("armor", "name\tcode\nCap\tcap\nShared armor\tshared\n");
        Table("weapons", "name\tcode\nHand Axe\thax\n");
        Table("misc", "name\tcode\nHealing Potion\thp1\nShared misc\tshared\n" +
            string.Concat(Enumerable.Range(0, 300).Select(i => $"Item {i:000}\titem{i:000}\n")) + "Amulet\tamu\n");
        var file = Semantics.TableFile(project!, "gamble");
        var source = (await OpenDocumentAsync(file))!;
        async Task Wait(Func<bool> ready)
        {
            for (int attempt = 0; attempt < 300 && !ready(); attempt++) await Task.Delay(10);
            Require(ready(), "Gamble reference UI did not settle.");
        }
        Button? Arrow(int row, DataGrid? grid = null) => ((Control?)grid ?? source).GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("cellReference") && b.DataContext is RowView item && item.Row == row);
        async Task ShowSource(int row)
        {
            await OpenDocumentAsync(file); source.Jump(row, "code"); await Wait(() => Arrow(row) is { Bounds.Width: > 0 });
        }
        await ShowSource(0);
        Require(Arrow(4) is { IsVisible: false }, "Empty Gamble cell shows a reference arrow.");
        foreach (var (row, name) in new[] { (0, "armor"), (1, "weapons"), (2, "misc") })
        {
            await ShowSource(row);
            var button = Arrow(row)!;
            var point = button.TranslatePoint(new Point(7, 7), this)!.Value;
            this.MouseDown(point, MouseButton.Left); this.MouseUp(point, MouseButton.Left);
            await Wait(() => Active?.Document.Table?.Name == name && Active.SelectedColumn == "code");
            Require(!source.Document.IsDirty && source.TableGrid.GetVisualDescendants().OfType<TextBox>().All(t => !t.IsFocused),
                "Reference click edited the source cell.");
        }
        var target = Active!; target.Document.SetCells([(0, "code", "unsaved")]); target.RefreshRowValues(0);
        await ShowSource(2); source.Document.SetCells([(2, "code", "unsaved")]); source.RefreshRowValues(2);
        await NavigateCellReferenceAsync(source, 2, "code", Arrow(2)!);
        Require(Active == target && target.Document.IsDirty, "Reference did not reuse the unsaved target tab.");
        target.Document.Undo(); target.Refresh(); source.Document.Undo(); source.Refresh();

        await ShowSource(5); var duplicate = Arrow(5)!;
        await NavigateCellReferenceAsync(source, 5, "code", duplicate);
        var choices = duplicate.ContextMenu!.Items.OfType<MenuItem>().Where(i => i.IsEnabled).ToArray();
        Require(choices.Length == 2 && Active == source, "Duplicate reference did not offer both destinations.");
        duplicate.ContextMenu.Close();
        choices[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Wait(() => Active?.Document.Table?.Name == "misc" && Active.SelectedRow == 1);
        await ShowSource(3); var missing = Arrow(3)!;
        await NavigateCellReferenceAsync(source, 3, "code", missing);
        Require(Active == source && missing.ContextMenu!.Items.OfType<MenuItem>().All(i => !i.IsEnabled), "Missing reference opened an unrelated row.");
        missing.ContextMenu!.Close();

        // A chooser opened before an edit must never navigate using its stale row matches.
        await ShowSource(5); duplicate = Arrow(5)!;
        await NavigateCellReferenceAsync(source, 5, "code", duplicate);
        var stale = duplicate.ContextMenu!.Items.OfType<MenuItem>().First(i => i.IsEnabled); duplicate.ContextMenu.Close();
        source.Document.SetCells([(5, "code", "cap")]);
        stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(Active == source && Status.Text!.Contains("changed"), "Stale reference chooser navigated after editing the source.");
        source.Document.Undo(); source.Refresh();

        await ShowSource(0); source.ToggleFrozenRows();
        await Wait(() => Arrow(0, source.FrozenGrid) is { IsVisible: true });
        Arrow(0, source.FrozenGrid)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => Active?.Document.Table?.Name == "armor");
        await ShowSource(0); source.ToggleFrozenRows();
        source.TableGrid.Focus(); this.KeyPress(Key.Enter, RawInputModifiers.Alt, PhysicalKey.Enter, null);
        await Wait(() => Active?.Document.Table?.Name == "armor");
        await ShowSource(0);
        source.TableGrid.Focus(); this.KeyTextInput("edited"); this.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Wait(() => source.Document.Table!.Cell(0, "code") == "edited");
        source.Document.Undo(); source.Refresh();
        // The reference target starts offscreen, and may be hidden by an existing row filter.
        var misc = (await OpenDocumentAsync(Semantics.TableFile(project!, "misc")))!;
        var rowFilter = misc.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Filter rows (Enter)");
        rowFilter.Text = "Healing"; await misc.FilterAsync();
        await ShowSource(6);
        var amuletArrow = Arrow(6)!;
        var amuletPoint = amuletArrow.TranslatePoint(new Point(7, 7), this)!.Value;
        this.MouseDown(amuletPoint, MouseButton.Left); this.MouseUp(amuletPoint, MouseButton.Left);
        DataGridRow? LinkedRow() => misc.TableGrid.GetVisualDescendants().OfType<DataGridRow>()
            .FirstOrDefault(r => r.DataContext is RowView { Row: 302 });
        bool VisibleAmulet()
        {
            var linked = LinkedRow();
            return Active == misc && misc.HighlightedReferenceRow == 302 && linked is { Bounds.Height: > 0 } &&
                linked.TranslatePoint(new Point(), misc.TableGrid) is { Y: >= 0 } position &&
                position.Y + linked.Bounds.Height <= misc.TableGrid.Bounds.Height;
        }
        await Wait(VisibleAmulet);
        await Wait(() => LinkedRow()!.Background is not null);
        Require(rowFilter.Text == "" && misc.SelectedCells.Count == 1 && misc.SelectedCells.Contains((302, 1)),
            "Reference did not reveal Amulet with only its code selected for editing.");
        var nameCell = misc.TableGrid.Columns[0].GetCellContent(LinkedRow()!)!.GetVisualAncestors().OfType<DataGridCell>().First();
        Require(nameCell.Background == LinkedRow()!.Background && nameCell.Background != null,
            "Reference highlighted only the code cell instead of the whole Amulet row.");
        var highlightBrush = nameCell.Background;
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(Path.Combine(output, "amulet-reference-highlight.png"), PngBitmapEncoderOptions.Default); }
        misc.SelectCell(301, 1);
        Require(misc.HighlightedReferenceRow == -1 && LinkedRow()!.Background != highlightBrush,
            "Moving to a different row retained the old reference highlight.");
        misc.JumpToReference(302, "code"); misc.ToggleFrozenRows();
        await ShowSource(6); await NavigateCellReferenceAsync(source, 6, "code", Arrow(6)!);
        await Wait(() => misc.FrozenGrid.GetVisualDescendants().OfType<DataGridRow>()
            .Any(r => r.DataContext is RowView { Row: 302 } && r.Background == highlightBrush));
        Require(misc.HighlightedReferenceRow == 302 && misc.SelectedCells.Count == 1, "Frozen reference destination lost its row highlight.");
        misc.Document.SetCells([(302, "code", "changed")]);
        Require(misc.HighlightedReferenceRow == -1, "Editing retained a stale reference highlight.");
        misc.Document.Undo(); misc.ToggleFrozenRows();
        await ShowSource(0);
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(Path.Combine(output, "gamble-cell-references.png"), PngBitmapEncoderOptions.Default); }
        await SmokeExpandedCellReferencesAsync(output);
        Require(!(Output.Text ?? "")[Math.Min(previousOutput, Output.Text?.Length ?? 0)..].Contains("There is no current row"),
            "Reference navigation reported a grid initialization error.");
        foreach (var tab in tabs.Where(t => t.Content is EditorPane p && p.Document.Table?.Name is "gamble" or "armor" or "misc" or "weapons").ToArray())
            await CloseTabAsync(tab);
        foreach (var name in new[] { "gamble", "armor", "misc", "weapons" }) File.Delete(Semantics.TableFile(project!, name));
    }

    private async Task SmokeExpandedCellReferencesAsync(string output)
    {
        var fixtures = new Dictionary<string, string>
        {
            ["magicprefix"] = "Name\tmod1code\tmod1param\nSkillful\tstate\t1\n",
            ["properties"] = "code\tfunc1\tstat1\nstate\t24\tstate\n",
            ["states"] = "state\t*ID\nnone\t0\nfrozen\t1\n",
            ["missiles"] = "Missile\tSubMissile1\nfirebolt\texplosion\n" +
                string.Concat(Enumerable.Range(0, 150).Select(i => $"filler{i}\t\n")) + "explosion\t\n"
        };
        var saved = fixtures.Keys.ToDictionary(n => n, n => File.Exists(Semantics.TableFile(project!, n)) ? File.ReadAllBytes(Semantics.TableFile(project!, n)) : null);
        try
        {
            foreach (var pair in fixtures) TableData.Write(Semantics.TableFile(project!, pair.Key), TableData.FromTsv(Utf8.GetBytes(pair.Value), pair.Key, "global/excel/" + pair.Key + ".txt"));
            async Task Wait(Func<bool> ready)
            {
                for (int i = 0; i < 300 && !ready(); i++) await Task.Delay(10);
                Require(ready(), "Expanded reference UI did not settle.");
            }
            async Task Click(EditorPane pane, int row, string column, string target, int targetRow)
            {
                await OpenDocumentAsync(pane.Document.FilePath); pane.Jump(row, column);
                Button? CurrentArrow()
                {
                    var realized = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => r.DataContext is RowView item && item.Row == row);
                    return realized == null ? null : pane.TableGrid.CurrentColumn?.GetCellContent(realized)?
                        .GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("cellReference") && b.IsVisible);
                }
                await Wait(() => CurrentArrow() is { Bounds.Width: > 0 });
                var point = CurrentArrow()!.TranslatePoint(new Point(7, 7), this)!.Value;
                this.MouseDown(point, MouseButton.Left); this.MouseUp(point, MouseButton.Left);
                await Wait(() => Active?.Document.Table?.Name == target && Active.HighlightedReferenceRow == targetRow);
                Require(Active!.SelectedCells.Count == 1 && !pane.Document.IsDirty, "Expanded navigation changed the source or selected the whole destination for editing.");
            }
            var affix = (await OpenDocumentAsync(Semantics.TableFile(project!, "magicprefix")))!;
            await Click(affix, 0, "mod1code", "properties", 0);
            await Click(affix, 0, "mod1param", "states", 1);
            var properties = tabs.Select(t => t.Content).OfType<EditorPane>().Single(p => p.Document.Table?.Name == "properties");
            var validProperties = properties.Document.Text;
            properties.Document.SetRaw("broken"); properties.Document.ApplySource();
            Require(properties.Document.Table == null && properties.Document.PendingSource, "Invalid property source fixture remained parsed.");
            await OpenDocumentAsync(affix.Document.FilePath); affix.Jump(0, "mod1code");
            await Wait(() => affix.TableGrid.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("cellReference") && b.IsVisible));
            var anchor = affix.TableGrid.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("cellReference") && b.IsVisible);
            await NavigateCellReferenceAsync(affix, 0, "mod1code", anchor);
            Require(Active == affix && anchor.ContextMenu != null && anchor.ContextMenu.Items.OfType<MenuItem>().All(i => !i.IsEnabled),
                "An invalid open target with no parsed table fell back to stale disk.");
            anchor.ContextMenu!.Close();
            properties.Document.SetRaw(validProperties); properties.Document.ApplySource(); properties.Document.Save();
            var missiles = (await OpenDocumentAsync(Semantics.TableFile(project!, "missiles")))!;
            await Click(missiles, 0, "SubMissile1", "missiles", 151);
            await Wait(() => missiles.TableGrid.GetVisualDescendants().OfType<DataGridRow>().Any(r => r.DataContext is RowView { Row: 151 } &&
                r.TranslatePoint(new Point(), missiles.TableGrid) is { Y: >= 0 } p && p.Y + r.Bounds.Height <= missiles.TableGrid.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            bitmap.Render(this); bitmap.Save(Path.Combine(output, "missile-reference-highlight.png"), PngBitmapEncoderOptions.Default);
        }
        finally
        {
            foreach (var tab in tabs.Where(t => t.Content is EditorPane p && fixtures.ContainsKey(p.Document.Table?.Name ?? "")).ToArray()) await CloseTabAsync(tab);
            foreach (var pair in saved)
            {
                var file = Semantics.TableFile(project!, pair.Key);
                if (pair.Value == null) File.Delete(file); else File.WriteAllBytes(file, pair.Value);
            }
        }
    }
}
