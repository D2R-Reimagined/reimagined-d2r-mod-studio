using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ModStudio.Core;
using static ModStudio.Core.Storage;

namespace ModStudio.App;

public partial class MainWindow
{
    /// <summary>
    /// The particle editor: a synthetic effect in the project opens as an inspector (not hex), lists its layer, shows its
    /// renderer, texture thumbnail, colour and curve, finds the missile JSON that uses it and opens its texture in a tab.
    /// Editing a setting marks the tab unsaved and Save writes it; a recolour through the sliders changes the colour and
    /// the curve, undo and redo step through it; Save as new effect writes a renamed copy and points the missile at it.
    /// A damaged file falls back to a message. With MODSTUDIO_GAME_DATA a vanilla effect is recoloured and saved as a
    /// project copy that overrides it.
    /// </summary>
    private async Task SmokeParticleInspectorAsync(string output)
    {
        async Task Until(Func<bool> condition, string failure)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!condition())
            {
                Require(DateTime.UtcNow < deadline, failure);
                await Dispatcher.UIThread.InvokeAsync(() => UpdateLayout(), DispatcherPriority.Background);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                await Task.Delay(30);
            }
        }
        void Screenshot(string name)
        {
            UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var image = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            image.Render(this); image.Save(System.IO.Path.Combine(output, name), PngBitmapEncoderOptions.Default);
        }
        async Task<ParticleInspectorPane> Open(string path)
        {
            await OpenDocumentAsync(path);
            var pane = (Documents.SelectedItem as TabItem)?.Content as ParticleInspectorPane;
            Require(pane != null, path + " did not open in the particle inspector.");
            await Until(() => pane!.Info != null || pane.Error != null, "Particle inspector never finished reading " + path);
            return pane!;
        }
        var opened = tabs.ToHashSet();
        bool hadData = Directory.Exists(System.IO.Path.Combine(project!.Root, "data"));
        var data = System.IO.Path.Combine(project.Root, "data", "hd");
        var effect = System.IO.Path.Combine(data, "vfx", "particles", "smoke", "fx_smoke.particles");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(effect)!); Directory.CreateDirectory(System.IO.Path.Combine(data, "vfx", "textures")); Directory.CreateDirectory(System.IO.Path.Combine(data, "missiles"));
        var bytes = SmokeEffect(); File.WriteAllBytes(effect, bytes);
        var texture = new byte[40 + 8 + 16 * 16 * 4];
        void Put(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(offset, 4), value);
        Put(0, 0x2845443c); Put(4, 31); Put(8, 16); Put(12, 16); Put(28, 1); Put(36, 16 * 16 * 4); Put(40, 8);
        for (int i = 0; i < 256; i++) { texture[48 + i * 4] = 255; texture[48 + i * 4 + 1] = (byte)(i % 16 * 16); texture[48 + i * 4 + 3] = 255; }
        File.WriteAllBytes(System.IO.Path.Combine(data, "vfx", "textures", "smoke_flame.texture"), texture);
        File.WriteAllText(System.IO.Path.Combine(data, "missiles", "smoke_bolt.json"), "{\"dependencies\":{\"particles\":[{\"path\":\"data/hd/vfx/particles/smoke/fx_smoke.particles\"}]}}");

        var pane = await Open(effect);
        Require(pane.Info != null && pane.LayerItems.Count() == 2, "Inspector did not list the effect overview and its layer: " + pane.Error);
        Require(pane.DetailText.Contains("Baked with PopcornFX 2.9.5") && pane.DetailText.Contains("data/hd/vfx/textures/smoke_flame.texture") && pane.DetailText.Contains("project"), "Overview is missing the version or the project texture.");
        await Until(() => pane.ShownThumbnails == 1, "Texture thumbnail was not drawn.");
        var find = ((Control)pane).GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "Find usages");
        Require(find != null, "Overview has no Find usages button.");
        find!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => pane.UsageSearch is { IsCompleted: true }, "Usage search did not finish.");
        await Until(() => pane.DetailText.Contains("smoke_bolt.json"), "Usage search did not list the missile JSON naming the effect.");
        Require(pane.Usages.Count == 1, "Usage search found the wrong files.");
        pane.SelectLayer(1);
        await Until(() => pane.DetailText.Contains("BILLBOARD RENDERER"), "Layer did not show its renderer.");
        Require(pane.DetailText.Contains("Material vfx/materials/Diablo_Uber.pkma") && pane.DetailText.Contains("General.MipBias") && pane.DetailText.Contains("4 components (colour-like)"), "Layer is missing its material, settings or colour curve.");
        Screenshot("particle-inspector.png");
        var link = ((Control)pane).GetLogicalDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string tip && tip.EndsWith("smoke_flame.texture", StringComparison.OrdinalIgnoreCase));
        var before = tabs.Count; link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => tabs.Count == before + 1 && (Documents.SelectedItem as TabItem)?.Content is SpecialistPreviewPane, "Texture link did not open the texture in its own tab.");
        Require(File.ReadAllBytes(effect).SequenceEqual(bytes), "Inspecting the effect changed it.");
        var particleTab = tabs.First(t => t.Content == pane); Documents.SelectedItem = particleTab;
        await Until(() => pane.DetailText.Contains("BILLBOARD RENDERER"), "Returning to the effect tab lost its layer view.");

        // A setting edit commits when its box loses focus, marks the tab unsaved and saves in place.
        var mipBias = pane.DetailContent!.GetLogicalDescendants().OfType<TextBox>().First(b => b.Text == "-0.5");
        mipBias.Focus(); mipBias.Text = "-1.5"; FocusManager!.Focus(null);
        await Until(() => pane.IsDirty, "Editing General.MipBias did not mark the effect unsaved: " + pane.StatusText);
        Require(((particleTab.Header as StackPanel)?.Children[0] as TextBlock)?.Text?.StartsWith("● ") == true, "Unsaved effect tab has no unsaved marker: " + ((particleTab.Header as StackPanel)?.Children[0] as TextBlock)?.Text + " / " + particleTab.Header?.GetType().Name);
        Require(File.ReadAllBytes(effect).SequenceEqual(bytes), "An unsaved edit reached the disk.");
        await SaveAllAsync();
        Require(!pane.IsDirty && ParticleEffectInfo.Load(effect).Platforms[0].Layers[0].Renderers[0].Properties.Single(p => p.Name == "General.MipBias").Value == "-1.5", "Save all did not write the edited setting.");

        // Recolour through the overview's sliders: the colour constant and the RGBA curve both turn.
        pane.SelectLayer(0); await Until(() => pane.DetailText.Contains("RECOLOUR"), "Overview has no recolour card.");
        var hue = pane.DetailContent!.GetLogicalDescendants().OfType<Slider>().First(); hue.Value = 120;
        pane.DetailContent!.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == "Apply recolour").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => pane.StatusText.StartsWith("Recoloured 2 colours"), "Recolour did not apply: " + pane.StatusText);
        var turned = pane.Info!.Platforms[0].Layers[0];
        Require(Math.Abs(turned.Colors[0].Value[1] - 0.9f) < 1e-4 && Math.Abs(turned.Colors[0].Value[0] - 0.1f) < 1e-4 && turned.Curves[0].Value(0, 1) > turned.Curves[0].Value(0, 0), "A 120° recolour did not turn orange to green.");
        pane.Undo(); Require(pane.Info!.Platforms[0].Layers[0].Colors[0].Value[0] == 0.9f && !pane.IsDirty, "Undo did not restore the saved colours.");
        pane.Redo(); Require(pane.IsDirty && pane.Info!.Platforms[0].Layers[0].Colors[0].Value[1] > 0.5f, "Redo did not reapply the recolour.");
        pane.SelectLayer(1); await Until(() => pane.DetailText.Contains("COLOURS"), "Layer does not list its colour constant.");
        Screenshot("particle-editor-layer.png");
        pane.SelectLayer(0); await Until(() => pane.DetailText.Contains("RECOLOUR"), "Overview did not return.");
        Screenshot("particle-editor-recolour.png");
        async Task Dialog<T>(Task<T> showing, string name)
        {
            await Until(() => OwnedWindows.Count > 0, name + " did not open.");
            var dialog = OwnedWindows[^1]; await Task.Delay(150); AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
            using (var image = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height), new Vector(96, 96))) { image.Render(dialog); image.Save(System.IO.Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default); }
            dialog.Close(); await showing;
        }
        await Dialog(ParticleDialogs.ColorAsync(pane, [2.5f, 0.8f, 0.2f, 1f], "Flame colour"), "particle-dialog-colour");
        await Dialog(ParticleDialogs.PickResourceAsync(pane, "data/hd/vfx/textures/smoke_flame.texture", ".texture", ["data/hd/vfx/textures/smoke_flame.texture", "data/hd/vfx/textures/other.texture"], (p, b) => { }), "particle-dialog-texture");
        await Dialog(ParticleDialogs.CloneAsync(pane, "fx_smoke_copy", [System.IO.Path.Combine(data, "missiles", "smoke_bolt.json")], f => "data/hd/missiles/smoke_bolt.json"), "particle-dialog-clone");

        // Save as new effect: the copy gets the unsaved recolour, the missile now names the copy, the original is untouched.
        var savedOriginal = File.ReadAllBytes(effect); var missile = System.IO.Path.Combine(data, "missiles", "smoke_bolt.json");
        await pane.SaveAsNewAsync("fx_smoke_green", [missile]);
        var copy = System.IO.Path.Combine(data, "vfx", "particles", "smoke", "fx_smoke_green.particles");
        Require(File.Exists(copy) && pane.FilePath == copy && !pane.IsDirty && File.ReadAllBytes(effect).SequenceEqual(savedOriginal), "Save as new effect did not write a separate copy: " + pane.StatusText);
        Require(File.ReadAllText(missile).Contains("data/hd/vfx/particles/smoke/fx_smoke_green.particles") && (particleTab.Tag as string) == copy, "The missile JSON or the tab still names the original.");

        var damaged = System.IO.Path.Combine(data, "vfx", "particles", "smoke", "fx_damaged.particles"); File.WriteAllBytes(damaged, bytes[..60]);
        var broken = await Open(damaged);
        Require(broken.Error != null && broken.Info == null, "Damaged effect did not report an error.");

        if (GameDataFolders().Select(f => HdAppearance.Locate(f, "data/hd/vfx/particles/missiles/expansion_imp_fireball/fx_imp_fireball.particles")).OfType<string>().FirstOrDefault() is { } real)
        {
            var vanilla = await Open(real);
            Require(vanilla.Info != null && vanilla.Platform?.Name == "PC" && vanilla.LayerItems.Count() > 5, "Vanilla fireball did not list its PC layers: " + vanilla.Error);
            vanilla.SelectLayer(2);
            await Until(() => vanilla.DetailText.Contains("RENDERER"), "Vanilla layer did not show its renderer.");
            await vanilla.Thumbnails; await Until(() => vanilla.ShownThumbnails > 0, "Vanilla textures did not draw thumbnails.");
            Screenshot("particle-inspector-vanilla.png");
            // A game-data effect saves as a project copy at its own path; the game data stays as it was.
            var light = vanilla.Info!.Platforms[0].Layers.First(l => l.Name == "Light").Colors[0]; var original = File.ReadAllBytes(real);
            Require(vanilla.Edit("Light colour", f => ParticleEdits.SetColorConstant(f, light.Layer, light.Index, [0.2f, 0.4f, 1f, 1f])) && vanilla.IsDirty, "Vanilla light colour did not change: " + vanilla.StatusText);
            vanilla.Save();
            var overrideFile = System.IO.Path.Combine(data, "vfx", "particles", "missiles", "expansion_imp_fireball", "fx_imp_fireball.particles");
            Require(File.Exists(overrideFile) && vanilla.FilePath == overrideFile && File.ReadAllBytes(real).SequenceEqual(original), "Saving a game-data effect did not write a project copy.");
            Require(ParticleEffectInfo.Load(overrideFile).Platforms[0].Layers.First(l => l.Name == "Light").Colors[0].Value.SequenceEqual([0.2f, 0.4f, 1f, 1f]), "The project copy lacks the new light colour.");
        }
        foreach (var tab in tabs.Where(t => !opened.Contains(t)).ToList()) await CloseTabAsync(tab);
        // Later smokes list the project's HD missiles and textures; leave them as the fixture made them.
        if (!hadData) { Directory.Delete(System.IO.Path.Combine(project.Root, "data"), true); return; }
        Directory.Delete(System.IO.Path.GetDirectoryName(effect)!, true);
        File.Delete(System.IO.Path.Combine(data, "vfx", "textures", "smoke_flame.texture")); File.Delete(System.IO.Path.Combine(data, "missiles", "smoke_bolt.json"));
    }

    /// <summary>One PC layer, "Flame": a billboard with a texture, a float setting, an orange colour constant (also in its compiled script) and an RGBA curve over life.</summary>
    private static byte[] SmokeEffect()
    {
        static byte[] U(params uint[] values) { var b = new byte[values.Length * 4]; for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), values[i]); return b; }
        static byte[] A(params uint[] values) => U([(uint)values.Length, .. values]);
        static uint F(float f) => BitConverter.SingleToUInt32Bits(f);
        byte[] Orange = U(F(0.9f), F(0.4f), F(0.1f), F(1));
        var file = new ParticleFile();
        file.Strings.AddRange(["CParticleEffect", "CLayerGraphCompileCache", "CLayerGraphCompileCache_LayerSlot", "CLayerCompileCache", "CLayerCompileCacheRenderer", "CLayerCompileCacheRendererProperty",
            "CLayerCompileCacheSampler", "CParticleNodeSamplerData_Curve", "PC", "Flame", "vfx/materials/Diablo_Uber.pkma", "Blend", "Blend.TextureBase", "data/hd/vfx/textures/smoke_flame.texture", "General.MipBias", "FlameColor", "CCompilerBlobCache"]);
        file.Classes.AddRange([.. Enumerable.Range(0, 8), 16]);
        (int Class, byte[] Payload)[] chunks =
        [
            (0, ParticleObject.Encode((1, A(2)))),
            (1, ParticleObject.Encode((0, U(8)), (3, A(3)))),
            (2, ParticleObject.Encode((0, U(4)))),
            (3, ParticleObject.Encode((0, [.. U(2), .. Orange, .. Orange]), (1, [.. U(2), .. Orange, .. U(1, 1, 1, 1)]), (7, A(9)), (8, A(5)), (9, A(11)), (20, U(9)))),
            (4, ParticleObject.Encode((2, A(6, 7, 8)), (4, U(10)))),
            (5, ParticleObject.Encode((0, U(11)), (2, U(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue)))),
            (5, ParticleObject.Encode((0, U(12)), (1, U(12)), (3, U(13)))),
            (5, ParticleObject.Encode((0, U(14)), (1, U(7)), (2, U(F(-0.5f), 0, 0, 0)))),
            (6, ParticleObject.Encode((0, U(15)), (1, U(10)))),
            (7, ParticleObject.Encode((17, A(0, F(0.4f), F(1))), (18, A(F(4), F(2), F(0.5f), F(1), F(1), F(0.4f), F(0.1f), F(0.8f), F(0.2f), F(0), F(0), F(0))))),
        ];
        foreach (var (cls, payload) in chunks) file.Chunks.Add(new ParticleChunk(cls, payload));
        file.Chunks.Add(new ParticleChunk(16, ParticleObject.Encode((2, [.. U(10, 7), .. Orange, .. Orange, .. U(9)]))));
        return file.Write();
    }
}
