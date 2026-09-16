using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Media;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private readonly ItemPreviewResolver itemResolver = new();
    private readonly PreviewWorkQueue itemWork = new();
    private readonly ContentControl itemPreviewContent = new();
    private readonly NumericUpDown itemLevel = new() { Minimum = 1, Maximum = 99, Value = 80, Width = 115 };
    private readonly ComboBox itemLocale = new() { ItemsSource = ModProject.GameLocales, SelectedItem = "enUS", MinWidth = 90 };
    private string? itemLocaleProject; private bool refreshingItemLocales;
    private string ItemLocale => itemLocale.SelectedItem as string ?? "enUS";
    private TabItem itemPreviewTab = null!;
    private CancellationTokenSource? itemPreviewCancellation;
    private CancellationTokenSource? itemTooltipCloseCancellation;
    private Control? itemTooltipAnchor;
    private EditorPane? itemRequestPane;
    private bool itemHoverRequest;
    private int resolvedWorkspace = -1;
    private string? resolvedProject;
    internal Task PendingItemPreview { get; private set; } = Task.CompletedTask;
    internal ItemPreviewResult? LastItemPreview { get; private set; }
    private void InitializeItemPreview()
    {
        var options = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto"), Margin = new(0, 0, 0, 8) };
        options.Children.Add(new TextBlock { Text = "Character level" });
        var localeLabel = new TextBlock { Text = "Locale" }; Grid.SetColumn(localeLabel, 1); options.Children.Add(localeLabel);
        Grid.SetRow(itemLevel, 1); itemLevel.HorizontalAlignment = HorizontalAlignment.Left; options.Children.Add(itemLevel);
        Grid.SetRow(itemLocale, 1); Grid.SetColumn(itemLocale, 1); itemLocale.HorizontalAlignment = HorizontalAlignment.Left; options.Children.Add(itemLocale);
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(options, Dock.Top); panel.Children.Add(options);
        panel.Children.Add(new ScrollViewer { Content = itemPreviewContent });
        itemPreviewTab = new TabItem { Header = new TextBlock { Text = "Item Preview", FontSize = 12 }, Content = panel, IsVisible = false }; InspectorTabs.Items.Add(itemPreviewTab);
        var hoverCards = new CheckBox { Content = "Hover cards over table rows", IsChecked = EditorPane.ItemHoverCards, Margin = new(0, 6, 0, 0) };
        ToolTip.SetTip(hoverCards, "Also available as the Hover cards toggle on the table toolbar. Off keeps the rendered item in this tab only.");
        Grid.SetRow(hoverCards, 2); Grid.SetColumnSpan(hoverCards, 2); options.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); options.Children.Add(hoverCards);
        hoverCards.IsCheckedChanged += (_, _) => { if (EditorPane.ItemHoverCards != (hoverCards.IsChecked == true)) { EditorPane.ItemHoverCards = hoverCards.IsChecked == true; EditorPane.RaiseItemHoverCardsChanged(); } };
        EditorPane.ItemHoverCardsChanged += () =>
        {
            if (hoverCards.IsChecked != EditorPane.ItemHoverCards) hoverCards.IsChecked = EditorPane.ItemHoverCards;
            if (!EditorPane.ItemHoverCards && itemHoverRequest) CancelItemPreview();
            if (Program.Arguments.Contains("--smoke")) return;
            try { var prefs = StudioPreferences.Load(StudioPreferences.DefaultFile); prefs.ItemHoverCards = EditorPane.ItemHoverCards; prefs.Save(StudioPreferences.DefaultFile); }
            catch (Exception ex) { ShowError(ex); }
        };
        if (!Program.Arguments.Contains("--smoke"))
            try { EditorPane.ItemHoverCards = StudioPreferences.Load(StudioPreferences.DefaultFile).ItemHoverCards; hoverCards.IsChecked = EditorPane.ItemHoverCards; } catch (Exception) { }
        itemLevel.ValueChanged += (_, _) => RefreshItemPreview(); itemLocale.SelectionChanged += (_, _) => { if (!refreshingItemLocales && itemLocale.SelectedItem != null) RefreshItemPreview(); };
        InspectorTabs.SelectionChanged += (_, _) => RefreshItemPreview();
        Closed += (_, _) => CancelItemPreview();
        itemPreviewContent.Content = new TextBlock { Text = "Select a Unique or Set item.", TextWrapping = TextWrapping.Wrap };
    }
    private void CancelItemPreview()
    {
        CancelScheduledItemTooltipClose();
        itemPreviewCancellation?.Cancel();
        if (itemTooltipAnchor != null) { ToolTip.SetIsOpen(itemTooltipAnchor, false); ToolTip.SetTip(itemTooltipAnchor, null); itemTooltipAnchor.ClearValue(ToolTip.PlacementProperty); itemTooltipAnchor.ClearValue(ToolTip.CustomPopupPlacementCallbackProperty); itemTooltipAnchor = null; }
    }
    private void CancelScheduledItemTooltipClose()
    {
        itemTooltipCloseCancellation?.Cancel();
        itemTooltipCloseCancellation = null;
    }
    /// <summary>Offers the project's catalog locales; keeps the current choice when it is still available.</summary>
    private void RefreshItemLocales()
    {
        if (project?.Root == itemLocaleProject) return;
        itemLocaleProject = project?.Root;
        var locales = project?.Locales() ?? ModProject.GameLocales;
        var current = ItemLocale; refreshingItemLocales = true;
        try { itemLocale.ItemsSource = locales; itemLocale.SelectedItem = locales.Contains(current) ? current : locales.Contains("enUS") ? "enUS" : locales.FirstOrDefault(); }
        finally { refreshingItemLocales = false; }
    }
    private void ScheduleItemTooltipClose()
    {
        CancelScheduledItemTooltipClose();
        if (!itemHoverRequest) return;
        // The pointer left the row before its hover request resolved. Drop the request so the
        // tooltip cannot open later at wherever the pointer has moved to.
        if (itemTooltipAnchor == null) { CancelItemPreview(); return; }
        var wait = itemTooltipCloseCancellation = new CancellationTokenSource();
        _ = CloseItemTooltipAfterGraceAsync(wait);
    }
    private async Task CloseItemTooltipAfterGraceAsync(CancellationTokenSource wait)
    {
        try
        {
            await Task.Delay(300, wait.Token);
            if (ReferenceEquals(itemTooltipCloseCancellation, wait)) CancelItemPreview();
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(itemTooltipCloseCancellation, wait)) itemTooltipCloseCancellation = null;
            wait.Dispose();
        }
    }
    private void RefreshItemPreview()
    {
        if (InspectorTabs == null || itemPreviewTab == null) return;
        CancelItemPreview(); LastItemPreview = null;
        bool available = Active?.Document.Table?.Name is "uniqueitems" or "setitems";
        if (!available && InspectorTabs.SelectedItem == itemPreviewTab) InspectorTabs.SelectedIndex = 0;
        itemPreviewTab.IsVisible = available;
        if (!available)
        {
            itemPreviewContent.Content = new TextBlock { Text = "Select a Unique or Set item.", TextWrapping = TextWrapping.Wrap };
            return;
        }
        if (InspectorTabs.SelectedItem != itemPreviewTab) return;
        RefreshItemLocales();
        if (Active is { } pane && pane.Document.Table?.Name is "uniqueitems" or "setitems") RequestItemPreview(pane, pane.SelectedRow, null);
        else itemPreviewContent.Content = new TextBlock { Text = "Select a Unique or Set item.", TextWrapping = TextWrapping.Wrap };
    }
    private void RequestItemPreview(EditorPane pane, int row, Control? anchor, Point? at = null)
    {
        CancelItemPreview();
        itemRequestPane = pane; itemHoverRequest = anchor != null;
        PendingItemPreview = LoadItemPreviewAsync(pane, row, anchor, at);
    }
    private async Task LoadItemPreviewAsync(EditorPane pane, int row, Control? anchor, Point? at)
    {
        var work = itemPreviewCancellation = new CancellationTokenSource(); var token = work.Token;
        var selectedProject = project; var table = pane.Document.Table; var profile = Profile;
        int revision = pane.Document.Revision, workspace = workspaceRevision, level = (int)(itemLevel.Value ?? 80);
        string locale = ItemLocale;
        if (selectedProject == null || table == null || row < 0 || row >= table.Records.Count) { itemPreviewCancellation = null; work.Dispose(); return; }
        try
        {
            if (anchor == null) itemPreviewContent.Content = new TextBlock { Text = "Resolving item…" };
            await Task.Delay(anchor == null ? 120 : 350, token);
            Storage.Require(!pane.Document.PendingSource, "Apply the source changes before previewing this item.");
            // Clone only the selected row on the UI thread. Mutable dependency tables are never shared with a worker.
            var record = (JsonObject)table.Records[row]!.DeepClone();
            var dirtyDependency = tabs.Select(t => t.Content).OfType<EditorPane>().FirstOrDefault(p => p != pane && p.Document.IsDirty &&
                (p.Document.Table?.Name is "weapons" or "armor" or "misc" or "properties" or "itemstatcost" or "sets" || p.Document.Table?.IsCatalog == true || p.Document.FilePath.Contains("compatibility" + System.IO.Path.DirectorySeparatorChar)));
            Storage.Require(dirtyDependency == null, "Save the edited dependency before previewing: " + dirtyDependency?.Document.FilePath);
            var result = await itemWork.RunAsync(ct => {
                if (resolvedProject != selectedProject.Root || resolvedWorkspace != workspace) { itemResolver.Clear(); resolvedProject = selectedProject.Root; resolvedWorkspace = workspace; }
                return itemResolver.Resolve(selectedProject, table.Name, record, profile, level, locale, ct);
            }, token);
            if (token.IsCancellationRequested || selectedProject != project || revision != pane.Document.Revision || workspace != workspaceRevision || Active != pane || Profile != profile) return;
            LastItemPreview = result;
            if (anchor == null) itemPreviewContent.Content = ItemCard(result);
            else ShowItemTooltip(anchor, result, at);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            var failed = new ItemPreviewResult("Preview unavailable", false, [], [ex.Message]); LastItemPreview = failed;
            if (anchor == null) itemPreviewContent.Content = ItemCard(failed);
            else ShowItemTooltip(anchor, failed, at);
        }
        finally { if (ReferenceEquals(itemPreviewCancellation, work)) itemPreviewCancellation = null; work.Dispose(); }
    }
    private void ShowItemTooltip(Control anchor, ItemPreviewResult result, Point? at = null)
    {
        // The default tooltip theme constrains and centers its content. A wider child
        // was consequently clipped on both sides. Size the popup and its viewport together.
        double width = Math.Min(440, Math.Max(1, Bounds.Width - 32));
        // A card that fits neither below nor above the row would be slid over it and end up under the pointer; the row
        // then loses the pointer, the card closes, the next move reopens it, and the card flickers for as long as the
        // pointer moves. Cap the height to the roomier side of the row and place the card there, beside the pointer.
        var rowTop = anchor.TranslatePoint(new Point(), this)?.Y ?? 0; double rowHeight = Math.Max(1, anchor.Bounds.Height);
        double below = Bounds.Height - rowTop - rowHeight - 16, above = rowTop - 16;
        bool placeBelow = below >= above || below >= 320;
        double height = Math.Clamp(Math.Min(560, placeBelow ? below : above), 120, Math.Max(120, Bounds.Height - 48));
        double pointerX = Math.Clamp(at?.X ?? 0, 0, Math.Max(0, anchor.Bounds.Width - 1));
        ToolTip.SetPlacement(anchor, PlacementMode.Custom);
        ToolTip.SetCustomPopupPlacementCallback(anchor, placement =>
        {
            placement.AnchorRectangle = new Rect(pointerX, 0, 1, rowHeight);
            placement.Anchor = placeBelow ? PopupAnchor.Bottom : PopupAnchor.Top;
            placement.Gravity = placeBelow ? PopupGravity.BottomRight : PopupGravity.TopRight;
            placement.Offset = new Point(12, placeBelow ? 4 : -4);
            placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.FlipY | PopupPositionerConstraintAdjustment.ResizeY;
        });
        var scroll = new ScrollViewer {
            Content = ItemCard(result), MaxHeight = height,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var tooltip = new ToolTip {
            Content = scroll, Width = width, MaxWidth = width, MaxHeight = height,
            Padding = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        // Keep the popup open while the user moves off the row and into its selectable text.
        tooltip.PointerEntered += (_, _) => CancelScheduledItemTooltipClose();
        tooltip.PointerExited += (_, _) => ScheduleItemTooltipClose();
        itemTooltipAnchor = anchor;
        ToolTip.SetTip(anchor, tooltip); ToolTip.SetIsOpen(anchor, true);
    }
    private static Control ItemCard(ItemPreviewResult result)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new(12), MaxWidth = 440 };
        panel.Children.Add(new SelectableTextBlock { Text = result.Name, FontSize = 18, Foreground = result.IsSet ? Brushes.LightGreen : Brushes.Tan, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new SelectableTextBlock { Text = string.Join("\n", result.Lines), Foreground = Brushes.LightSteelBlue, TextWrapping = TextWrapping.Wrap });
        if (result.Issues.Length > 0) panel.Children.Add(new SelectableTextBlock { Text = "Incomplete preview\n" + string.Join("\n", result.Issues), Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap });
        return new Border { Background = new SolidColorBrush(Color.Parse("#151515")), Child = panel };
    }
}
