using System.Text.Json.Nodes;

namespace ModStudio.Core;

/// <summary>
/// One state of a layout that shows different widgets at different times: which widgets it hides (by path), sprites it
/// swaps (widget path → filename), and the tab it selects on a tab bar. Pages only change what the designer draws.
/// </summary>
public sealed record UiPage(string Label, string Tip, IReadOnlySet<string> Hidden, IReadOnlyDictionary<string, string> Sprites, string? TabBar = null, int Tab = -1);

/// <summary>Pages that replace one another. <see cref="Preferred"/> sets open on their first page; others open showing everything.</summary>
public sealed record UiPageSet(string Label, IReadOnlyList<UiPage> Pages, bool Preferred);

/// <summary>
/// Finds the pages of a layout. Some panels hold several screens in one file and the game shows one at a time: the stash
/// keeps its personal and shared grid and its gem, material and rune pages side by side, switched by its tab bar. A tab
/// bar whose tab names (its textStrings) name containers beside it is read as such a switch, with a per-tab background
/// when the panel lists one per tab (the stash's backgroundFile). Otherwise, sibling containers whose contents overlap
/// are offered as alternatives.
/// </summary>
public static class UiPages
{
    public static IReadOnlyList<UiPageSet> Find(UiScene scene, Func<string, string?>? localize = null)
    {
        var sets = new List<UiPageSet>();
        var claimed = new HashSet<UiWidget>();
        foreach (var bar in scene.Widgets.Where(w => w.Type == "TabBarWidget" && w.Parent != null))
            if (Tabbed(scene, bar, localize, claimed) is { } set) sets.Add(set);
        foreach (var parent in scene.Widgets)
            foreach (var group in OverlappingContainers(parent).Where(g => g.All(c => !claimed.Contains(c))))
            {
                var pages = group.Select(shown => new UiPage(shown.Name, $"Shows {shown.Name} and hides {string.Join(", ", group.Where(c => c != shown).Select(c => c.Name))}",
                    group.Where(c => c != shown).Select(c => c.Path).ToHashSet(StringComparer.Ordinal), new Dictionary<string, string>())).ToList();
                sets.Add(new($"Overlapping in {parent.Name}", pages, false));
            }
        return sets;
    }

    /// <summary>Containers: widgets that hold others and draw nothing themselves.</summary>
    private static bool IsContainer(UiWidget w) => w.Children.Count > 0 && UiLayout.SpriteField(w) == null && w.String("text") == null;

    /// <summary>A tab key ("@gems" → "gems") and whether a container's name belongs to it. Tabs showing the basic stash are personal and shared; the Horadric Cube sits on the materials tab.</summary>
    private static bool Belongs(string container, string tab)
    {
        var name = container.ToLowerInvariant();
        if (tab is "personal" or "shared" && name.Contains("basic", StringComparison.Ordinal)) return true;
        if (tab == "materials" && name.Contains("cube", StringComparison.Ordinal)) return true;
        return tab.Length >= 3 && (name.Contains(tab, StringComparison.Ordinal) || tab.EndsWith('s') && name.Contains(tab[..^1], StringComparison.Ordinal));
    }

    private static UiPageSet? Tabbed(UiScene scene, UiWidget bar, Func<string, string?>? localize, HashSet<UiWidget> claimed)
    {
        if (bar.Value("textStrings") is not JsonArray strings || strings.Count < 2) return null;
        var labels = strings.Select(s => (s as JsonValue)?.TryGetValue<string>(out var t) == true ? t : "").ToList();
        var keys = labels.Select(l => l.TrimStart('@').ToLowerInvariant()).ToList();
        var panel = bar.Parent!;
        // Containers anywhere in the panel whose names belong to a tab (the shared-stash navigation sits inside the basic stash).
        var candidates = panel.Descendants().Where(w => w != bar && IsContainer(w) && keys.Any(k => Belongs(w.Name, k))).ToList();
        // One entry per switch: a container inside another that belongs to the same tabs goes with it (the cube's grid with the cube).
        string TabsOf(UiWidget w) => string.Concat(keys.Select(k => Belongs(w.Name, k) ? "1" : "0"));
        candidates = [.. candidates.Where(c => !candidates.Any(a => a != c && IsInside(c, a) && TabsOf(a) == TabsOf(c)))];
        if (candidates.Count < 2) return null;
        // A per-tab picture: an array field of the panel (or the root) with one sprite name per tab, drawn by the panel's
        // background image (the stash's backgroundFile).
        var owner = new[] { panel, scene.Root }.FirstOrDefault(p => p.Fields.Keys.Any(k => k.Contains("background", StringComparison.OrdinalIgnoreCase) && p.Value(k) is JsonArray a && a.Count == keys.Count));
        var pictures = owner?.Fields.Keys.Where(k => k.Contains("background", StringComparison.OrdinalIgnoreCase)).Select(k => owner.Value(k) as JsonArray).FirstOrDefault(a => a?.Count == keys.Count);
        var background = pictures == null ? null : owner!.Children.FirstOrDefault(c => c.Type == "ImageWidget" && c.Name.Equals("background", StringComparison.OrdinalIgnoreCase));
        var pages = new List<UiPage>();
        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var shown = candidates.Where(c => Belongs(c.Name, key)).ToList();
            // A container stays when a shown container is inside it (the basic stash holds the shared tabs' navigation).
            var hidden = candidates.Where(c => !shown.Contains(c) && !shown.Any(s => IsInside(s, c))).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
            var sprites = new Dictionary<string, string>(StringComparer.Ordinal);
            if (background != null && (pictures![i] as JsonValue)?.TryGetValue<string>(out var picture) == true && picture.Length > 0) sprites[background.Path] = picture;
            var label = labels[i].StartsWith('@') ? localize?.Invoke(labels[i][1..]) ?? key : labels[i];
            label = label.Length == 0 ? $"Tab {i + 1}" : label;
            var tip = shown.Count == 0 ? $"Tab {i + 1}: none of the containers is named for it" : $"Tab {i + 1}: shows {string.Join(", ", shown.Select(s => s.Name))}";
            if (sprites.Count > 0) tip += $"; background {sprites.Values.First()}";
            pages.Add(new(label, tip, hidden, sprites, bar.Path, i));
        }
        claimed.UnionWith(candidates);
        return new($"{bar.Name} tabs", pages, true);
    }

    private static bool IsInside(UiWidget widget, UiWidget container) { for (var at = widget.Parent; at != null; at = at.Parent) if (at == container) return true; return false; }

    /// <summary>What a container covers on screen: its own rect when it has one, otherwise the union of what it holds.</summary>
    public static UiRect Extent(UiWidget w)
    {
        var own = w.Bounds;
        if (own.Width > 0 && own.Height > 0) return own;
        var union = new UiRect(0, 0, 0, 0);
        foreach (var d in w.Descendants()) if (d.Bounds is { Width: > 0, Height: > 0 } b) union = union.Union(b);
        return union;
    }

    private static bool RuntimeLayout(string type) =>
        type.Contains("Table", StringComparison.Ordinal) || type.Contains("List", StringComparison.Ordinal) || type.Contains("ScrollView", StringComparison.Ordinal) || type.Contains("Stack", StringComparison.Ordinal);

    /// <summary>Groups of a widget's container children that cover the same area (each overlapping another by at least 40% of the smaller).</summary>
    private static IEnumerable<List<UiWidget>> OverlappingContainers(UiWidget parent)
    {
        // Tables, lists and stacks place their rows at runtime, so rows written without positions only look stacked.
        if (RuntimeLayout(parent.Type)) yield break;
        var containers = parent.Children.Where(c => IsContainer(c) && !RuntimeLayout(c.Type)).Select(c => (Widget: c, Area: Extent(c))).Where(c => c.Area.Width > 0 && c.Area.Height > 0).ToList();
        if (containers.Count < 2) yield break;
        var group = Enumerable.Range(0, containers.Count).ToArray();
        int Find(int k) => group[k] == k ? k : group[k] = Find(group[k]);
        for (int a = 0; a < containers.Count; a++)
            for (int b = a + 1; b < containers.Count; b++)
            {
                var x = containers[a].Area; var y = containers[b].Area;
                double w = Math.Min(x.Right, y.Right) - Math.Max(x.X, y.X), h = Math.Min(x.Bottom, y.Bottom) - Math.Max(x.Y, y.Y);
                if (w > 0 && h > 0 && w * h >= 0.4 * Math.Min(x.Width * x.Height, y.Width * y.Height)) group[Find(a)] = Find(b);
            }
        foreach (var members in Enumerable.Range(0, containers.Count).GroupBy(Find).Where(g => g.Count() > 1))
            yield return [.. members.Select(k => containers[k].Widget)];
    }
}
