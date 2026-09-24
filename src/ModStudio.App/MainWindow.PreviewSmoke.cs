using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    private async Task SmokeColumnGuideAsync(string output)
    {
        var entry = ColumnGuide.Find("skills", "cltdofunc") ?? throw new InvalidOperationException("Missing cltdofunc guide.");
        foreach (int width in new[] { 270, 320, 460 })
        {
            var card = ColumnGuideTooltip.Create("skills", "cltdofunc", entry);
            var host = new Window { Width = width, Height = 800, Content = card, Background = Brushes.Black };
            try
            {
                host.Show();
                for (int attempt = 0; attempt < 100 && card.Bounds.Width == 0; attempt++) await Task.Delay(10);
                host.UpdateLayout();
                var panel = (StackPanel)((ScrollViewer)card).Content!;
                var grid = panel.Children.OfType<Grid>().Single();
                var cells = grid.Children.OfType<TextBlock>().ToArray();
                Require(cells.All(cell => cell.Bounds.Width >= 18), "Guide table squeezed a column at width " + width);
                Require(cells.Where(cell => Grid.GetColumn(cell) == grid.ColumnDefinitions.Count - 1).All(cell => cell.Bounds.Width >= 80),
                    "Guide descriptions lost readable width at " + width);
                Require(cells.Single(cell => Grid.GetRow(cell) == 0 && Grid.GetColumn(cell) == grid.ColumnDefinitions.Count - 1).Bounds.Height < 100,
                    "Short guide description wrapped into excessive height at " + width);
                Require(card.Bounds.Height <= 560, $"Guide card extends past its height limit: {card.Bounds.Height} (desired {card.DesiredSize.Height}, align {card.VerticalAlignment}) at width {width}.");
                Require(grid.RowDefinitions.Count <= ColumnGuideTooltip.TableRows + 1 && panel.Children.OfType<TextBlock>().Any(t => t.Text == ColumnGuideView.OpenHint), "Hover card is not the short form with the searchable-guide hint.");
                await Task.Delay(120);
                using var image = new RenderTargetBitmap(new PixelSize(width, 800), new Vector(96, 96));
                image.Render(host); image.Save(System.IO.Path.Combine(output, $"column-guide-{width}.png"), PngBitmapEncoderOptions.Default);
            }
            finally { host.Close(); }
        }
        // The Column Guide tab lists every bundled value, filters them, highlights the current cell's code and keeps the search while only the value changes.
        var guideView = new ColumnGuideView(ex => throw ex);
        var guideHost = new Window { Width = 1200, Height = 260, Content = guideView, Background = Brushes.Black };
        try
        {
            guideHost.Show();
            Require(guideView.Column == null && guideView.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == ColumnGuideView.EmptyText), "Column Guide tab does not explain what appears there before a cell is selected.");
            var skills = TableData.FromTsv(Utf8.GetBytes("skill\tcltdofunc\r\nx\t3\r\n"), "skills", "global/excel/skills.txt");
            guideView.Show(skills, "cltdofunc", "3");
            for (int attempt = 0; attempt < 100 && guideView.Bounds.Width == 0; attempt++) await Task.Delay(10);
            guideHost.UpdateLayout(); await Task.Delay(50); guideHost.UpdateLayout();
            var list = guideView.GetVisualDescendants().OfType<ListBox>().Single();
            var search = guideView.GetVisualDescendants().OfType<TextBox>().Single();
            Require(guideView.Column == "cltdofunc" && list.ItemCount == entry.Table!.Length - (entry.TableHasHeading ? 1 : 0) && list.ItemCount > ColumnGuideTooltip.TableRows, "Column Guide does not list every bundled value.");
            Require(list.SelectedIndex >= 0 && list.SelectedItem?.ToString()?.Contains("IsCurrent = True") == true, "Column Guide did not highlight the current value.");
            Require(list.Bounds.Height > 120 && list.Bounds.Width > 500, $"Column Guide value list does not fill the panel: {list.Bounds}.");
            var term = entry.Table![5][1][..Math.Min(6, entry.Table[5][1].Length)];
            search.Text = term; await Task.Delay(30);
            Require(list.ItemCount >= 1 && list.ItemCount < entry.Table.Length - 1, "Column Guide search did not filter the values.");
            guideView.Show(skills, "cltdofunc", "4"); guideHost.UpdateLayout();
            Require(guideView.GetVisualDescendants().OfType<TextBox>().Single().Text == term, "Column Guide lost its search when only the cell value changed.");
            Require(!entry.TableHasHeading && (ColumnGuide.Find("missiles", "pCltDoFunc")?.TableHasHeading ?? false), "Guide table heading detection is wrong for a known headed and a known unheaded table.");
            Require(guideView.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString()?.StartsWith("Open online guide") == true), "Column Guide has no link to the online guide.");
            using var image = new RenderTargetBitmap(new PixelSize(1200, 260), new Vector(96, 96));
            image.Render(guideHost); image.Save(System.IO.Path.Combine(output, "column-guide-tab.png"), PngBitmapEncoderOptions.Default);
            guideView.Show(skills, "skill", "x");
            Require(guideView.GetVisualDescendants().OfType<TextBox>().All(t => t.Text != term), "Column Guide kept the search of a different column.");
            guideView.Show(null, null, null);
            Require(guideView.Column == null, "Column Guide did not return to its empty message.");
        }
        finally { guideHost.Close(); }
    }

    private async Task SmokePreviewsAsync(string output, IEnumerable<string> realAssets)
    {
        var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(output, "synthetic.sprite")); var bytes = new byte[72];
        void Put(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        Put(0, 0x31417053); Put(4, 31 | (2 << 16)); Put(8, 4); Put(12, 2); Put(20, 2);
        for (int pixel = 0; pixel < 8; pixel++) { bytes[40 + pixel * 4 + (pixel % 4 < 2 ? 0 : 2)] = 255; bytes[43 + pixel * 4] = 255; }
        File.WriteAllBytes(file, bytes);
        async Task Wait(Func<bool> ready)
        {
            var until = DateTime.UtcNow.AddSeconds(15); while (!ready() && DateTime.UtcNow < until) await Task.Delay(30);
            Require(ready(), "Specialist preview did not reach the expected state.");
        }
        await OpenDocumentAsync(file, true); var tab = (TabItem)Documents.SelectedItem!; var preview = (SpecialistPreviewPane)tab.Content!;
        await Wait(() => preview.Ready); Require(preview.PreviewStatus.Contains("4 × 2"), "Sprite preview has wrong atlas dimensions.");
        preview.SelectFrame(1); await Wait(() => preview.PreviewStatus.Contains("2 × 2"));
        int decoded = preview.DecodeCount;
        preview.SelectChannel(2); await Wait(() => preview.PreviewStatus.Contains("· Alpha ·"));
        preview.SelectChannel(1); preview.SelectChannel(2); preview.SelectChannel(0);
        await Wait(() => preview.PreviewStatus.Contains("· RGBA ·"));
        Require(preview.DecodeCount == decoded, "Channel switching decoded cached pixels again.");
        preview.SelectFrame(0); await Wait(() => preview.PreviewStatus.Contains("4 × 2"));
        Require(preview.DecodeCount == decoded, "Returning to a cached frame decoded it again.");
        await preview.ReloadAsync();
        Require(preview.Ready && preview.DecodeCount == decoded + 1, "Reload did not invalidate the preview cache.");
        Require(File.ReadAllBytes(file).SequenceEqual(bytes) && preview.HexText.Contains("00000000"), "Preview mutated source or omitted hex inspection.");
        await CloseTabAsync(tab); Require(!tabs.Contains(tab) && !preview.Ready, "Closing preview retained bitmap resources.");
        File.WriteAllBytes(file, bytes[..12]); await OpenDocumentAsync(file, true); tab = (TabItem)Documents.SelectedItem!; preview = (SpecialistPreviewPane)tab.Content!;
        await Wait(() => preview.PreviewStatus.StartsWith("Preview unavailable")); Require(preview.HexText.Length > 20, "Malformed asset lost its hex fallback."); await CloseTabAsync(tab);
        File.WriteAllBytes(file, bytes); var pending = OpenDocumentAsync(file, true); tab = (TabItem)Documents.SelectedItem!; await CloseTabAsync(tab); await pending;
        Require(!tabs.Contains(tab), "Closed preview reopened after loading.");
        var pngFile = System.IO.Path.GetFullPath(System.IO.Path.Combine(output, "synthetic.png"));
        using (var png = new WriteableBitmap(new PixelSize(2, 2), new Vector(96,96), Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul))
        {
            using (var locked = png.Lock()) for (int row = 0; row < 2; row++) System.Runtime.InteropServices.Marshal.Copy(new byte[] { 255,0,0,255,0,0,255,255 }, 0, locked.Address + row * locked.RowBytes, 8);
            png.Save(pngFile, PngBitmapEncoderOptions.Default);
        }
        await OpenDocumentAsync(pngFile, true); tab = (TabItem)Documents.SelectedItem!; preview = (SpecialistPreviewPane)tab.Content!;
        await Wait(() => preview.Ready); Require(preview.PreviewStatus.Contains("2 × 2"), "Standard PNG preview failed."); await CloseTabAsync(tab);
        int number = 0;
        foreach (var real in realAssets)
        {
            await OpenDocumentAsync(real, true); tab = (TabItem)Documents.SelectedItem!; preview = (SpecialistPreviewPane)tab.Content!;
            await Wait(() => preview.Ready || preview.PreviewStatus.StartsWith("Preview unavailable"));
            Require(preview.Ready, real + ": " + preview.PreviewStatus); await Task.Delay(120);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)); bitmap.Render(this);
            bitmap.Save(System.IO.Path.Combine(output, "specialist-" + number++ + ".png"), PngBitmapEncoderOptions.Default);
            await CloseTabAsync(tab);
        }
    }
}
