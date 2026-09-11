using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ModStudio.Core;

namespace ModStudio.App;

public sealed class SpecialistPreviewPane : Grid
{
    public static bool Supports(string file) => SpecialistPreview.Supports(file) || Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif";
    private readonly string file;
    private readonly Image picture = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox frames = new() { MinWidth = 160, MaxWidth = 280 };
    private readonly ComboBox zoom = new() { ItemsSource = new[] { "Fit", "100%", "200%", "400%" }, SelectedIndex = 0, Width = 85 };
    private readonly ComboBox channels = new() { ItemsSource = new[] { "RGBA", "RGB", "Alpha" }, Width = 85 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(10) };
    private readonly TextBox details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180 };
    private readonly TextBox hex = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new("Consolas, Menlo, monospace"), IsVisible = false };
    private readonly ScrollViewer scroll;
    private readonly Border backdrop;
    private readonly ProgressBar busy = new() { IsIndeterminate = true, Height = 4 };
    private PreviewAsset? asset;
    private Bitmap? bitmap;
    private byte[]? palette;
    private int revision;
    private bool attached, changingFrames;
    internal void SelectFrame(int index) => frames.SelectedIndex = index;
    internal string HexText => hex.Text ?? "";
    public bool Ready => bitmap != null;
    public string PreviewStatus => status.Text ?? "";
    public SpecialistPreviewPane(string path)
    {
        file = path; RowDefinitions = new("Auto,Auto,*,Auto");
        var toolbar = new WrapPanel { Margin = new(6), Orientation = Orientation.Horizontal };
        toolbar.Children.Add(frames); toolbar.Children.Add(zoom); channels.SelectedIndex = Path.GetExtension(path).Equals(".texture", StringComparison.OrdinalIgnoreCase) ? 1 : 0; toolbar.Children.Add(channels);
        ToolTip.SetTip(channels, "RGBA shows transparency; RGB ignores alpha; Alpha displays the alpha channel.");
        var background = new ComboBox { ItemsSource = new[] { "Dark", "Light", "Magenta" }, SelectedIndex = 0, Width = 100 };
        ToolTip.SetTip(background, "Preview background for inspecting transparency"); toolbar.Children.Add(background);
        var toggle = new Button { Content = "Hex" }; toggle.Click += (_, _) => { hex.IsVisible = !hex.IsVisible; scroll!.IsVisible = !hex.IsVisible; toggle.Content = hex.IsVisible ? "Preview" : "Hex"; }; toolbar.Children.Add(toggle);
        var reload = new Button { Content = "Reload" }; reload.Click += async (_, _) => await LoadAsync(); toolbar.Children.Add(reload);
        var choosePalette = new Button { Content = "Load palette…", IsVisible = Path.GetExtension(path).Equals(".dc6", StringComparison.OrdinalIgnoreCase) };
        choosePalette.Click += async (_, _) => {
            try
            {
                var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new() { Title = "Choose Diablo II pal.dat (768 BGR bytes)", AllowMultiple = false });
                if (files.FirstOrDefault() is not { } selected) return;
                using var stream = await selected.OpenReadAsync(); var data = new byte[769]; int count = 0, read;
                while (count < data.Length && (read = await stream.ReadAsync(data.AsMemory(count))) != 0) count += read;
                Storage.Require(count == 768, "Palette must contain exactly 768 bytes."); palette = data[..768]; await LoadAsync();
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }; toolbar.Children.Add(choosePalette);
        Children.Add(toolbar); SetRow(busy, 1); Children.Add(busy);
        backdrop = new Border { Background = new SolidColorBrush(Color.Parse("#303036")), Child = picture };
        scroll = new ScrollViewer { Content = backdrop, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SetRow(scroll, 2); Children.Add(scroll); SetRow(hex, 2); Children.Add(hex);
        var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(new Expander { Header = "Format details", Content = details }); SetRow(footer, 3); Children.Add(footer);
        background.SelectionChanged += (_, _) => backdrop.Background = background.SelectedIndex switch { 1 => Brushes.White, 2 => Brushes.Magenta, _ => new SolidColorBrush(Color.Parse("#303036")) };
        frames.SelectionChanged += async (_, _) => { if (!changingFrames && asset != null) await RenderFrameAsync(); };
        channels.SelectionChanged += async (_, _) => { if (attached && bitmap != null) await RenderFrameAsync(); };
        zoom.SelectionChanged += (_, _) => ResizePreview();
        scroll.SizeChanged += (_, _) => ResizePreview();
        AttachedToVisualTree += async (_, _) => { attached = true; await LoadAsync(); };
        DetachedFromVisualTree += (_, _) => { attached = false; revision++; asset = null; picture.Source = null; bitmap?.Dispose(); bitmap = null; };
    }
    private async Task LoadAsync()
    {
        int current = ++revision; asset = null; details.Text = ""; frames.IsEnabled = false; busy.IsVisible = true; status.Text = "Loading read-only preview…";
        try
        {
            var result = await Task.Run(() => {
                Storage.NoLinks(file); using var input = File.OpenRead(file); var prefix = new byte[(int)Math.Min(4096, input.Length)]; input.ReadExactly(prefix);
                var text = new StringBuilder();
                for (int offset = 0; offset < prefix.Length; offset += 16) { var line = prefix.Skip(offset).Take(16).ToArray(); text.AppendLine($"{offset:X8}  {Convert.ToHexString(line).Chunk(2).Select(c => new string(c)).Aggregate("", (a, b) => a + b + " "),-48} {new string(line.Select(b => b is >= 32 and <= 126 ? (char)b : '.').ToArray())}"); }
                PreviewAsset? decoded = null; string? error = null;
                try { if (SpecialistPreview.Supports(file)) decoded = SpecialistPreview.Load(file, palette); } catch (Exception ex) { error = ex.Message; }
                return (Hex: $"{input.Length:N0} bytes · first {prefix.Length:N0} bytes\n\n" + text, Asset: decoded, Error: error);
            });
            if (!attached || current != revision) return;
            hex.Text = result.Hex; if (result.Error != null) throw new InvalidDataException(result.Error); asset = result.Asset; frames.IsEnabled = true;
            changingFrames = true; frames.ItemsSource = asset?.Frames ?? ["Image (first frame)"]; frames.SelectedIndex = asset?.InitialFrame ?? 0; changingFrames = false;
            details.Text = asset?.Summary ?? "Standard image preview. Animated GIF/WebP displays its first frame.";
            await RenderFrameAsync();
        }
        catch (Exception ex) { if (attached && current == revision) { status.Text = "Preview unavailable: " + ex.Message; picture.Source = null; bitmap?.Dispose(); bitmap = null; busy.IsVisible = false; } }
    }
    private async Task RenderFrameAsync()
    {
        int current = ++revision, frame = frames.SelectedIndex, channel = channels.SelectedIndex; var selected = asset; busy.IsVisible = true;
        try
        {
            var pixels = await Task.Run(() => {
                if (selected != null) return selected.Decode(frame);
                Storage.Require(new FileInfo(file).Length <= 64 * 1024 * 1024, "Image exceeds the 64 MiB preview limit.");
                using var input = File.OpenRead(file); using var codec = SkiaSharp.SKCodec.Create(input) ?? throw new InvalidDataException("Unrecognized image format.");
                var info = codec.Info; Storage.Require(info.Width > 0 && info.Height > 0 && (long)info.Width * info.Height <= 16 * 1024 * 1024, "Image exceeds the 16-megapixel preview limit.");
                var rgbaInfo = new SkiaSharp.SKImageInfo(info.Width, info.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
                var bytes = new byte[checked(info.Width * info.Height * 4)];
                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try { Storage.Require(codec.GetPixels(rgbaInfo, handle.AddrOfPinnedObject()) == SkiaSharp.SKCodecResult.Success, "Image data is incomplete or invalid."); } finally { handle.Free(); }
                return new PreviewPixels(info.Width, info.Height, bytes);
            });
            if (channel > 0) await Task.Run(() => {
                for (int i = 0; i < pixels.Rgba.Length; i += 4) { if (channel == 2) pixels.Rgba[i] = pixels.Rgba[i + 1] = pixels.Rgba[i + 2] = pixels.Rgba[i + 3]; pixels.Rgba[i + 3] = 255; }
            });
            if (!attached || current != revision) return;
            var next = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using (var target = next.Lock()) for (int y = 0; y < pixels.Height; y++) Marshal.Copy(pixels.Rgba, y * pixels.Width * 4, target.Address + y * target.RowBytes, pixels.Width * 4);
            picture.Source = next; bitmap?.Dispose(); bitmap = next;
            status.Text = $"Read-only · {channels.SelectedItem} · {pixels.Width} × {pixels.Height} · {Path.GetFileName(file)}" + (selected?.UsesPalette == true && palette == null ? " · grayscale indices (load palette for color)" : Path.GetExtension(file).Equals(".ds1", StringComparison.OrdinalIgnoreCase) ? " · occupancy overview, not full collision / terrain" : ""); ResizePreview();
        }
        catch (Exception ex) { if (attached && current == revision) { picture.Source = null; bitmap?.Dispose(); bitmap = null; status.Text = "Preview unavailable: " + ex.Message; } }
        finally { if (attached && current == revision) busy.IsVisible = false; }
    }
    private void ResizePreview()
    {
        if (bitmap == null) return; bool fit = zoom.SelectedIndex == 0;
        scroll.HorizontalScrollBarVisibility = scroll.VerticalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        double scale = fit ? Math.Min(Math.Max(1, scroll.Bounds.Width) / bitmap.PixelSize.Width, Math.Max(1, scroll.Bounds.Height) / bitmap.PixelSize.Height) : Math.Pow(2, zoom.SelectedIndex - 1);
        picture.Width = bitmap.PixelSize.Width * scale; picture.Height = bitmap.PixelSize.Height * scale;
        RenderOptions.SetBitmapInterpolationMode(picture, BitmapInterpolationMode.None);
    }
}
