using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

public sealed partial class ParticleInspectorPane
{
    /// <summary>A curve or colour constant the recolour card can change, with the colours it holds now.</summary>
    private sealed record RecolourTarget(string Key, string Label, int? Curve, (int Layer, int Index)? Constant, float[][] Colors, bool Likely, string Note);
    private double recolourHue, recolourSaturation = 1, recolourBrightness = 1;
    private readonly HashSet<string> recolourSkipped = [], recolourChosen = [];

    private Control LayerView(ParticleLayer layer)
    {
        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(Title(layer.Name, layer.Renderers.Length == 0 ? "Draws nothing itself: it spawns particles or events for other layers." : Count(layer.Renderers.Length, "renderer")));
        foreach (var renderer in layer.Renderers) root.Children.Add(RendererCard(renderer));
        if (layer.Colors.Length > 0)
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var c in layer.Colors) list.Children.Add(ConstantRow(layer, c));
            root.Children.Add(Card("Colours", list, "Colour constants of this layer's script. Equal colours in one layer change together."));
        }
        if (layer.Curves.Length > 0)
        {
            var curves = new StackPanel { Spacing = 14 };
            foreach (var curve in layer.Curves) curves.Children.Add(CurveView(curve));
            root.Children.Add(Card("Curves", curves, "Values the layer's script samples over time (drawn with straight lines between keys). Edit a key's numbers, or its colour swatch; key times stay as baked."));
        }
        var others = layer.Samplers.Where(s => s.Kind != "Curve").ToList();
        if (others.Count > 0)
        {
            var list = new StackPanel { Spacing = 3 };
            foreach (var s in others) list.Children.Add(Line($"{s.Name} · {s.Kind}", Muted));
            root.Children.Add(Card("Other samplers", list));
        }
        return root;
    }

    private Control RendererCard(ParticleRenderer renderer)
    {
        var panel = new StackPanel { Spacing = 10 };
        if (renderer.Material != null) panel.Children.Add(Line("Material " + renderer.Material, Muted));
        var chips = new WrapPanel();
        foreach (var feature in renderer.Features)
            chips.Children.Add(new Border { Background = Chip, CornerRadius = new(3), Padding = new(6, 2), Margin = new(0, 0, 6, 6), Child = new TextBlock { Text = feature, FontSize = 11, Foreground = Text } });
        if (chips.Children.Count > 0) panel.Children.Add(chips);
        bool all = showDisabled.IsChecked == true;
        foreach (var p in renderer.Properties.Where(p => p.Path != null && (p.FeatureEnabled || all))) panel.Children.Add(FileRow(p.Path!, p));
        var settings = renderer.Properties.Where(p => p.Type != 0 && p.Path == null && p.Type is not (12 or 13 or 14 or 15) && (p.FeatureEnabled || all)).ToList();
        if (settings.Count > 0)
        {
            var grid = Table(["Setting", "Type", "Value"]);
            foreach (var p in settings)
                AddRow(grid, [new TextBlock { Text = p.Name, FontSize = 12, Foreground = p.FeatureEnabled ? Text : Faint }, new TextBlock { Text = p.TypeName, FontSize = 12, Foreground = Muted }, SettingEditor(p)],
                    p.FeatureEnabled ? null : "This setting belongs to a feature the renderer leaves off, so the game ignores it.");
            panel.Children.Add(grid);
        }
        return Card($"{renderer.Kind} renderer", panel);
    }

    private Control SettingEditor(ParticleProperty p)
    {
        if (!p.Editable) return new TextBlock { Text = p.Value, FontSize = 12, Foreground = Muted };
        if (p.Type == 1)
        {
            var check = new CheckBox { IsChecked = p.Value == "true" };
            check.IsCheckedChanged += (_, _) => Edit($"{p.Name} = {(check.IsChecked == true ? "true" : "false")}", f => ParticleEdits.SetSetting(f, p.Reference, ParticleEdits.Ints(check.IsChecked == true ? 1 : 0)));
            return check;
        }
        return NumberBox(p.Value, p.Width == 1 ? 90 : 60 * p.Width, text =>
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != p.Width) { status.Text = $"{p.Name} needs {p.Width} number{(p.Width == 1 ? "" : "s")} separated by commas."; return false; }
            if (p.IsFloat)
            {
                var values = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++) if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !float.IsFinite(values[i])) { status.Text = $"{p.Name}: '{parts[i]}' is not a number."; return false; }
                return Edit($"{p.Name} = {text}", f => ParticleEdits.SetSetting(f, p.Reference, ParticleEdits.Floats(values)));
            }
            var ints = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out ints[i])) { status.Text = $"{p.Name}: '{parts[i]}' is not a whole number."; return false; }
            return Edit($"{p.Name} = {text}", f => ParticleEdits.SetSetting(f, p.Reference, ParticleEdits.Ints(ints)));
        });
    }

    /// <summary>A text box that commits on Enter or leaving it; a rejected value reverts. Escape reverts.</summary>
    private static TextBox NumberBox(string text, double width, Func<string, bool> commit)
    {
        var box = new TextBox { Text = text, Width = width, FontSize = 12, Padding = new(4, 1), MinHeight = 24 };
        void Commit() { var value = (box.Text ?? "").Trim(); if (value == text) return; if (!commit(value)) box.Text = text; }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { box.Text = text; e.Handled = true; } };
        return box;
    }

    private Control ConstantRow(ParticleLayer layer, ParticleColorConstant c)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var swatch = new Button { Content = Swatch(c.Value, 40, 22), Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0) };
        ToolTip.SetTip(swatch, "Pick a new colour");
        swatch.Click += async (_, _) =>
        {
            var picked = await ParticleDialogs.ColorAsync(this, c.Value, $"{layer.Name} colour");
            if (picked != null) Edit($"{layer.Name} colour → {Numbers(picked)}", f => ParticleEdits.SetColorConstant(f, c.Layer, c.Index, picked));
        };
        row.Children.Add(swatch);
        row.Children.Add(NumberBox(Numbers(c.Value), 200, text =>
        {
            var values = ParseFloats(text, c.Width, "This colour");
            return values != null && Edit($"{layer.Name} colour → {Numbers(values)}", f => ParticleEdits.SetColorConstant(f, c.Layer, c.Index, values));
        }));
        row.Children.Add(new TextBlock { Text = c.Width == 4 ? "RGBA" : "RGB", FontSize = 11, Foreground = Faint, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private float[]? ParseFloats(string text, int count, string what)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries); var values = new float[parts.Length];
        if (parts.Length != count) { status.Text = $"{what} needs {count} numbers separated by commas."; return null; }
        for (int i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !float.IsFinite(values[i])) { status.Text = $"'{parts[i]}' is not a number."; return null; }
        return values;
    }

    private Control CurveView(ParticleCurve curve)
    {
        const double width = 360, height = 90;
        var panel = new StackPanel { Spacing = 4 };
        string[] names = curve.Dimension switch { 4 => ["R", "G", "B", "A"], 3 => ["X/R", "Y/G", "Z/B"], 2 => ["X", "Y"], _ => ["Value"] };
        float min = Math.Min(0, curve.Values.Min()), max = Math.Max(1, curve.Values.Max());
        panel.Children.Add(Line($"{curve.Name} · {curve.Times.Length} keys · {(curve.Dimension == 4 ? "4 components (colour-like)" : curve.Dimension + " component" + (curve.Dimension == 1 ? "" : "s"))} · {F(min)} to {F(max)}", Muted));
        var canvas = new Canvas { Width = width, Height = height, Background = PlotBackground, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        double X(float t) => (t - curve.Times[0]) / Math.Max(1e-6, curve.Times[^1] - curve.Times[0]) * (width - 8) + 4;
        double Y(float v) => height - 4 - (v - min) / Math.Max(1e-6, max - min) * (height - 8);
        for (int c = 0; c < curve.Dimension; c++)
        {
            var line = new Polyline { Stroke = curve.Dimension == 1 ? Channels[3] : Channels[Math.Min(c, 3)], StrokeThickness = 1.5, Points = [.. Enumerable.Range(0, curve.Times.Length).Select(k => new Point(X(curve.Times[k]), Y(curve.Value(k, c))))] };
            canvas.Children.Add(line);
            for (int k = 0; k < curve.Times.Length; k++) { var dot = new Ellipse { Width = 4, Height = 4, Fill = line.Stroke }; Canvas.SetLeft(dot, X(curve.Times[k]) - 2); Canvas.SetTop(dot, Y(curve.Value(k, c)) - 2); canvas.Children.Add(dot); }
        }
        panel.Children.Add(canvas);
        if (curve.Dimension >= 3) panel.Children.Add(Strip([.. Enumerable.Range(0, curve.Times.Length).Select(k => KeyColor(curve, k))], curve.Times, width));
        var keys = Table(["Time", .. names, ""]);
        for (int k = 0; k < curve.Times.Length; k++)
        {
            int key = k;
            var cells = new List<Control> { new TextBlock { Text = F(curve.Times[k]), FontSize = 12, Foreground = Muted } };
            for (int c = 0; c < curve.Dimension; c++)
            {
                int component = c;
                cells.Add(NumberBox(F(curve.Value(k, c)), 64, text =>
                {
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !float.IsFinite(v)) { status.Text = $"'{text}' is not a number."; return false; }
                    var values = (float[])curve.Values.Clone(); values[key * curve.Dimension + component] = v;
                    return Edit($"{curve.Name} key {key + 1} {names[component]} = {F(v)}", f => ParticleEdits.SetCurve(f, curve.Reference, values));
                }));
            }
            if (curve.Dimension >= 3)
            {
                var swatch = new Button { Content = Swatch(KeyColor(curve, k)), Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0) };
                ToolTip.SetTip(swatch, "Pick this key's colour");
                swatch.Click += async (_, _) =>
                {
                    var picked = await ParticleDialogs.ColorAsync(this, KeyColor(curve, key), $"{curve.Name} key {key + 1}");
                    if (picked == null) return;
                    var values = (float[])curve.Values.Clone(); picked.CopyTo(values, key * curve.Dimension);
                    Edit($"{curve.Name} key {key + 1} colour → {Numbers(picked)}", f => ParticleEdits.SetCurve(f, curve.Reference, values));
                };
                cells.Add(swatch);
            }
            else cells.Add(new TextBlock());
            AddRow(keys, [.. cells]);
        }
        panel.Children.Add(keys);
        return panel;
    }
    private static float[] KeyColor(ParticleCurve curve, int k) => curve.Values[(k * curve.Dimension)..(k * curve.Dimension + curve.Dimension)];

    /// <summary>Colours over time as a strip; a single colour fills it.</summary>
    private static Border Strip(float[][] colors, float[]? times, double width, double height = 14)
    {
        var stops = new GradientStops();
        float first = times?[0] ?? 0, span = times == null ? 1 : Math.Max(1e-6f, times[^1] - times[0]);
        for (int k = 0; k < colors.Length; k++) stops.Add(new GradientStop(ToColor(colors[k]), times == null ? (colors.Length == 1 ? 0 : k / (double)(colors.Length - 1)) : (times[k] - first) / span));
        if (colors.Length == 1) stops.Add(new GradientStop(ToColor(colors[0]), 1));
        return new Border { Width = width, Height = height, CornerRadius = new(2), HorizontalAlignment = HorizontalAlignment.Left, Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative), GradientStops = stops } };
    }

    // ── Recolour ───────────────────────────────────────────────────────────────────────────────────────

    private static List<RecolourTarget> RecolourTargets(ParticlePlatform platform)
    {
        var targets = new List<RecolourTarget>();
        foreach (var layer in platform.Layers)
        {
            foreach (var c in layer.Colors)
                targets.Add(new RecolourTarget($"const:{c.Layer}:{c.Index}", $"{layer.Name} · colour", null, (c.Layer, c.Index), [c.Value], true, c.Width == 4 ? "RGBA constant" : "RGB constant"));
            foreach (var curve in layer.Curves.Where(c => c.Dimension >= 3).DistinctBy(c => c.Reference))
            {
                bool colourLike = curve.Dimension == 4 && curve.Values.All(v => v >= 0);
                targets.Add(new RecolourTarget($"curve:{curve.Reference}", $"{layer.Name} · {curve.Name}", curve.Reference, null, [.. Enumerable.Range(0, curve.Times.Length).Select(k => KeyColor(curve, k))], colourLike,
                    curve.Dimension == 4 ? "RGBA curve" : "3-component curve: could be a colour or a direction"));
            }
        }
        return targets;
    }

    private Control RecolourCard(ParticlePlatform platform)
    {
        var targets = RecolourTargets(platform);
        bool Chosen(RecolourTarget t) => t.Likely ? !recolourSkipped.Contains(t.Key) : recolourChosen.Contains(t.Key);
        var panel = new StackPanel { Spacing = 10 };
        var rows = new StackPanel { Spacing = 6 };
        var afters = new List<(RecolourTarget Target, ContentControl Holder)>();
        ColorShift Shift() => new(recolourHue, recolourSaturation, recolourBrightness);
        void Preview()
        {
            var shift = Shift();
            foreach (var (t, holder) in afters) holder.Content = Strip([.. t.Colors.Select(c => (float[])[.. shift.Apply(c[..3]), .. c[3..]])], null, 120);
        }
        Control SliderRow(string label, double min, double max, double value, Action<double> set, Func<double, string> format)
        {
            var row = new Grid { ColumnDefinitions = new("110,*,70") };
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new(0, -6) };
            var shown = new TextBlock { Text = format(value), FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            slider.ValueChanged += (_, e) => { set(e.NewValue); shown.Text = format(e.NewValue); Preview(); };
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            SetColumn(slider, 1); row.Children.Add(slider); SetColumn(shown, 2); row.Children.Add(shown);
            return row;
        }
        panel.Children.Add(SliderRow("Hue", -180, 180, recolourHue, v => recolourHue = v, v => $"{v:+0;-0;0}°"));
        panel.Children.Add(SliderRow("Saturation", 0, 2, recolourSaturation, v => recolourSaturation = v, v => $"{v * 100:0}%"));
        panel.Children.Add(SliderRow("Brightness", 0, 3, recolourBrightness, v => recolourBrightness = v, v => $"{v * 100:0}%"));
        foreach (var t in targets)
        {
            var row = new Grid { ColumnDefinitions = new("Auto,120,24,120,*") };
            var check = new CheckBox { IsChecked = Chosen(t), VerticalAlignment = VerticalAlignment.Center };
            check.IsCheckedChanged += (_, _) =>
            {
                bool on = check.IsChecked == true;
                if (t.Likely) { if (on) recolourSkipped.Remove(t.Key); else recolourSkipped.Add(t.Key); }
                else { if (on) recolourChosen.Add(t.Key); else recolourChosen.Remove(t.Key); }
            };
            ToolTip.SetTip(check, t.Note);
            var before = Strip(t.Colors, null, 120); var after = new ContentControl();
            var label = new TextBlock { Text = t.Label, FontSize = 12, Foreground = t.Likely ? Text : Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(label, t.Note);
            row.Children.Add(check); SetColumn(before, 1); row.Children.Add(before);
            var arrow = new TextBlock { Text = "→", Foreground = Faint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; SetColumn(arrow, 2); row.Children.Add(arrow);
            SetColumn(after, 3); row.Children.Add(after); SetColumn(label, 4); row.Children.Add(label);
            rows.Children.Add(row); afters.Add((t, after));
        }
        panel.Children.Add(rows);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "Apply recolour", Classes = { "accent" } };
        apply.Click += (_, _) =>
        {
            var chosen = targets.Where(Chosen).ToList();
            if (chosen.Count == 0) { status.Text = "Tick at least one colour to recolour."; return; }
            var shift = Shift();
            if (Edit($"Recoloured {Count(chosen.Count, "colour")} (hue {recolourHue:+0;-0;0}°, saturation {recolourSaturation * 100:0}%, brightness {recolourBrightness * 100:0}%)",
                f => ParticleEdits.Recolor(f, shift, chosen.Where(t => t.Curve != null).Select(t => t.Curve!.Value), chosen.Where(t => t.Constant != null).Select(t => t.Constant!.Value))))
            { recolourHue = 0; recolourSaturation = recolourBrightness = 1; ShowSelection(); }
        };
        var reset = new Button { Content = "Reset sliders" };
        reset.Click += (_, _) => { recolourHue = 0; recolourSaturation = recolourBrightness = 1; ShowSelection(); };
        buttons.Children.Add(apply); buttons.Children.Add(reset); panel.Children.Add(buttons);
        Preview();
        return Card("Recolour", panel, "Turns the hue, saturation and brightness of the effect's colour curves and colour constants together. Textures keep their own colours: swap a layer's gradient or mask texture to change those. Untick anything that is not a colour.");
    }
}
