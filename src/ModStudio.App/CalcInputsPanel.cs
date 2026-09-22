using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The values the skill and missile previews assume, shared between them so a synergy level set while reading a skill
/// still applies to the missiles it fires: the level to show, other skills' levels, stat totals and the character level.
/// </summary>
internal sealed class CalcInputState
{
    public int Level { get; set; } = 1;
    public int CharacterLevel { get; set; } = CalcAssumptions.DefaultCharacterLevel;
    public Dictionary<string, int> SkillLevels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> Stats { get; } = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();
    /// <summary>A copy for a worker: the resolver must not see later edits to these dictionaries.</summary>
    public CalcPreviewOptions Options() => new(Level, new CalcAssumptions
    {
        CharacterLevel = CharacterLevel,
        SkillLevels = new Dictionary<string, int>(SkillLevels, StringComparer.OrdinalIgnoreCase),
        Stats = new Dictionary<string, long>(Stats, StringComparer.OrdinalIgnoreCase)
    });
}

/// <summary>
/// The inputs above a skill or missile card: the level to show, then one number box per assumption the last resolution
/// read. The boxes are rebuilt only when that set of assumptions changes, so typing into one never loses focus.
/// </summary>
internal sealed class CalcInputsPanel : StackPanel
{
    private readonly CalcInputState state;
    private readonly NumericUpDown level = new() { Minimum = 1, Maximum = 99, Value = 1, Width = 115, FormatString = "0" };
    private readonly WrapPanel assumptions = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock assumptionsLabel = new() { Text = "ASSUMED BY THE CALCULATIONS", Foreground = Brushes.Tan, FontSize = 11, Margin = new(0, 6, 0, 2), IsVisible = false };
    private string shown = "";
    private readonly List<(CalcInput Input, NumericUpDown Box)> boxes = [];
    private bool updating;

    public CalcInputsPanel(CalcInputState state, Control locale)
    {
        this.state = state; Spacing = 2; Margin = new(0, 0, 0, 8);
        // Wraps rather than clipping the locale picker in a narrow inspector.
        var top = new WrapPanel { Orientation = Orientation.Horizontal };
        Control Labelled(string text, Control input)
        {
            var field = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 16, 4) };
            field.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }); field.Children.Add(input);
            return field;
        }
        top.Children.Add(Labelled("Level", level));
        top.Children.Add(Labelled("Locale", locale));
        ToolTip.SetTip(level, "The level the tooltip and calculations are shown at. The level table always covers every level.");
        Children.Add(top); Children.Add(assumptionsLabel); Children.Add(assumptions);
        level.ValueChanged += (_, _) =>
        {
            if (updating || level.Value is not { } value) return;
            state.Level = (int)value; state.RaiseChanged();
        };
    }

    /// <summary>Follows a resolution: the level range it had and the assumptions it read.</summary>
    public void Show(int maxLevel, IReadOnlyList<CalcInput> inputs)
    {
        updating = true;
        try
        {
            if (maxLevel > 0) level.Maximum = maxLevel;
            level.Value = Math.Clamp(state.Level, 1, (int)level.Maximum);
        }
        finally { updating = false; }
        var key = string.Join("\n", inputs.Select(i => i.Kind + ":" + i.Name));
        if (key == shown)
        {
            // Another preview may have changed a shared assumption; show its value without raising a change.
            updating = true;
            try { for (int i = 0; i < boxes.Count; i++) if (boxes[i].Box.Value != inputs[i].Value) boxes[i].Box.Value = inputs[i].Value; }
            finally { updating = false; }
            return;
        }
        shown = key;
        assumptions.Children.Clear(); boxes.Clear();
        assumptionsLabel.IsVisible = inputs.Count > 0;
        foreach (var input in inputs) assumptions.Children.Add(Field(input));
    }

    private Control Field(CalcInput input)
    {
        var (minimum, maximum) = input.Kind switch { "skill" => (0m, 99m), "ulvl" => (1m, 99m), _ => (-100000m, 100000m) };
        var box = new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = input.Value, Width = 115, FormatString = "0" };
        Avalonia.Automation.AutomationProperties.SetName(box, input.Label);
        ToolTip.SetTip(box, input.Kind switch
        {
            "skill" => $"Points in {input.Name}. Calculations read it through skill('{input.Name}'.blvl) and similar lookups; 0 means the skill is not learned.",
            "stat" => $"The {input.Name} stat total that stat('{input.Name}'.accr) reads.",
            _ => "The character level that ulvl reads."
        });
        boxes.Add((input, box));
        box.ValueChanged += (_, _) =>
        {
            if (updating) return;
            var value = (long)(box.Value ?? 0);
            switch (input.Kind)
            {
                case "skill": state.SkillLevels[input.Name] = (int)value; break;
                case "stat": state.Stats[input.Name] = value; break;
                default: state.CharacterLevel = (int)value; break;
            }
            state.RaiseChanged();
        };
        var panel = new StackPanel { Spacing = 2, Margin = new(0, 2, 12, 4) };
        panel.Children.Add(new TextBlock { Text = input.Label, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 });
        panel.Children.Add(box);
        return panel;
    }
}
