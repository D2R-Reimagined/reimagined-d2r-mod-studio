using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>Column letters in headers and the Row Editor, and the live JSON check of the source editor.</summary>
    private async Task SmokeEditingAidsAsync(string root, string output)
    {
        void Screenshot(string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, name), PngBitmapEncoderOptions.Default);
        }
        var pane = await OpenDocumentAsync(System.IO.Path.Combine(root, "source/tables/sounds.json"));
        Require(pane != null, "Editing-aids smoke could not open its table.");
        var editor = pane!; var table = editor.Document.Table!;
        string Header(int column) => editor.TableGrid.Columns.First(c => editor.ColumnIndexOf(c) == column).Header as string ?? "";
        Require(Header(1) == table.Columns[1] && Header(0) == "▣ " + table.Columns[0], $"Headers did not start as plain names: '{Header(0)}', '{Header(1)}'.");
        EditorPane.ColumnLetters = true; EditorPane.RaiseColumnLettersChanged(); await Task.Delay(80);
        Require(Header(1) == "B · " + table.Columns[1] && Header(0) == "▣ A · " + table.Columns[0], $"Column letters did not appear in the headers: '{Header(0)}', '{Header(1)}'.");
        InspectorTabs.SelectedIndex = 1; editor.Jump(3, table.Columns[0]); await Task.Delay(250);
        var fields = RowEditorFields.ItemsSource as RowEditorField[];
        Require(fields is { Length: > 2 } && fields[0].Label.StartsWith("A · ") && fields[2].Label.StartsWith("C · "), "Row Editor labels did not lead with column letters.");
        Screenshot("column-letters.png");
        RowEditorSearch.Text = "C"; await Task.Delay(50);
        Require(((IEnumerable<RowEditorField>)RowEditorFields.ItemsSource!).Any(f => f.Label.StartsWith("C · ")), "Searching the Row Editor by column letter found nothing.");
        RowEditorSearch.Text = ""; await Task.Delay(50);
        EditorPane.ColumnLetters = false; EditorPane.RaiseColumnLettersChanged(); await Task.Delay(80);
        Require(Header(1) == table.Columns[1] && ((RowEditorField[])RowEditorFields.ItemsSource!)[0].Label == table.Columns[0], "Headers and Row Editor labels did not go back to plain names.");
        InspectorTabs.SelectedIndex = 0;

        // Breaking the JSON of a raw document marks the line while typing, before Apply; fixing it clears the mark.
        var raw = await OpenDocumentAsync(System.IO.Path.Combine(root, "modinfo.json"));
        Require(raw != null && raw.Source.IsVisible, "Editing-aids smoke could not open modinfo.json in the source editor.");
        var source = raw!.Source; var original = source.Text;
        source.Text = "{\n  \"name\": \"x\",\n  \"bad\": [1, 2\n}"; await Task.Delay(600);
        Require(raw.SourceErrors.Count == 1 && raw.SourceErrors[0].Line == 4 && raw.StatusText.StartsWith("⚠ Line 4:"), $"A broken JSON source was not marked on its line: {raw.SourceErrors.Count} errors, status '{raw.StatusText}'.");
        source.CaretOffset = 1; await Task.Delay(50); // bracket highlighting runs its renderer on the caret move
        Screenshot("json-error.png");
        source.Text = original; await Task.Delay(600);
        Require(raw.SourceErrors.Count == 0 && !raw.StatusText.StartsWith("⚠"), "Restoring valid JSON did not clear the error mark.");
        Require(source.TextArea.ContextMenu is { } menu && menu.ItemsSource!.OfType<MenuItem>().Any(m => m.Header as string == "Paste"), "The source editor has no context menu.");
        // Leave the fixture as found: the two raw edits are undone and the tabs dropped, so later steps open these files fresh.
        raw.Undo(); raw.Undo(); await Task.Delay(600);
        Require(!raw.Document.IsDirty, "Undoing the raw edits did not restore modinfo.json.");

        // Dragging the second tab's header left of the first one's midpoint swaps them; the dragged tab stays selected.
        var first = tabs[0]; var second = tabs[1]; await Task.Delay(50);
        var secondOrigin = second.TranslatePoint(new Point(second.Bounds.Width / 2, second.Bounds.Height / 2), this)!.Value;
        var firstOrigin = first.TranslatePoint(new Point(4, first.Bounds.Height / 2), this)!.Value;
        this.MouseDown(secondOrigin, MouseButton.Left, RawInputModifiers.None);
        this.MouseMove(new Point(secondOrigin.X - 20, secondOrigin.Y), RawInputModifiers.LeftMouseButton);
        this.MouseMove(firstOrigin, RawInputModifiers.LeftMouseButton); await Task.Delay(50);
        // Mid-drag the order is untouched; a ghost of the tab follows the pointer and the insertion bar sits at the first tab's left edge.
        Require(tabs[0] == first && tabGhost != null && tabInsertMark is { IsVisible: true } && second.Opacity < 1 && tabDropSlot == 0,
            $"Dragging a tab did not show its ghost and insertion mark before the drop: order kept {tabs[0] == first}, ghost {tabGhost != null}, mark visible {tabInsertMark?.IsVisible}, opacity {second.Opacity}, slot {tabDropSlot}.");
        Screenshot("tab-drag.png");
        this.MouseUp(firstOrigin, MouseButton.Left, RawInputModifiers.None); await Task.Delay(50);
        Require(tabs[0] == second && tabs[1] == first && Documents.SelectedItem == second && second.Opacity == 1 && tabGhost == null, "Dropping a dragged tab did not reorder the tabs or clean up the ghost.");

        // Ctrl+wheel over the table and Ctrl+0 change and restore the table font size; the source editor has its own size.
        var zoomPane = (EditorPane)first.Content!; Documents.SelectedItem = first; await Task.Delay(80);
        double tableSize = ViewSettings.TableFontSize, sourceSize = ViewSettings.SourceFontSize;
        var gridPoint = zoomPane.TableGrid.TranslatePoint(new Point(200, 120), this)!.Value;
        this.MouseWheel(gridPoint, new Vector(0, 1), RawInputModifiers.Control); await Task.Delay(50);
        Require(ViewSettings.TableFontSize == tableSize + 1 && ViewSettings.SourceFontSize == sourceSize && zoomPane.TableGrid.FontSize == tableSize + 1 && zoomPane.TableGrid.RowHeight == ViewSettings.TableRowHeight, $"Ctrl+wheel did not enlarge the table font ({ViewSettings.TableFontSize}).");
        this.MouseWheel(gridPoint, new Vector(0, 1), RawInputModifiers.None); await Task.Delay(30);
        Require(ViewSettings.TableFontSize == tableSize + 1, "A plain wheel turn changed the font size.");
        zoomPane.TableGrid.Focus(); this.KeyPress(Key.D0, RawInputModifiers.Control, PhysicalKey.Digit0, null); await Task.Delay(50);
        Require(ViewSettings.TableFontSize == ViewSettings.DefaultTableFontSize, "Ctrl+0 did not restore the default table font size.");
        ViewSettings.Zoom(source: false, 0);

        // Colour-transform picker: a synthetic act palette (gradient base, hue table t rotates indices by t) in the project's data folder is found and drawn as swatches.
        var pl2 = new byte[PaletteShifts.FileSize];
        for (int i = 0; i < 256; i++) { pl2[i * 4] = (byte)i; pl2[i * 4 + 1] = (byte)(255 - i); pl2[i * 4 + 2] = (byte)(i / 2); }
        for (int t = 0; t < PaletteShifts.Tables; t++) for (int i = 0; i < 256; i++) pl2[PaletteShifts.HueOffset + t * 256 + i] = (byte)((i + t) % 256);
        var pl2Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "data", "global", "palette", "ACT1", "pal.pl2")); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pl2Path)!); File.WriteAllBytes(pl2Path, pl2);
        var shifts = PaletteShifts.Load(pl2Path); var foundPl2 = PaletteShifts.Find([root]);
        Require(shifts.IsIdentity(0) && !shifts.IsIdentity(3) && shifts.Transformed(3)[0] == shifts.Base(3) && foundPl2 != null && string.Equals(System.IO.Path.GetFullPath(foundPl2), pl2Path, StringComparison.OrdinalIgnoreCase),
            $"Synthetic pal.pl2 did not parse or was not found under the project (found '{foundPl2}').");
        void Table(string name, string text) => TableData.Write(Semantics.TableFile(project!, name), TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt"));
        Table("superuniques", "Name\tClass\thcIdx\tUtrans\tUtrans(N)\tUtrans(H)\nBishibosh\tfallenshaman1\t0\t11\t11\t11\nBonebreak\tskeleton1\t1\t5\t5\t5\n");
        var uniques = await OpenDocumentAsync(Semantics.TableFile(project!, "superuniques"));
        Require(uniques != null, "Colour-transform smoke could not open superuniques."); uniques!.SelectCell(1, 3); await Task.Delay(50);
        Require(uniques.SelectedCellIsColorTransform && uniques.SelectedColumn == "Utrans", "Utrans was not recognized as a colour-transform column.");
        uniques.SelectCell(1, 1); Require(!uniques.SelectedCellIsColorTransform, "A plain column was offered the colour-transform picker.");
        string? applied = null;
        var card = ColorTransformPicker.Create("superuniques", "Utrans", "5", [root], v => applied = v, ex => throw ex, () => { });
        var host = new Window { Width = 620, Height = 560, Content = card }; host.Show(); await Task.Delay(150);
        var swatches = card.GetVisualDescendants().OfType<ListBox>().Single();
        Require(swatches.ItemCount == PaletteShifts.Tables + 1 && swatches.SelectedIndex < 0 && card.GetVisualDescendants().OfType<Image>().Count() > 10, $"Colour-transform picker did not list every hue table with a swatch ({swatches.ItemCount} rows).");
        using (var image = new RenderTargetBitmap(new PixelSize(620, 560), new Vector(96, 96))) { image.Render(host); image.Save(System.IO.Path.Combine(output, "color-transforms.png"), PngBitmapEncoderOptions.Default); }
        swatches.SelectedIndex = 8; await Task.Delay(50);
        Require(applied == "7", $"Choosing a swatch did not hand back its table number (got '{applied}').");
        host.Close();
        uniques.SelectCell(1, 3); uniques.ShowColorTransformPicker(null); await Task.Delay(100);
        File.Delete(pl2Path);
        tabs.Clear(); previewTab = null;
    }
}
