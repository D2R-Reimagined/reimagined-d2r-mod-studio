using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>Column guide flyout and the item hover-card toggle, both living on the table toolbar.</summary>
public sealed partial class EditorPane
{
    /// <summary>Whether hovering a unique/set item row pops up its rendered tooltip. Shared by every pane; the window persists it.</summary>
    public static bool ItemHoverCards { get; set; } = true;
    public static event Action? ItemHoverCardsChanged;
    public static void RaiseItemHoverCardsChanged() => ItemHoverCardsChanged?.Invoke();
    /// <summary>Whether headers (and Row Editor labels) lead with the spreadsheet letter of the column, A … Z, AA …, for people who know the TXT files by letter. Shared by every pane; the window persists it.</summary>
    public static bool ColumnLetters { get; set; }
    public static event Action? ColumnLettersChanged;
    public static void RaiseColumnLettersChanged() => ColumnLettersChanged?.Invoke();
    /// <summary>"AB · name" when letters are on, otherwise the name alone.</summary>
    public static string ColumnLabel(int index, string name) => ColumnLetters ? TableData.ColumnLetter(index) + " · " + name : name;

    /// <summary>The View menu on the toolbar: column letters for tables, the source editor font for everything.</summary>
    private void InitializeViewMenu(Panel toolbar)
    {
        var view = EditorToolbarIcons.Create("View"); var menu = new ContextMenu(); var items = new List<Control>();
        MenuItem? letters = null;
        if (Document.Table != null)
        {
            letters = new MenuItem { Header = "Column letters (A, B … AA) in headers", ToggleType = MenuItemToggleType.CheckBox, IsChecked = ColumnLetters };
            ToolTip.SetTip(letters, "Letters follow the column order of the TXT file, the way a spreadsheet shows it, even when columns are frozen or moved on screen.");
            letters.Click += (_, _) => { ColumnLetters = !ColumnLetters; RaiseColumnLettersChanged(); };
            items.Add(letters);
            void Follow() { if (columnMap.Count > 0) { UpdateColumnHeaders(); UpdateNote(); } }
            AttachedToVisualTree += (_, _) => { Follow(); ColumnLettersChanged += Follow; };
            DetachedFromVisualTree += (_, _) => ColumnLettersChanged -= Follow;
        }
        var zoomIn = new MenuItem { Header = "Larger text", InputGesture = new KeyGesture(Key.OemPlus, KeyModifiers.Control) }; zoomIn.Click += (_, _) => ViewSettings.Zoom(Source.IsVisible, 1);
        var zoomOut = new MenuItem { Header = "Smaller text", InputGesture = new KeyGesture(Key.OemMinus, KeyModifiers.Control) }; zoomOut.Click += (_, _) => ViewSettings.Zoom(Source.IsVisible, -1);
        var zoomReset = new MenuItem { Header = "Default text size", InputGesture = new KeyGesture(Key.D0, KeyModifiers.Control) }; zoomReset.Click += (_, _) => ViewSettings.Zoom(Source.IsVisible, 0);
        var settings = new MenuItem { Header = "View settings…" };
        settings.Click += async (_, _) => { try { if (TopLevel.GetTopLevel(this) is Window owner) await ViewSettings.ShowDialogAsync(owner); } catch (Exception ex) { error(ex); } };
        items.AddRange([zoomIn, zoomOut, zoomReset, new Separator(), settings]);
        menu.ItemsSource = items;
        view.Click += (_, _) => { if (letters != null) letters.IsChecked = ColumnLetters; menu.Open(view); };
        toolbar.Children.Add(view);
    }

    /// <summary>Ctrl+wheel and Ctrl+plus/minus/0 change the font size of the view under the pointer (table or source), Ctrl+0 restores the default.</summary>
    private void InitializeZoom()
    {
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
            e.Handled = true; ViewSettings.Zoom(Source.IsVisible, e.Delta.Y > 0 ? 1 : -1);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            int? steps = e.Key switch { Key.OemPlus or Key.Add => 1, Key.OemMinus or Key.Subtract => -1, Key.D0 or Key.NumPad0 => 0, _ => null };
            if (steps == null) return;
            e.Handled = true; ViewSettings.Zoom(Source.IsVisible, steps.Value);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    /// <summary>Pointer position within the row that raised the last <see cref="ItemHovered"/>, so a hover card can avoid the pointer.</summary>
    public Point HoverPosition { get; private set; }
    private ToggleButton? itemCardToggle;

    /// <summary>Raised to bring up the Column Guide tab of the window's bottom panel for a column.</summary>
    public event Action<EditorPane, string>? ColumnGuideRequested;
    /// <summary>Opens the Column Guide tab on a column; it then follows the selected cell as usual.</summary>
    public void ShowColumnGuide(string column) { if (Document.Table != null) ColumnGuideRequested?.Invoke(this, column); }

    /// <summary>Folders the colour-transform picker searches for an act palette: the project and its deployment. Set by the window.</summary>
    public static Func<IEnumerable<string>>? PaletteRoots { get; set; }
    /// <summary>Whether the selected cell holds a hue table number (monstats TransLvl, superuniques Utrans).</summary>
    public bool SelectedCellIsColorTransform => Document.Table is { IsCatalog: false } table && PaletteShifts.IsTransformColumn(table.Name, SelectedColumn);
    /// <summary>Opens the swatch picker for the selected cell; a chosen table is written into every selected cell of that column.</summary>
    public void ShowColorTransformPicker(Control? anchor, string? column = null)
    {
        var table = Document.Table; if (table == null) return;
        column ??= SelectedColumn; int index = table.ColumnIndex(column);
        if (table.IsCatalog || index < 0 || !PaletteShifts.IsTransformColumn(table.Name, column)) return;
        var value = SelectedRow >= 0 && SelectedRow < table.Records.Count ? table.Cell(SelectedRow, column) : "";
        var roots = (PaletteRoots?.Invoke() ?? []).Append(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Document.FilePath)!, "..", ".."))).ToArray();
        ColorTransformPicker.Show(anchor ?? this, table.Name, column, value, roots, picked =>
        {
            var targets = selectedCells.Where(c => c.Col == index).ToArray();
            ApplyToCells(targets.Length > 0 ? targets : [(SelectedRow, index)], picked, null);
        }, error);
    }

    private void InitializeItemCardToggle(Panel toolbar)
    {
        if (Document.Table?.Name is not ("uniqueitems" or "setitems")) return;
        itemCardToggle = new ToggleButton { Content = "Hover cards", IsChecked = ItemHoverCards, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0), Padding = new(8, 4) };
        ToolTip.SetTip(itemCardToggle, "Show the rendered item when the pointer rests on a row. The Item Preview tab always shows the selected item.");
        itemCardToggle.IsCheckedChanged += (_, _) => { if (ItemHoverCards != (itemCardToggle.IsChecked == true)) { ItemHoverCards = itemCardToggle.IsChecked == true; RaiseItemHoverCardsChanged(); } };
        // Follow toggles made in other panes; subscribe only while in the tree so closed panes are not kept alive by the static event.
        void Follow() { if (itemCardToggle.IsChecked != ItemHoverCards) itemCardToggle.IsChecked = ItemHoverCards; }
        AttachedToVisualTree += (_, _) => { Follow(); ItemHoverCardsChanged += Follow; };
        DetachedFromVisualTree += (_, _) => ItemHoverCardsChanged -= Follow;
        toolbar.Children.Add(itemCardToggle);
    }
}
