using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
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
