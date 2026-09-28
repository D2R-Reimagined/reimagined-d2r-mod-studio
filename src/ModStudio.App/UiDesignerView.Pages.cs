using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// Pages: layouts that hold several screens in one file (the stash's personal, shared, gem, material and rune tabs) are
/// shown one screen at a time, the way the game shows them. Switching pages only changes what the canvas draws.
/// </summary>
internal sealed partial class UiDesignerView
{
    /// <summary>A choice in the Page menu: a page of a set, or everything (Page null).</summary>
    internal sealed record PageItem(string Label, UiPageSet? Set, UiPage? Page)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox pageChoice = new() { MinWidth = 140 };
    private readonly StackPanel pagePanel = new() { Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false };
    private IReadOnlyList<UiPageSet> pageSets = [];
    private (string Set, string Page)? pageKey;
    private bool pageChosen, syncingPages;

    internal IReadOnlyList<PageItem> PageItems => pageChoice.ItemsSource as IReadOnlyList<PageItem> ?? [];
    internal UiPage? CurrentPage => canvas.Page;

    private void InitializePages(Panel bar)
    {
        pagePanel.Children.Add(Caption("Page"));
        ToolTip.SetTip(pageChoice, "This layout holds several screens the game shows one at a time. Pick the one to see and edit; the others are hidden on the canvas (the file is not changed). Clicking a tab on the canvas does the same.");
        AutomationProperties.SetName(pageChoice, "Page");
        pagePanel.Children.Add(pageChoice);
        bar.Children.Add(pagePanel);
        pageChoice.SelectionChanged += (_, _) => { if (!syncingPages && pageChoice.SelectedItem is PageItem item) ShowPage(item, user: true); };
        canvas.TabClicked += (bar, tab) =>
        {
            if (PageItems.FirstOrDefault(i => i.Page?.TabBar == bar.Path && i.Page.Tab == tab) is { } item) pageChoice.SelectedItem = item;
        };
    }

    /// <summary>Finds the scene's pages again after a resolve, keeping the page shown (by name), or opening a tabbed panel on its first tab.</summary>
    private void RefreshPages()
    {
        if (scene == null || assets == null) return;
        pageSets = UiPages.Find(scene, key => assets.Localize(key));
        var items = new List<PageItem> { new("All pages", null, null) };
        foreach (var set in pageSets)
            foreach (var page in set.Pages)
                items.Add(new(pageSets.Count > 1 ? $"{page.Label} · {set.Label}" : page.Label, set, page));
        pagePanel.IsVisible = pageSets.Count > 0;
        var keep = pageKey is { } key ? items.FirstOrDefault(i => i.Set?.Label == key.Set && i.Page?.Label == key.Page) : null;
        if (keep == null && !pageChosen && pageSets.FirstOrDefault(s => s.Preferred) is { } preferred) keep = items.First(i => i.Set == preferred);
        keep ??= items[0];
        syncingPages = true;
        try { pageChoice.ItemsSource = items; pageChoice.SelectedItem = keep; }
        finally { syncingPages = false; }
        ShowPage(keep, user: false);
    }

    private void ShowPage(PageItem item, bool user)
    {
        if (user) pageChosen = true;
        pageKey = item.Page == null ? null : (item.Set!.Label, item.Page.Label);
        bool changed = !Equals(canvas.Page, item.Page);
        canvas.Page = item.Page;
        canvas.InvalidateVisual();
        if (!changed) return;
        // The tree follows the page: the other pages' containers fold away, the shown page's open.
        foreach (var path in pageSets.SelectMany(s => s.Pages).SelectMany(p => p.Hidden).Distinct())
            if (item.Page?.Hidden.Contains(path) == true) collapsed.Add(path); else collapsed.Remove(path);
        RebuildTree();
        if (user) SetStatus(item.Page == null ? "Showing every page at once." : $"Showing {item.Page.Label}: {item.Page.Tip}. Other pages are hidden on the canvas only.");
    }

    /// <summary>Shows the page with this label ("All pages" for everything), as picking it in the menu does.</summary>
    internal bool ShowPage(string label)
    {
        if (PageItems.FirstOrDefault(i => i.Label == label) is not { } item) return false;
        pageChoice.SelectedItem = item;
        return true;
    }

    /// <summary>Whether a page hides a widget (it or a container around it).</summary>
    private static bool Hides(UiPage page, UiWidget widget)
    {
        for (var at = widget; at != null; at = at.Parent) if (page.Hidden.Contains(at.Path)) return true;
        return false;
    }

    /// <summary>A widget picked in the tree on a page that hides it: show the first page of that set that has it.</summary>
    private void RevealPage(UiWidget? widget)
    {
        if (widget == null || canvas.Page is not { } page || !Hides(page, widget)) return;
        var item = PageItems.FirstOrDefault(i => i.Page != null && pageSets.Any(s => s.Pages.Contains(page) && s.Pages.Contains(i.Page)) && !Hides(i.Page, widget))
            ?? PageItems.First();
        pageChoice.SelectedItem = item;
    }
}
