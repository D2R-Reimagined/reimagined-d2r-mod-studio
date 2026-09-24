using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The Visual Builder for unique and set items: the item beside its in-game picture and tooltip, its identity fields,
/// property slots and set bonuses. The tooltip re-resolves after each edit, and its numbers jump to the field they were read from.
/// </summary>
internal sealed class VisualBuilderView : TableBuilderView<BuilderEntry, VisualBuilderCatalog>
{
    private static readonly IBrush UniqueBrush = new SolidColorBrush(Color.Parse("#C7B377")), SetBrush = new SolidColorBrush(Color.Parse("#3DDB3D")),
        MagicBrush = new SolidColorBrush(Color.Parse("#7878FF")), MagicLink = new SolidColorBrush(Color.Parse("#A3A3FF"));
    /// <summary>Common properties offered as one-click additions, when the project's properties table has them.</summary>
    private static readonly (string Code, string Label)[] QuickProperties = [
        ("dmg%", "Enhanced damage"), ("dmg-norm", "+Damage"), ("ac%", "Enhanced defense"), ("ac", "+Defense"), ("allskills", "All skills"), ("res-all", "All resistances"),
        ("hp", "Life"), ("mana", "Mana"), ("str", "Strength"), ("dex", "Dexterity"), ("swing2", "Attack speed"), ("cast2", "Cast rate"), ("balance2", "Hit recovery"),
        ("move2", "Run/walk"), ("lifesteal", "Life leech"), ("manasteal", "Mana leech"), ("mag%", "Magic find"), ("sock", "Sockets")];

    private bool IsSetTable => Table?.Name == "setitems";
    private string CodeColumn => IsSetTable ? "item" : "code";
    private readonly NumericUpDown characterLevel = new() { Minimum = 1, Maximum = 99, Value = 80, Width = 120, FormatString = "0" };
    private readonly VisualBuilderResolver catalogResolver = new();
    private readonly ItemPreviewResolver previewResolver = new();
    private readonly PreviewWorkQueue previewWork = new();
    private CancellationTokenSource? previewCancellation, spriteCancellation;
    private (string? Project, int Workspace) previewContext;
    private string? spriteKey;
    private readonly List<SlotView> slots = [];
    private readonly HashSet<string> revealed = new(StringComparer.Ordinal);
    private ContentControl tooltipHost = new();
    private ItemPicture picture = new();
    private SelectableTextBlock issues = new();
    private TextBlock gameName = new(), baseName = new(), propertyCount = new(), bonusCount = new();
    private WrapPanel quickAdd = new();

    private sealed record SlotView(PropertySlot Slot, Grid Row, ContentControl Line, AutoCompleteBox Code, TextBox Parameter, TextBox Min, TextBox Max);

    internal Task PendingSprite { get; private set; } = Task.CompletedTask;
    internal ItemPreviewResult? LastPreview { get; private set; }
    internal ItemSprite? LastSprite { get; private set; }

    public VisualBuilderView(EditorPane pane, VisualBuilderHost host) : base(pane, host)
    {
        characterLevel.ValueChanged += (_, _) => SchedulePreview();
        Ready();
    }

    protected override string ListHeading => IsSetTable ? "SET ITEMS" : "UNIQUE ITEMS";
    protected override string SearchHint => IsSetTable ? "Search set items by name, set, base or code" : "Search uniques by name, key, base or code";
    protected override string AddLabel => IsSetTable ? "+ New set item" : "+ New unique";
    protected override string Noun => IsSetTable ? "a set item" : "a unique";
    protected override string NameColumn => "index";
    protected override IBrush TitleBrush => IsSetTable ? SetBrush : UniqueBrush;
    protected override string MoreColumnsHint => " · drop sounds, transforms, cost, ladder…";
    protected override string TitleText(TableData table, int row) => LastPreview?.Tooltip != null ? LastPreview.Name : base.TitleText(table, row);

    protected override JsonObject NewRow(TableData table)
    {
        var fields = new JsonObject { ["index"] = IsSetTable ? "New Set Item" : "New Unique", ["lvl"] = "1", ["lvl req"] = "1" };
        if (table.ColumnIndex("rarity") >= 0) fields["rarity"] = "1";
        if (table.ColumnIndex("spawnable") >= 0) fields["spawnable"] = "1";
        return fields;
    }

    protected override IReadOnlyList<object> ListRows(TableData table) => VisualBuilder.Rows(table);
    protected override VisualBuilderCatalog LoadCatalog(ModProject project, string profile, string locale, IReadOnlyList<object> rows, bool fresh, CancellationToken token)
    {
        if (fresh) catalogResolver.Clear();
        return catalogResolver.Catalog(project, profile, locale, [.. rows.Cast<BuilderRow>()], token);
    }
    protected override IReadOnlyList<BuilderEntry> EntriesOf(VisualBuilderCatalog catalog) => catalog.Entries;
    protected override int RowOf(BuilderEntry entry) => entry.Row;
    protected override string SourceIdOf(BuilderEntry entry) => entry.SourceId;
    protected override bool IsInactive(BuilderEntry entry) => entry.Inactive;
    protected override string SearchTextOf(BuilderEntry entry) => entry.SearchText;
    // Header rows ("Expansion") are listed, muted, so the table's own sections stay visible.
    protected override bool Listed(BuilderEntry entry) => !entry.Inactive || entry.Index.Length > 0;

    protected override Control EntryView(BuilderEntry entry)
    {
        var panel = new StackPanel { Margin = new(2, 3) };
        var name = entry.Name.Length > 0 ? entry.Name : entry.Index.Length > 0 ? entry.Index : $"Row {entry.Row}";
        panel.Children.Add(new TextBlock { Text = name, FontSize = 13, Foreground = entry.Inactive ? Muted : IsSetTable ? SetBrush : UniqueBrush, TextTrimming = TextTrimming.CharacterEllipsis, FontStyle = entry.Inactive ? FontStyle.Italic : FontStyle.Normal });
        var detailText = entry.Inactive ? "no base item · header row" : string.Join(" · ", new[] { entry.BaseName.Length > 0 ? entry.BaseName : entry.Code, entry.RequiredLevel.Length > 0 ? "lvl " + entry.RequiredLevel : "", IsSetTable ? entry.Set : "" }.Where(s => s.Length > 0));
        panel.Children.Add(new TextBlock { Text = detailText, FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        if (entry.Name != entry.Index && entry.Index.Length > 0) ToolTip.SetTip(panel, $"{entry.Index} · row {entry.Row}");
        return panel;
    }

    protected override void OnSelect() { revealed.Clear(); LastPreview = null; LastSprite = null; spriteKey = null; }
    protected override void OnInvalidate() => spriteKey = null;
    protected override void OnBuiltEmpty() => slots.Clear();
    protected override void Stop() { previewCancellation?.Cancel(); spriteCancellation?.Cancel(); }

    protected override void BuildCards(StackPanel root, TableData table)
    {
        slots.Clear();
        root.Children.Add(Hero());
        root.Children.Add(IdentityCard(table));
        root.Children.Add(PropertiesCard(table, false));
        if (IsSetTable) root.Children.Add(PropertiesCard(table, true));
    }

    /// <summary>The item's picture beside its tooltip, with the tooltip's options underneath.</summary>
    private Control Hero()
    {
        var hero = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 20 };
        picture = new ItemPicture();
        hero.Children.Add(picture);
        var tooltipColumn = new StackPanel { Spacing = 8 }; SetColumn(tooltipColumn, 1);
        tooltipHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Left, Content = new TextBlock { Text = "Resolving item…", Foreground = Muted } };
        tooltipColumn.Children.Add(tooltipHost);
        var options = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        // The level and locale pickers outlive a rebuild of the editor; they move into the new one.
        foreach (var shared in new Control[] { characterLevel, locale }) (shared.Parent as Panel)?.Children.Remove(shared);
        options.Children.Add(Labeled("Character level", characterLevel, "Per-level properties are shown for this level"));
        options.Children.Add(Labeled("Locale", locale, "Language the names and property text are shown in"));
        tooltipColumn.Children.Add(options);
        tooltipColumn.Children.Add(new TextBlock { Text = "Click a number in the tooltip to jump to the field it comes from. Ranges show as (min–max).", FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        issues = new SelectableTextBlock { Foreground = Brushes.Salmon, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        tooltipColumn.Children.Add(issues);
        hero.Children.Add(tooltipColumn);
        return hero;
    }

    private Control IdentityCard(TableData table)
    {
        var grid = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 14, LineSpacing = 10 };
        void Add(string column, string label, Control input, string? tip = null) { if (table.ColumnIndex(column) >= 0) grid.Children.Add(Labeled(label, input, tip ?? ColumnGuide.Find(table.Name, column)?.Description)); }
        if (table.ColumnIndex("index") >= 0)
        {
            var nameColumn = new StackPanel { Spacing = 3 };
            var name = Text("index", "String key", 260);
            gameName = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
            nameColumn.Children.Add(new TextBlock { Text = "Name (string key)", FontSize = 11, Foreground = Muted });
            nameColumn.Children.Add(name); nameColumn.Children.Add(gameName);
            ToolTip.SetTip(nameColumn, "The key the game looks the item's name up by in the string tables. It also names the item's HD picture in uniques.json / sets.json.");
            grid.Children.Add(nameColumn);
        }
        if (table.ColumnIndex(CodeColumn) >= 0)
        {
            var baseColumn = new StackPanel { Spacing = 3 };
            baseColumn.Children.Add(new TextBlock { Text = "Base item", FontSize = 11, Foreground = Muted });
            baseColumn.Children.Add(Choice(CodeColumn, "Base code", 220, () => catalog?.Bases ?? [], b => b.SearchText,
                b => Option(b.Code, b.Name + " · " + b.Table + (b.RequiredLevel.Length > 0 ? " · lvl " + b.RequiredLevel : "")), code => catalog?.Bases.Any(b => b.Code == code) == true));
            baseName = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
            baseColumn.Children.Add(baseName);
            ToolTip.SetTip(baseColumn, "The base item this is made from (weapons, armor or misc code). It sets the picture, damage or defense, and base requirements. Type a code or part of its name.");
            grid.Children.Add(baseColumn);
        }
        if (IsSetTable) Add("set", "Set", Choice("set", "Set key", 240, () => catalog?.Sets ?? [], s => s.SearchText, s => Option(s.Index, s.Name), key => catalog?.Sets.Any(s => s.Index == key) == true), "The set (sets.txt index) this item belongs to.");
        Add("lvl", "Item level", Text("lvl", "", 100), "Item level: the lowest monster/area level that can drop it, and its affix level.");
        Add("lvl req", "Required level", Text("lvl req", "", 100), "Character level needed to equip it. The base item's own requirement applies if higher.");
        Add("rarity", "Rarity", Text("rarity", "", 90), "Weight against the other items of the same base when this quality rolls.");
        foreach (var (column, label, tip) in new[] { ("spawnable", "Can drop", "Unchecked items never drop; they can still be made in the cube."), ("disabled", "Disabled", "Disabled items are not generated at all."), ("nolimit", "No limit", "Can drop again even if already found in this game.") })
            if (table.ColumnIndex(column) >= 0) grid.Children.Add(Toggle(column, label, tip));
        return Card(IsSetTable ? "Item" : "Unique", grid);
    }

    private Control PropertiesCard(TableData table, bool bonuses)
    {
        var tableSlots = VisualBuilder.Slots(table.Name, table.Columns).Where(s => s.SetBonus == bonuses).ToArray();
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(SlotHeader(bonuses));
        foreach (var slot in tableSlots) { var view = SlotRow(slot, bonuses); slots.Add(view); list.Children.Add(view.Row); }
        var add = new Button { Content = bonuses ? "+ Add set bonus" : "+ Add property", Padding = new(10, 4), MinHeight = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 4, 0, 0) };
        add.Click += (_, _) => Try(() => RevealSlot(bonuses, null));
        list.Children.Add(add);
        var count = new TextBlock { FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        if (bonuses) bonusCount = count; else propertyCount = count;
        if (!bonuses)
        {
            quickAdd = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4, Margin = new(0, 6, 0, 0) };
            list.Children.Add(quickAdd);
            RefreshQuickAdd();
        }
        else if (table.ColumnIndex("add func") >= 0)
            list.Children.Insert(0, Labeled("How the bonuses apply (add func)", Numbered("add func", 380, "0 · no item bonuses", "1 · bonus with a specific other set item", "2 · bonus by number of set items worn"), ColumnGuide.Find(table.Name, "add func")?.Description));
        return Card(bonuses ? "Set bonuses on this item" : "Properties", list, count,
            bonuses ? "Extra properties this item gains while other pieces of its set are worn. The set's own partial and full bonuses are edited in sets." : "Everything the item rolls. Min and max give the roll range; the parameter is a skill, class, chance or other detail the property reads.");
    }

    // Code, parameter and range share the width; the tooltip line sits under them, so a narrow editor still fits.
    private const string SlotColumns = "64,2*,1.4*,*,*,Auto";
    private static Grid SlotHeader(bool bonuses)
    {
        var grid = new Grid { ColumnDefinitions = new(SlotColumns), ColumnSpacing = 6 };
        string[] labels = [bonuses ? "Worn with" : "Slot", "Property", "Parameter", "Min", "Max"];
        for (int i = 0; i < labels.Length; i++) { var text = new TextBlock { Text = labels[i], FontSize = 11, Foreground = Muted }; SetColumn(text, i); grid.Children.Add(text); }
        return grid;
    }

    private SlotView SlotRow(PropertySlot slot, bool bonuses)
    {
        var grid = new Grid { ColumnDefinitions = new(SlotColumns), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 6, IsVisible = false, Margin = new(0, 0, 0, 4) };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = Muted, Text = bonuses ? $"{slot.Pieces} items" : slot.Prop.Replace("prop", "#") };
        ToolTip.SetTip(label, string.Join(", ", slot.Columns));
        grid.Children.Add(label);
        var code = Choice(slot.Prop, "Property code", double.NaN, () => catalog?.Properties ?? [], p => p.SearchText,
            p => Option(p.Code, p.Tooltip.Length > 0 ? p.Tooltip : p.Notes), text => catalog?.Properties.Any(p => p.Code == text) == true);
        SetColumn(code, 1); grid.Children.Add(code);
        var parameter = Text(slot.Parameter, "", double.NaN); SetColumn(parameter, 2); grid.Children.Add(parameter);
        var min = Text(slot.Min, "", double.NaN); SetColumn(min, 3); grid.Children.Add(min);
        var max = Text(slot.Max, "", double.NaN); SetColumn(max, 4); grid.Children.Add(max);
        var line = new ContentControl { Margin = new(4, 3, 0, 0) }; SetRow(line, 1); SetColumn(line, 1); SetColumnSpan(line, 4); grid.Children.Add(line);
        var clear = new Button { Content = "✕", Padding = new(8, 2), MinHeight = 0, FontSize = 12, Foreground = Muted, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(clear, "Remove this property (clears its four cells)");
        clear.Click += (_, _) => { revealed.Remove(slot.Prop); CommitAll(slot.Columns.Select(c => (c, ""))); };
        SetColumn(clear, 5); grid.Children.Add(clear);
        var view = new SlotView(slot, grid, line, code, parameter, min, max);
        code.TextChanged += (_, _) => DescribeSlot(view);
        return view;
    }

    /// <summary>Placeholders and the tooltip template of the slot's property, from properties.txt's documentation columns.</summary>
    private void DescribeSlot(SlotView view)
    {
        var property = catalog?.Properties.FirstOrDefault(p => p.Code == (view.Code.Text ?? ""));
        view.Parameter.PlaceholderText = property?.Parameter ?? ""; view.Min.PlaceholderText = property?.Min ?? ""; view.Max.PlaceholderText = property?.Max ?? "";
        ToolTip.SetTip(view.Code, property == null ? null : string.Join("\n", new[] { property.Code, property.Tooltip, property.Notes }.Where(s => s.Length > 0)));
    }

    /// <summary>Shows the next empty slot (or the first one when the property is quick-added) and focuses where to type next.</summary>
    private void RevealSlot(bool bonuses, string? code)
    {
        if (SelectedRow < 0 || Table == null) return;
        var free = slots.FirstOrDefault(s => s.Slot.SetBonus == bonuses && !s.Row.IsVisible);
        if (free == null) { SetStatus(bonuses ? "Every set bonus slot is in use." : $"All {slots.Count(s => !s.Slot.SetBonus)} property slots are in use.", true); return; }
        revealed.Add(free.Slot.Prop); free.Row.IsVisible = true;
        if (code != null) Commit(free.Slot.Prop, code);
        UpdateSlotCounts();
        var target = code == null ? (Control)free.Code : free.Min;
        Dispatcher.UIThread.Post(() => { target.BringIntoView(); target.Focus(); }, DispatcherPriority.Background);
    }

    protected override IEnumerable<string> HandledColumns(TableData table) => VisualBuilder.Slots(table.Name, table.Columns).SelectMany(s => s.Columns);

    protected override void Reveal(string column)
    {
        if (slots.FirstOrDefault(s => s.Slot.Columns.Contains(column)) is { } slot) { slot.Row.IsVisible = true; revealed.Add(slot.Slot.Prop); }
    }

    protected override void OnSynced(TableData table, int row)
    {
        foreach (var slot in slots) { slot.Row.IsVisible = revealed.Contains(slot.Slot.Prop) || slot.Slot.Columns.Any(c => table.Cell(row, c).Length > 0); DescribeSlot(slot); }
        UpdateSlotCounts();
        DescribeBase();
    }

    /// <summary>The base code's name, table and level under the base item field.</summary>
    private void DescribeBase()
    {
        var table = Table; int row = SelectedRow; if (table == null || row < 0) return;
        var code = table.Cell(row, CodeColumn);
        baseName.Text = code.Length == 0 ? "No base item: the row is inactive until one is set."
            : catalog?.Bases.FirstOrDefault(b => b.Code == code) is { } found ? $"{found.Name} · {found.Table}" + (found.RequiredLevel.Length > 0 ? $" · base level {found.RequiredLevel}" : "")
            : catalog == null ? "" : "Not a weapons, armor or misc code in this project.";
    }

    private void UpdateSlotCounts()
    {
        int Used(bool bonus) => slots.Count(s => s.Slot.SetBonus == bonus && s.Row.IsVisible);
        propertyCount.Text = $"{Used(false)} of {slots.Count(s => !s.Slot.SetBonus)} slots";
        bonusCount.Text = $"{Used(true)} of {slots.Count(s => s.Slot.SetBonus)} slots";
    }

    protected override void OnCatalogLoaded()
    {
        foreach (var slot in slots) DescribeSlot(slot);
        DescribeBase();
        RefreshQuickAdd();
    }

    private void RefreshQuickAdd()
    {
        quickAdd.Children.Clear();
        if (catalog == null) return;
        quickAdd.Children.Add(new TextBlock { Text = "Quick add:", FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        foreach (var (code, label) in QuickProperties)
        {
            if (catalog.Properties.FirstOrDefault(p => p.Code == code) is not { } property) continue;
            var chip = new Button { Content = label, Padding = new(8, 2), MinHeight = 0, FontSize = 11, Margin = new(0) };
            ToolTip.SetTip(chip, code + (property.Tooltip.Length > 0 ? " · " + property.Tooltip : ""));
            chip.Click += (_, _) => Try(() => RevealSlot(false, code));
            quickAdd.Children.Add(chip);
        }
    }

    // ── Preview and picture ────────────────────────────────────────────────────────────────────────────

    protected override async Task PreviewAsync()
    {
        previewCancellation?.Cancel();
        var work = previewCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var table = Table; int row = SelectedRow; var record = SelectedRecord();
        if (project == null || table == null || record == null) { work.Dispose(); if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; return; }
        string profile = host.Profile(), language = Locale, name = table.Name; int level = (int)(characterLevel.Value ?? 80), workspace = host.Workspace(), revision = Document.Revision;
        try
        {
            if (host.DirtyDependency(pane) is { } dirty) { ShowUnavailable("Save the edited dependency to preview: " + dirty); return; }
            if (previewContext != (project.Root, workspace)) { previewResolver.Clear(); previewContext = (project.Root, workspace); }
            var result = await previewWork.RunAsync(ct => previewResolver.Resolve(project, name, record, profile, level, language, ct), token);
            if (token.IsCancellationRequested || revision != Document.Revision || SelectedRow != row) return;
            LastPreview = result;
            tooltipHost.Content = TooltipCard(result);
            issues.Text = result.Issues.Length == 0 ? "" : "Incomplete preview\n" + string.Join("\n", result.Issues);
            issues.IsVisible = result.Issues.Length > 0;
            gameName.Text = result.Tooltip != null ? "Shown in game as: " + result.Name : "";
            ShowSlotLines(result, table.Name);
            UpdateTitle();
            RequestSprite(project, table, row);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ShowUnavailable(ex.Message); }
        finally { if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; work.Dispose(); }
    }

    private void ShowUnavailable(string message)
    {
        LastPreview = new ItemPreviewResult("Preview unavailable", IsSetTable, [], [message]);
        tooltipHost.Content = new TextBlock { Text = message, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        issues.IsVisible = false;
    }

    /// <summary>The item drawn like the game's tooltip: centred lines on black, name and base in the quality colour, properties in blue.</summary>
    private static Control TooltipCard(ItemPreviewResult result)
    {
        var quality = result.IsSet ? SetBrush : UniqueBrush;
        var name = new GameTooltip.Block([new PreviewText(result.Name, [])], quality, quality, 18);
        if (result.Tooltip is not { } tooltip) return GameTooltip.Card([name, new(result.Text, WhiteBrush, PreviewLinkText.LinkBrush, 13)]);
        return GameTooltip.Card([name, new(tooltip.BaseName is { } baseName ? [baseName] : [], quality, quality, 16), new(tooltip.Stats, WhiteBrush, Brushes.White, 15, 4),
            new(tooltip.Properties, MagicBrush, MagicLink, 15, 2), new(tooltip.SetLines, SetBrush, Brushes.LightGreen, 14, 8)]);
    }

    /// <summary>Beside each slot, the tooltip lines read from its cells; the property's template when it resolves to none.</summary>
    private void ShowSlotLines(ItemPreviewResult result, string table)
    {
        var lines = result.Tooltip is { } tooltip ? tooltip.Properties.Concat(tooltip.SetLines).ToArray() : result.Text;
        foreach (var slot in slots)
        {
            var columns = slot.Slot.Columns.ToHashSet(StringComparer.Ordinal);
            var own = lines.Where(l => l.Links.Any(link => link.Targets.Any(t => t.Table == table && t.SourceId == selectedSourceId && columns.Contains(t.Column)))).ToArray();
            slot.Line.Content = own.Length > 0
                ? new PreviewLinkText(own, MagicLink) { Foreground = MagicBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap }
                : new TextBlock { Text = (slot.Code.Text ?? "").Length == 0 ? "Pick a property code" : "Not shown in the tooltip", Foreground = Muted, FontSize = 11, FontStyle = FontStyle.Italic };
        }
    }

    private void RequestSprite(ModProject project, TableData table, int row)
    {
        var code = table.Cell(row, CodeColumn); var index = table.Cell(row, "index");
        var baseItem = catalog?.Bases.FirstOrDefault(b => b.Code == code);
        var gameData = host.GameData();
        var key = string.Join('|', project.Root, table.Name, index, code, baseItem?.Table, baseItem?.Tier, string.Join(';', gameData));
        if (key == spriteKey) return;
        spriteKey = key;
        PendingSprite = LoadSpriteAsync(project, gameData, table.Name, index, code, baseItem);
    }

    private async Task LoadSpriteAsync(ModProject project, IReadOnlyList<string> gameData, string table, string index, string code, BuilderBase? baseItem)
    {
        spriteCancellation?.Cancel();
        var work = spriteCancellation = new CancellationTokenSource(); var token = work.Token;
        try
        {
            var sprite = await Task.Run(() => ItemSprites.Resolve(project, gameData, table, index, code, baseItem?.Table, baseItem?.Tier ?? "normal", token), token);
            if (token.IsCancellationRequested) return;
            LastSprite = sprite;
            ShowSprite(sprite, gameData.Count > 0);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) picture.Fail(ex.Message); }
        finally { if (ReferenceEquals(spriteCancellation, work)) spriteCancellation = null; work.Dispose(); }
    }

    private void ShowSprite(ItemSprite sprite, bool haveGameData) => picture.Show(sprite, host, haveGameData, SetStatus);
}

/// <summary>Small helpers the builders share for pictures.</summary>
internal static class Bitmaps
{
    /// <summary>An RGBA (straight alpha) buffer as a bitmap.</summary>
    public static WriteableBitmap From(int width, int height, byte[] rgba)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        Copy(bitmap, width, height, rgba);
        return bitmap;
    }

    public static void Copy(WriteableBitmap bitmap, int width, int height, byte[] rgba)
    {
        using var target = bitmap.Lock();
        for (int y = 0; y < height; y++) Marshal.Copy(rgba, y * width * 4, target.Address + y * target.RowBytes, width * 4);
    }

    public static Button ChooseGameDataButton(VisualBuilderHost host, Action<string, bool> error)
    {
        var choose = new Button { Content = "Choose game data folder…", Padding = new(8, 3), MinHeight = 0, FontSize = 11 };
        ToolTip.SetTip(choose, "Your extracted D2R data (for example with CascView): the folder holding hd/ and global/. Files the mod does not ship are read from it.");
        choose.Click += async (_, _) => { try { await host.ChooseGameData(); } catch (Exception ex) { error(ex.Message, true); } };
        return choose;
    }
}
