using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;
public partial class MainWindow
{
    private async Task SmokeItemPreviewsAsync(string output)
    {
        Require(!itemPreviewTab.IsVisible, "Item preview tab is visible without an item table.");
        void Table(string name, string text)
        {
            var table = TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt");
            var dir = System.IO.Path.Combine(project!.Root, "source/tables", name); Require(!Directory.Exists(dir), "Item smoke needs a fresh fixture: " + dir); Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, "records.json"), Json(table.Records)); File.WriteAllText(System.IO.Path.Combine(dir, "schema.json"), Json(table.Schema));
        }
        Table("uniqueitems", "index\tcode\tlvl req\tprop1\tmin1\tmax1\tvery_long_column_name_for_preview_testing\nFirst\thax\t5\tstr\t2\t4\t0\nSecond\thax\t9\tstr\t8\t8\t1\n");
        Table("weapons", "code\tnamestr\tmindam\tmaxdam\tlevelreq\n" + "hax\thax\t3\t6\t1\n");
        Table("properties", "code\tfunc1\tstat1\nstr\t1\tstrength\n");
        Table("itemstatcost", "Stat\tdescfunc\tdescstrpos\nstrength\t19\tstrength\n");
        var dir = System.IO.Path.Combine(project!.Root, "source/strings/preview-test"); Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "records.json"), new JsonArray(new[] { ("First", "First Unique"), ("Second", "Second Unique"), ("hax", "Hand Axe"), ("strength", "%+d to Strength") }.Select(x => (JsonNode)new JsonObject { ["Key"] = x.Item1, ["translations"] = new JsonObject { ["enUS"] = x.Item2 } }).ToArray()).ToJsonString());
        var pane = (await OpenDocumentAsync(System.IO.Path.Combine(project.Root, "source/tables/uniqueitems/records.json"), true))!;
        Require(itemPreviewTab.IsVisible, "Unique item table did not reveal the preview tab.");
        InspectorTabs.SelectedItem = itemPreviewTab;
        await PendingItemPreview; // Filesystem notifications can invalidate the first request.
        await Task.Delay(300); RequestItemPreview(pane, 0, null); await PendingItemPreview;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastItemPreview == null && DateTime.UtcNow < deadline) await Task.Delay(50);
        Require(LastItemPreview?.Name == "First Unique" && LastItemPreview.Lines.Contains("+(2–4) to Strength"), "Item inspector did not render resolved unique properties.");
        RequestItemPreview(pane, 0, null); var stale = PendingItemPreview;
        RequestItemPreview(pane, 1, null); await Task.WhenAll(stale, PendingItemPreview);
        Require(LastItemPreview?.Name == "Second Unique", "A stale hover replaced the latest item.");
        await Task.Delay(100);
        var headerLabel = pane.TableGrid.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => ToolTip.GetTip(t)?.ToString() == "very_long_column_name_for_preview_testing");
        Require(headerLabel != null, "Truncated column header has no full-name tooltip.");
        var codeHeader = pane.TableGrid.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "code" && ToolTip.GetTip(t) is Control);
        Require(codeHeader != null && codeHeader.GetVisualAncestors().OfType<DataGridColumnHeader>().Any(), "Documented column header has no data-guide card.");
        Require(((Control)ToolTip.GetTip(codeHeader!)!).GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("baseline item code") == true), "Column header guide card lacks the d2rdoc description.");
        ToolTip.SetIsOpen(codeHeader!, true); await Task.Delay(200);
        if (((Control)ToolTip.GetTip(codeHeader!)!).GetVisualAncestors().OfType<ToolTip>().FirstOrDefault() is { Bounds.Width: > 0 } guideCard)
            using (var guideImage = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(guideCard.Bounds.Width), (int)Math.Ceiling(guideCard.Bounds.Height)), new Vector(96, 96)))
            { guideImage.Render(guideCard); guideImage.Save(System.IO.Path.Combine(output, "column-guide.png"), PngBitmapEncoderOptions.Default); }
        ToolTip.SetIsOpen(codeHeader!, false);
        Require(itemLocale.ItemsSource is IEnumerable<string> localeChoices && localeChoices.SequenceEqual(project.Locales()) && itemLocale.SelectedItem as string == "enUS", "Locale picker does not offer the project's catalog locales.");
        InspectorTabs.SelectedIndex = 1; await Task.Delay(200);
        var guidedField = RowEditorFields.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => ToolTip.GetTip(p) is Control && p.GetLogicalDescendants().OfType<TextBox>().Any(t => Avalonia.Automation.AutomationProperties.GetName(t) == "prop1"));
        Require(guidedField != null, "Row editor field for prop1 has no data-guide card.");
        Require(RowEditorFields.GetVisualDescendants().OfType<StackPanel>().Any(p => ToolTip.GetTip(p) == null && p.GetLogicalDescendants().OfType<TextBox>().Any(t => Avalonia.Automation.AutomationProperties.GetName(t) == "very_long_column_name_for_preview_testing")), "Undocumented row editor field shows a guide card.");
        InspectorTabs.SelectedItem = itemPreviewTab; await PendingItemPreview;
        var anchor = pane.TableGrid.GetVisualDescendants().OfType<DataGridRow>().First();
        RequestItemPreview(pane, 0, anchor); var abandoned = PendingItemPreview;
        ScheduleItemTooltipClose(); await abandoned; await Task.Delay(450);
        Require(!ToolTip.GetIsOpen(anchor) && ToolTip.GetTip(anchor) == null, "Leaving a row before its hover resolved still opened the tooltip.");
        RequestItemPreview(pane, 0, anchor); await PendingItemPreview;
        Require(ToolTip.GetIsOpen(anchor), "Item hover did not open its tooltip.");
        CancelItemPreview();
        ShowItemTooltip(anchor, new ItemPreviewResult("The Jade Tan Do — long preview", false,
            Enumerable.Repeat("A long item property description with its complete roll range and additional information.", 28).ToArray(),
            ["An unresolved property with a long explanation must remain readable inside the popup."]));
        await Task.Delay(150);
        var tooltip = (ToolTip)ToolTip.GetTip(anchor)!;
        var viewport = (ScrollViewer)tooltip.Content!;
        ScheduleItemTooltipClose();
        await Task.Delay(100);
        Require(ToolTip.GetIsOpen(anchor), "Item tooltip has no pointer-transition grace period.");
        CancelScheduledItemTooltipClose();
        await Task.Delay(350);
        Require(ToolTip.GetIsOpen(anchor), "Entering the item tooltip cannot keep selectable text open.");
        Require(tooltip.Bounds.Width > 400 && viewport.Extent.Width <= viewport.Viewport.Width + 1 && viewport.Offset.X == 0,
            "Item tooltip is width-constrained or horizontally clipped.");
        var title = tooltip.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "The Jade Tan Do — long preview");
        Require(title.TranslatePoint(new Point(), viewport) is { X: >= 0 }, "Tooltip title starts outside the visible viewport.");
        Require(viewport.Extent.Height > viewport.Viewport.Height, "Long tooltip did not provide vertical overflow.");
        using (var tooltipImage = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(tooltip.Bounds.Width), (int)Math.Ceiling(tooltip.Bounds.Height)), new Vector(96,96)))
        { tooltipImage.Render(tooltip); tooltipImage.Save(System.IO.Path.Combine(output, "item-hover.png"), PngBitmapEncoderOptions.Default); }
        CancelItemPreview();
        RequestItemPreview(pane, 1, null); await PendingItemPreview;
        await Task.Delay(150);
        Require(itemPreviewContent.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Second Unique"), "Item card was not laid out in the inspector.");
        var selectable = itemPreviewContent.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
        Require(selectable.Length >= 2 && selectable.Any(t => t.Text?.Contains("+8 to Strength") == true), "Item preview text cannot be selected and copied.");
        var inspectorHeaders = InspectorTabs.Items.OfType<TabItem>().Where(t => t.IsVisible).ToArray();
        Require(inspectorHeaders.All(t => t.Bounds.Height <= 40) && inspectorHeaders.Max(t => t.Bounds.Y) - inspectorHeaders.Min(t => t.Bounds.Y) < 1, "Inspector tabs are oversized or wrap onto multiple rows.");
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96,96))) { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "item-preview.png"), PngBitmapEncoderOptions.Default); }
        RequestItemPreview(pane, 0, anchor); var closing = PendingItemPreview;
        await CloseTabAsync(tabs.First(t => t.Content == pane)); await closing;
        Require(!ToolTip.GetIsOpen(anchor), "Closed item tab retained its tooltip.");
        Require(!itemPreviewTab.IsVisible && InspectorTabs.SelectedIndex == 0, "Leaving item tables did not hide preview and select Details.");
        Table("setitems", "index\titem\tset\nFirst\thax\tTestSet\n");
        var setPane = await OpenDocumentAsync(System.IO.Path.Combine(project.Root, "source/tables/setitems/records.json"));
        Require(itemPreviewTab.IsVisible, "Set item table did not reveal the preview tab.");
        InspectorTabs.SelectedItem = itemPreviewTab;
        var setPreview = PendingItemPreview;
        var schemaPane = await OpenDocumentAsync(System.IO.Path.Combine(project.Root, "source/tables/setitems/schema.json"));
        await setPreview;
        Require(!itemPreviewTab.IsVisible && InspectorTabs.SelectedIndex == 0, "Schema view retained the item-only inspector tab.");
        await CloseTabAsync(tabs.First(t => t.Content == setPane));
        await CloseTabAsync(tabs.First(t => t.Content == schemaPane));
        // Remove fixture-only dependencies so the existing build smoke uses its original source set.
        foreach (var name in new[] { "uniqueitems", "setitems", "weapons", "properties", "itemstatcost" })
        {
            var folder = Inside(project.Root, "source/tables/" + name);
            File.Delete(System.IO.Path.Combine(folder, "records.json")); File.Delete(System.IO.Path.Combine(folder, "schema.json")); Directory.Delete(folder);
        }
        File.Delete(System.IO.Path.Combine(dir, "records.json")); Directory.Delete(dir);
    }
}
