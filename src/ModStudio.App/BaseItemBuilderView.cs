using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The Visual Builder for base items, one view for weapons, armor and misc: the item's HD picture beside its tooltip and
/// the facts behind it, its fields grouped by what they do (a card only appears when the table has its columns), vendor
/// stock, the tier it belongs to, the uniques and sets made on it, and its drop and affix previews.
/// </summary>
internal sealed class BaseItemBuilderView : TableBuilderView<BaseItemEntry, BaseItemBuilderCatalog>
{
    private static readonly string[] Vendors = ["Charsi", "Gheed", "Akara", "Fara", "Lysander", "Drognan", "Hratli", "Alkor", "Ormus", "Elzix", "Asheara", "Cain", "Halbu", "Jamella", "Larzuk", "Malah", "Anya"];
    private static readonly (string Title, string Note, (string Column, string Label)[] Fields)[] Groups = [
        ("Item", "Names, code, type and the tier codes that link normal, exceptional and elite versions.", [
            ("namestr", "Name (string key)"), ("name", "Name (internal)"), ("code", "Code"), ("type", "Type"), ("type2", "Second type"), ("alternategfx", "Graphics code"),
            ("normcode", "Normal code"), ("ubercode", "Exceptional code"), ("ultracode", "Elite code"), ("version", "Version"), ("rarity", "Rarity"), ("component", "Component")]),
        ("Level and requirements", "Drop level (qlvl) decides where it can drop; the requirements are what a character needs to use it.", [
            ("level", "Drop level"), ("levelreq", "Required level"), ("reqstr", "Required Strength"), ("reqdex", "Required Dexterity"), ("magic lvl", "Magic level"), ("ShowLevel", "Show level")]),
        ("Damage", "Weapons: one-hand, two-hand and throw damage, speed and the stat bonuses. Armor: kick or smite damage.", [
            ("mindam", "Min damage"), ("maxdam", "Max damage"), ("1or2handed", "One or two hands"), ("2handed", "Two-handed"), ("2handmindam", "Two-hand min"), ("2handmaxdam", "Two-hand max"),
            ("minmisdam", "Throw min"), ("maxmisdam", "Throw max"), ("rangeadder", "Range"), ("speed", "Speed"), ("StrBonus", "Strength bonus %"), ("DexBonus", "Dexterity bonus %"),
            ("hit class", "Hit class"), ("wclass", "Weapon class"), ("2handedwclass", "Two-hand weapon class"), ("missiletype", "Missile")]),
        ("Defense", "Base defense rolls between min and max; block is the base chance before class and level.", [
            ("minac", "Min defense"), ("maxac", "Max defense"), ("block", "Block %"), ("belt", "Belt slots"), ("rArm", "Right arm gfx"), ("lArm", "Left arm gfx"), ("Torso", "Torso gfx"),
            ("Legs", "Legs gfx"), ("rSPad", "Right pad gfx"), ("lSPad", "Left pad gfx")]),
        ("Durability, sockets and stacks", "Sockets are also capped by the type's limit for the item level (see Sockets above).", [
            ("durability", "Durability"), ("nodurability", "No durability"), ("gemsockets", "Max sockets"), ("gemapplytype", "Gem effect"), ("stackable", "Stackable"),
            ("minstack", "Min stack"), ("maxstack", "Max stack"), ("spawnstack", "Spawn stack")]),
        ("Use", "What using the item does (potions, scrolls, runes, gems).", [
            ("useable", "Usable"), ("pSpell", "Spell"), ("spellicon", "Spell icon"), ("state", "State"), ("cstate1", "Client state 1"), ("cstate2", "Client state 2"), ("len", "Length"),
            ("stat1", "Stat 1"), ("calc1", "Calc 1"), ("stat2", "Stat 2"), ("calc2", "Calc 2"), ("stat3", "Stat 3"), ("calc3", "Calc 3"), ("spelldesc", "Description function"),
            ("spelldescstr", "Description string"), ("spelldescstr2", "Description string 2"), ("spelldesccalc", "Description calc"), ("spelldesccolor", "Description colour"),
            ("autobelt", "Auto belt"), ("BetterGem", "Better gem"), ("multibuy", "Buy several")]),
        ("Price and upgrades", "What vendors charge, and what the item becomes when the Horadric cube upgrades it by difficulty.", [
            ("cost", "Cost"), ("gamble cost", "Gamble cost"), ("auto prefix", "Automatic affix group"), ("NightmareUpgrade", "Nightmare upgrade"), ("HellUpgrade", "Hell upgrade"),
            ("Transmogrify", "Transmogrify"), ("TMogType", "Transmogrifies into"), ("TMogMin", "Transmogrify min"), ("TMogMax", "Transmogrify max")]),
        ("Graphics and sound", "Inventory size and art, and the sounds it makes.", [
            ("invwidth", "Width"), ("invheight", "Height"), ("invfile", "Inventory art"), ("uniqueinvfile", "Unique art"), ("setinvfile", "Set art"), ("flippyfile", "Drop art"),
            ("dropsound", "Drop sound"), ("dropsfxframe", "Drop sound frame"), ("usesound", "Use sound"), ("transparent", "Transparent"), ("transtbl", "Transparency"),
            ("lightradius", "Light radius"), ("Transform", "Colour transform"), ("InvTrans", "Inventory transform")])];

    private readonly BaseItemPreviewResolver catalogResolver = new(), previewResolver = new();
    private readonly PreviewWorkQueue previewWork = new();
    private readonly DropPreviewResolver dropResolver = new();
    private readonly AffixPreviewResolver affixResolver = new();
    private CancellationTokenSource? previewCancellation, spriteCancellation, detailsCancellation;
    private (string? Project, int Workspace) previewContext;
    private string? spriteKey;
    private ItemPicture picture = new();
    private ContentControl tooltipHost = new(), facts = new(), uses = new(), tiers = new(), drops = new(), affixes = new();
    private Expander dropsExpander = new(), affixExpander = new();
    private SelectableTextBlock issues = new();

    internal BaseItemPreviewResult? LastPreview { get; private set; }
    internal ItemSprite? LastSprite { get; private set; }
    internal Task PendingSprite { get; private set; } = Task.CompletedTask;
    internal Task PendingDetails { get; private set; } = Task.CompletedTask;
    internal Control Uses => uses;
    internal Control Tiers => tiers;

    public BaseItemBuilderView(EditorPane pane, VisualBuilderHost host) : base(pane, host) => Ready();

    private string TableName => Table?.Name ?? "";
    protected override string ListHeading => TableName.ToUpperInvariant();
    protected override string SearchHint => "Search by name, code, type or tier";
    protected override string AddLabel => TableName switch { "weapons" => "+ New weapon", "armor" => "+ New armor", _ => "+ New item" };
    protected override string Noun => TableName switch { "weapons" => "a weapon", "armor" => "a piece of armor", _ => "an item" };
    protected override string NameColumn => "code";
    protected override IBrush TitleBrush => GameTooltip.White;
    /// <summary>A copy needs a code of its own: the same first two letters and the first free last one.</summary>
    protected override string CopyName(string name)
    {
        var used = Table is { } table ? Enumerable.Range(0, table.Records.Count).Select(i => table.Cell(i, "code")).ToHashSet(StringComparer.Ordinal) : [];
        var stem = name.Length >= 3 ? name[..2] : name;
        return "0123456789abcdefghijklmnopqrstuvwxyz".Select(c => stem + c).FirstOrDefault(code => !used.Contains(code)) ?? name + "_copy";
    }
    protected override string MoreColumnsHint => " · everything else in " + TableName;
    protected override string TitleText(TableData table, int row) => LastPreview is { Tooltip.Length: > 0 } preview ? preview.Name : base.TitleText(table, row);

    protected override JsonObject NewRow(TableData table)
    {
        var fields = new JsonObject();
        foreach (var (column, value) in new[] { ("name", "New Item"), ("namestr", "New Item"), ("code", "zzz"), ("level", "1"), ("rarity", "1"), ("spawnable", "1"), ("invwidth", "1"), ("invheight", "1"),
            ("durability", "20"), ("mindam", "1"), ("maxdam", "4"), ("minac", "5"), ("maxac", "8"), ("cost", "100"), ("normcode", "zzz") })
            if (table.ColumnIndex(column) >= 0) fields[column] = value;
        return fields;
    }

    protected override IReadOnlyList<object> ListRows(TableData table) => BaseItemPreviewResolver.Rows(table);
    protected override BaseItemBuilderCatalog LoadCatalog(ModProject project, string profile, string locale, IReadOnlyList<object> rows, bool fresh, CancellationToken token)
    {
        if (fresh) catalogResolver.Clear();
        return catalogResolver.Catalog(project, profile, locale, [.. rows.Cast<BaseItemRow>()], token);
    }
    protected override IReadOnlyList<BaseItemEntry> EntriesOf(BaseItemBuilderCatalog catalog) => catalog.Entries;
    protected override int RowOf(BaseItemEntry entry) => entry.Row;
    protected override string SourceIdOf(BaseItemEntry entry) => entry.SourceId;
    protected override bool IsInactive(BaseItemEntry entry) => entry.Inactive;
    protected override string SearchTextOf(BaseItemEntry entry) => entry.SearchText;

    protected override Control EntryView(BaseItemEntry entry)
    {
        var panel = new StackPanel { Margin = new(2, 3) };
        panel.Children.Add(new TextBlock { Text = entry.Name.Length > 0 ? entry.Name : entry.Code.Length > 0 ? entry.Code : $"Row {entry.Row}", FontSize = 13, Foreground = GameTooltip.White, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { entry.Code, entry.Type, entry.Level.Length > 0 ? "lvl " + entry.Level : "", entry.Tier }.Where(s => s.Length > 0)), FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    protected override void OnSelect() { LastPreview = null; LastSprite = null; spriteKey = null; }
    protected override void OnInvalidate() => spriteKey = null;
    protected override void Stop() { previewCancellation?.Cancel(); spriteCancellation?.Cancel(); detailsCancellation?.Cancel(); }

    // ── Cards ──────────────────────────────────────────────────────────────────────────────────────────

    protected override void BuildCards(StackPanel root, TableData table)
    {
        root.Children.Add(Hero());
        tiers = new ContentControl(); uses = new ContentControl();
        root.Children.Add(Card("Related", new StackPanel { Spacing = 10, Children = { tiers, uses } },
            note: "The other tiers of this item, and the uniques and sets made on it. Open one to edit it in its own builder."));
        foreach (var group in Groups)
        {
            var fields = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
            foreach (var (column, label) in group.Fields) if (FieldFor(table, column, label) is { } field) fields.Children.Add(field);
            if (fields.Children.Count > 0) root.Children.Add(Card(group.Title, fields, note: group.Note));
        }
        if (VendorGrid(table) is { } vendors) root.Children.Add(Card("Vendors", vendors, note: "How many each vendor stocks, and how many magic ones up to which magic level. Empty means they never sell it."));
        var flags = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 14, LineSpacing = 2 };
        foreach (var column in table.Columns)
            if (!editors.ContainsKey(column) && !column.StartsWith('*') && ColumnGuide.Find(table.Name, column)?.Description is { } guide && guide.StartsWith("Boolean", StringComparison.OrdinalIgnoreCase))
                flags.Children.Add(Toggle(column, column, guide));
        if (flags.Children.Count > 0) root.Children.Add(Card("Flags", flags, note: "Hover a switch for what it does."));
        drops = new ContentControl(); affixes = new ContentControl();
        dropsExpander = new Expander { Header = "Where it drops", Content = drops, HorizontalAlignment = HorizontalAlignment.Stretch };
        affixExpander = new Expander { Header = "Magic and rare affixes it can roll", Content = affixes, HorizontalAlignment = HorizontalAlignment.Stretch };
        // The drop and affix rolls are heavier than the tooltip: they are worked out when opened.
        foreach (var expander in new[] { dropsExpander, affixExpander })
            expander.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty && expander.IsExpanded) PendingDetails = DetailsAsync(); };
        root.Children.Add(dropsExpander); root.Children.Add(affixExpander);
    }

    /// <summary>A picker for columns that name an item type, a base code, a missile or a sound; a switch for the data guide's booleans; else a text field.</summary>
    private Control? FieldFor(TableData table, string column, string label)
    {
        if (table.ColumnIndex(column) < 0) return null;
        var guide = ColumnGuide.Find(table.Name, column)?.Description;
        Control input = column switch
        {
            "type" or "type2" => Choice(column, "item type", 150, () => catalog?.ItemTypes.Select(t => new TypeChoice(t.Code, t.Name)) ?? [], t => (t.Code + " " + t.Name).ToLowerInvariant(),
                t => Option(t.Code, t.Name), code => catalog?.ItemTypes.Any(t => t.Code == code) == true),
            "normcode" or "ubercode" or "ultracode" or "NightmareUpgrade" or "HellUpgrade" or "TMogType" => NameChoice(column, "base code", 100, () => catalog?.Codes ?? []),
            "missiletype" => NameChoice(column, "missile", 150, () => catalog?.Missiles ?? []),
            "dropsound" or "usesound" => NameChoice(column, "sound", 150, () => catalog?.Sounds ?? []),
            _ when guide?.StartsWith("Boolean", StringComparison.OrdinalIgnoreCase) == true => Toggle(column, label, guide),
            _ => Text(column, "", column is "name" or "namestr" or "invfile" or "uniqueinvfile" or "setinvfile" or "flippyfile" or "spelldescstr" or "spelldescstr2" or "spelldesccalc" or "calc1" or "calc2" or "calc3" ? 170 : 96)
        };
        return input is CheckBox ? input : Labeled(label, input, guide is { } g ? $"{column}: {g}" : column);
    }
    private sealed record TypeChoice(string Code, string Name) { public override string ToString() => Code; }

    /// <summary>Vendors down, their stock columns across: min and max stocked, min and max magic ones, and the magic level cap.</summary>
    private Grid? VendorGrid(TableData table)
    {
        string[] suffixes = ["Min", "Max", "MagicMin", "MagicMax", "MagicLvl"];
        var present = Vendors.Where(v => suffixes.Any(s => table.ColumnIndex(v + s) >= 0)).ToArray();
        if (present.Length == 0) return null;
        var grid = new Grid { ColumnDefinitions = new("90,*,*,*,*,*"), ColumnSpacing = 8, RowSpacing = 4 };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] heads = ["Min", "Max", "Magic min", "Magic max", "Magic level"];
        for (int c = 0; c < heads.Length; c++) { var head = new TextBlock { Text = heads[c], FontSize = 11, Foreground = Muted }; SetColumn(head, c + 1); grid.Children.Add(head); }
        foreach (var vendor in present)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); int r = grid.RowDefinitions.Count - 1;
            var name = new TextBlock { Text = vendor, VerticalAlignment = VerticalAlignment.Center, Foreground = GameTooltip.White }; SetRow(name, r); grid.Children.Add(name);
            for (int c = 0; c < suffixes.Length; c++)
            {
                if (table.ColumnIndex(vendor + suffixes[c]) < 0) continue;
                var input = Text(vendor + suffixes[c], "", double.NaN); SetRow(input, r); SetColumn(input, c + 1); grid.Children.Add(input);
            }
        }
        return grid;
    }

    private Control Hero()
    {
        var hero = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 20 };
        picture = new ItemPicture();
        hero.Children.Add(picture);
        var column = new StackPanel { Spacing = 10 }; SetColumn(column, 1);
        tooltipHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Left, Content = new TextBlock { Text = "Resolving item…", Foreground = Muted } };
        facts = new ContentControl();
        issues = new SelectableTextBlock { Foreground = Brushes.Salmon, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        column.Children.Add(tooltipHost); column.Children.Add(facts); column.Children.Add(issues);
        column.Children.Add(new TextBlock { Text = "Click a number to jump to the field it comes from.", FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        hero.Children.Add(column);
        return hero;
    }

    protected override void OnSynced(TableData table, int row) { ShowRelated(table, row); }
    protected override void OnCatalogLoaded() { if (Table is { } table && SelectedRow >= 0) ShowRelated(table, SelectedRow); }

    /// <summary>Buttons to the item's other tiers, and to the uniques and sets made on it.</summary>
    private void ShowRelated(TableData table, int row)
    {
        string Cell(string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        var tierRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4 };
        foreach (var (label, column) in new[] { ("Normal", "normcode"), ("Exceptional", "ubercode"), ("Elite", "ultracode") })
        {
            var code = Cell(column); if (code.Length == 0) continue;
            bool current = code == Cell("code");
            var button = new Button { Content = $"{label} · {code}", Padding = new(10, 3), MinHeight = 0, FontSize = 12, IsEnabled = !current };
            ToolTip.SetTip(button, current ? "This item" : $"Open {code}");
            button.Click += (_, _) => { if (!SelectWhere("code", code)) SetStatus($"{code} is not in {table.Name}.", true); };
            tierRow.Children.Add(button);
        }
        tiers.Content = tierRow.Children.Count > 0 ? Labeled("Tiers", tierRow) : new TextBlock { Text = "No tier codes.", Foreground = Muted, FontSize = 12 };
        var made = catalog?.Uses[Cell("code")].ToArray() ?? [];
        var list = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4 };
        foreach (var use in made)
        {
            var button = new Button { Content = use.Name.Length > 0 ? use.Name : use.Index, Padding = new(10, 3), MinHeight = 0, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse(use.Table == "setitems" ? "#3DDB3D" : "#C7B377")) };
            ToolTip.SetTip(button, $"Open {use.Index} in {use.Table}");
            button.Click += async (_, _) => { try { if (host.OpenInBuilder != null) await host.OpenInBuilder(use.Table, "index", use.Index); } catch (Exception ex) { SetStatus(ex.Message, true); } };
            list.Children.Add(button);
        }
        uses.Content = made.Length > 0 ? Labeled($"Uniques and sets on this base ({made.Length})", list) : new TextBlock { Text = catalog == null ? "" : "No uniques or sets are made on this base.", Foreground = Muted, FontSize = 12 };
    }

    // ── Preview, picture and details ───────────────────────────────────────────────────────────────────

    protected override async Task PreviewAsync()
    {
        previewCancellation?.Cancel();
        var work = previewCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var table = Table; int row = SelectedRow; var record = SelectedRecord();
        if (project == null || table == null || record == null) { work.Dispose(); if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; return; }
        string profile = host.Profile(), language = Locale, name = table.Name; int workspace = host.Workspace(), revision = Document.Revision;
        try
        {
            if (previewContext != (project.Root, workspace)) { previewResolver.Clear(); dropResolver.Clear(); affixResolver.Clear(); previewContext = (project.Root, workspace); }
            var result = await previewWork.RunAsync(ct => previewResolver.Resolve(project, name, record, profile, language, ct), token);
            if (token.IsCancellationRequested || revision != Document.Revision || SelectedRow != row) return;
            LastPreview = result;
            tooltipHost.Content = GameTooltip.Card([
                new([new PreviewText(result.Name, [])], GameTooltip.White, GameTooltip.White, 18),
                new(result.Tier.Length > 0 ? [new PreviewText(result.Tier, [])] : [], Muted, Muted, 13),
                new(result.Tooltip.Skip(1).ToArray(), GameTooltip.White, Brushes.White, 15, 4)]);
            facts.Content = FactsGrid(result);
            issues.Text = string.Join("\n", result.Issues); issues.IsVisible = result.Issues.Length > 0;
            UpdateTitle();
            RequestSprite(project, table, row);
            if (dropsExpander.IsExpanded || affixExpander.IsExpanded) PendingDetails = DetailsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) tooltipHost.Content = new TextBlock { Text = ex.Message, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 }; }
        finally { if (ReferenceEquals(previewCancellation, work)) previewCancellation = null; work.Dispose(); }
    }

    private static Control FactsGrid(BaseItemPreviewResult result)
    {
        var grid = new Grid { ColumnDefinitions = new("120,*"), RowSpacing = 3, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (label, value) in result.Facts)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); int r = grid.RowDefinitions.Count - 1;
            var name = new TextBlock { Text = label, Foreground = Muted, FontSize = 12 }; SetRow(name, r); grid.Children.Add(name);
            var text = new PreviewLinkText([value]) { Foreground = GameTooltip.White, FontSize = 12, TextWrapping = TextWrapping.Wrap }; SetRow(text, r); SetColumn(text, 1); grid.Children.Add(text);
        }
        return grid;
    }

    private void RequestSprite(ModProject project, TableData table, int row)
    {
        string Cell(string column) => table.ColumnIndex(column) >= 0 ? table.Cell(row, column) : "";
        var code = Cell("code"); var tier = BaseItemPreviewResolver.Tier(Cell) switch { "Elite" => "ultra", "Exceptional" => "uber", _ => "normal" };
        var gameData = host.GameData();
        var key = string.Join('|', project.Root, table.Name, code, tier, string.Join(';', gameData));
        if (key == spriteKey) return;
        spriteKey = key;
        PendingSprite = LoadSpriteAsync(project, gameData, table.Name, code, tier);
    }

    private async Task LoadSpriteAsync(ModProject project, IReadOnlyList<string> gameData, string table, string code, string tier)
    {
        spriteCancellation?.Cancel();
        var work = spriteCancellation = new CancellationTokenSource(); var token = work.Token;
        try
        {
            // A base item has no name-keyed picture: items.json names its art by code.
            var sprite = await Task.Run(() => ItemSprites.Resolve(project, gameData, table, "", code, table, tier, token), token);
            if (token.IsCancellationRequested) return;
            LastSprite = sprite;
            picture.Show(sprite, host, gameData.Count > 0, SetStatus);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) picture.Fail(ex.Message); }
        finally { if (ReferenceEquals(spriteCancellation, work)) spriteCancellation = null; work.Dispose(); }
    }

    /// <summary>The drop and affix previews, for whichever expander is open.</summary>
    private async Task DetailsAsync()
    {
        detailsCancellation?.Cancel();
        var work = detailsCancellation = new CancellationTokenSource(); var token = work.Token;
        var project = host.Project(); var table = Table; var record = SelectedRecord();
        if (project == null || table == null || record == null) return;
        string profile = host.Profile(), language = Locale, name = table.Name; bool wantDrops = dropsExpander.IsExpanded, wantAffixes = affixExpander.IsExpanded;
        try
        {
            if (host.DirtyDependency(pane) is { } dirty) { drops.Content = affixes.Content = new TextBlock { Text = "Save the edited dependency to preview: " + dirty, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; return; }
            if (wantDrops) drops.Content = new TextBlock { Text = "Rolling drops…", Foreground = Muted };
            if (wantAffixes) affixes.Content = new TextBlock { Text = "Rolling affixes…", Foreground = Muted };
            var (dropResult, affixResult) = await previewWork.RunAsync(ct => (wantDrops ? dropResolver.Resolve(project, name, record, profile, language, ct) : null,
                wantAffixes ? affixResolver.Resolve(project, name, record, profile, language, ct) : null), token);
            if (token.IsCancellationRequested) return;
            if (dropResult != null) drops.Content = MainWindow.DropCard(dropResult);
            if (affixResult != null) affixes.Content = MainWindow.AffixCard(affixResult);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) (wantDrops ? drops : affixes).Content = new TextBlock { Text = ex.Message, Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; }
        finally { if (ReferenceEquals(detailsCancellation, work)) detailsCancellation = null; work.Dispose(); }
    }
}
