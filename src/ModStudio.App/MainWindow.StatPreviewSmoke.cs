using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// The stat preview over a property whose authored maximum does not fit its stat's Save Bits, so the overflow check,
    /// the tooltip and the "Used by" list are all exercised. The fixture tables are removed afterwards.
    /// </summary>
    private async Task SmokeStatPreviewAsync(string output)
    {
        string[] names = ["properties", "itemstatcost", "uniqueitems"];
        foreach (var name in names) Require(!File.Exists(TableData.FileFor(project!, "tables", name)), "Stat preview smoke needs a fresh fixture: " + name);
        Require(!statPreviewTab.IsVisible, "Stat preview tab is visible without a properties or itemstatcost table.");
        void Table(string name, string text) => TableData.Write(TableData.FileFor(project!, "tables", name), TableData.FromTsv(Utf8.GetBytes(text), name, "global/excel/" + name + ".txt"));
        Table("properties", "code\tfunc1\tstat1\nstr\t1\tstrength\n");
        Table("itemstatcost", "Stat\tSave Bits\tSave Add\tdescfunc\tdescstrpos\tdescstrneg\nstrength\t8\t32\t19\tModStr1a\tModStr1a\n");
        Table("uniqueitems", "index\tcode\tprop1\tmin1\tmax1\nTest Ring\trin\tstr\t5\t300\n");
        var catalogFile = TableData.FileFor(project!, "strings", "stat-preview-test");
        TableData.Write(catalogFile, new TableData(
            new JsonObject { ["schemaVersion"] = 1, ["category"] = "stat-preview-test", ["locales"] = new JsonArray("enUS"), ["target"] = "local/lng/strings/stat-preview-test.json" },
            new JsonArray(new JsonObject { ["Key"] = "ModStr1a", ["translations"] = new JsonObject { ["enUS"] = "%+d to Strength" } })));

        var pane = (await OpenDocumentAsync(TableData.FileFor(project!, "tables", "properties"), true))!;
        Require(statPreviewTab.IsVisible && !skillPreviewTab.IsVisible && !missilePreviewTab.IsVisible, "The properties table did not reveal only the stat preview tab.");
        InspectorTabs.SelectedItem = statPreviewTab;
        await PendingStatPreview; // Filesystem notifications can invalidate the first request.
        pane.SelectCell(0, 0); await Task.Delay(200); await PendingStatPreview;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastStatPreview?.Name != "str" && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingStatPreview; }
        var str = LastStatPreview;
        Require(str?.Name == "str", $"Stat inspector did not resolve the selected property: {str?.Name}.");
        var stat = str!.Sections.Single(s => s.Title == "Stat strength").Lines;
        Require(stat.Contains("  300 → +300 to Strength") && stat.Any(l => l.Contains("-32 to 223")), "Stat section is missing its tooltip or save range: " + string.Join(" | ", stat));
        Require(str.Issues.Any(i => i.Contains("Test Ring") && i.Contains("300 is outside")), "The Save Bits overflow was not reported: " + string.Join(" | ", str.Issues));
        await Task.Delay(150); UpdateLayout();
        var rendered = statPreviewContent.GetVisualDescendants().OfType<TextBlock>().Select(PreviewCards.RenderedText).ToArray();
        Require(rendered.Contains("str") && rendered.Contains("FUNCTIONS") && rendered.Contains("STAT STRENGTH") && rendered.Contains("USED BY") && rendered.Any(t => t.StartsWith("Problems")),
            $"Stat card was not laid out with its sections: [{string.Join(" | ", rendered.Take(12))}] ({rendered.Length} blocks).");
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)))
        { bitmap.Render(this); bitmap.Save(System.IO.Path.Combine(output, "stat-preview.png"), PngBitmapEncoderOptions.Default); }
        // A sample typed above replaces the authored one.
        statMin.Value = 7; statMax.Value = 7;
        deadline = DateTime.UtcNow.AddSeconds(8);
        while (LastStatPreview?.Sample.StartsWith("min 7") != true && DateTime.UtcNow < deadline) { await Task.Delay(60); await PendingStatPreview; }
        Require(LastStatPreview?.Sections.Single(s => s.Title == "Stat strength").Lines.Contains("  7 → +7 to Strength") == true, "Sample inputs did not re-render the tooltip.");
        statMin.Value = null; statMax.Value = null;
        await CloseTabAsync(tabs.First(t => t.Content == pane));
        Require(!statPreviewTab.IsVisible && InspectorTabs.SelectedIndex == 0, "Leaving the properties table did not hide the preview and select Details.");
        foreach (var name in names) File.Delete(TableData.FileFor(project!, "tables", name));
        File.Delete(catalogFile);
    }
}
