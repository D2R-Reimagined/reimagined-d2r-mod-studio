using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>
/// The profile side of a table editor. In the shared view (the default) cells a profile changes carry a small corner
/// mark in that profile's color, and rows a profile leaves out a line through them in its color. In a profile's view the
/// grid shows what that profile builds: its own changes tinted in its color, changes it inherits fainter in the parent's,
/// stale changes in red and left-out rows struck through. Typing, pasting and clearing there write the profile's
/// overrides (compatibility/&lt;profile&gt;/), never the shared table; Ctrl+Z undoes them.
/// </summary>
public sealed partial class EditorPane
{
    /// <summary>The open project; set by the main window. Without it the pane has no profile view.</summary>
    public Func<ModProject?>? ProfileProject { get; set; }
    /// <summary>Raised after this pane wrote profile files, so the main window can refresh what depends on them.</summary>
    public event Action<EditorPane, ProfileWrite>? ProfileWritten;
    /// <summary>Raised when the pane switches between the shared view and a profile's view.</summary>
    public event Action<EditorPane>? ProfileLensChanged;
    /// <summary>Asks the main window to open a file (a profile's rule file) in a tab.</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>Profiles whose marks the shared view hides; shared by every pane so the choice reads the same everywhere.</summary>
    private static readonly HashSet<string> hiddenMarkProfiles = new(StringComparer.Ordinal);
    private static event Action? MarkVisibilityChanged;
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#B9AD97"));

    private string? lens;
    /// <summary>The profile whose view the grid shows, or null for shared source.</summary>
    public string? ProfileLens => lens;
    private ProfileOverlay? lensOverlay;
    private string? overlayProblem;
    private readonly Dictionary<string, Color> profileColors = new(StringComparer.Ordinal);
    /// <summary>Shared view: per cell, the colors of the profiles whose own layer changes it.</summary>
    private readonly Dictionary<(int Row, string Column), List<Color>> markedCells = [];
    /// <summary>Shared view: per row, the colors of the profiles that leave it out.</summary>
    private readonly Dictionary<int, List<Color>> markedRows = [];
    private readonly Dictionary<string, List<string>> markSummary = new(StringComparer.Ordinal);
    /// <summary>Shared view: the overlay of every profile with marks, for hover text.</summary>
    private readonly Dictionary<string, ProfileOverlay> markOverlays = new(StringComparer.Ordinal);
    /// <summary>What the cell editor holds in a profile's view until it commits; written as one override then.</summary>
    private readonly Dictionary<(int Row, int Col), string> lensPending = [];
    private readonly List<ProfileWrite> profileUndo = [], profileRedo = [];
    private ComboBox? lensPicker;
    private TextBox? lensReason;
    private Button? marksButton;
    private TextBlock? lensSummary;
    private readonly DispatcherTimer overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool updatingLensPicker;
    private readonly Dictionary<(uint Color, byte Alpha), IBrush> tintBrushes = [];

    private sealed record LensChoice(string? Id, string Label, Color? Color);
    /// <summary>The reason box of the profile view (smoke tests fill it).</summary>
    internal string ProfileReason { get => lensReason?.Text ?? ""; set { if (lensReason != null) lensReason.Text = value; } }
    /// <summary>Profiles the view picker offers.</summary>
    internal IReadOnlyList<string> ProfileLensChoices => (lensPicker?.ItemsSource as IEnumerable<LensChoice>)?.Select(c => c.Id).OfType<string>().ToArray() ?? [];
    internal string ProfileLensSummary => lensSummary?.Text ?? "";

    private void InitializeProfileLens(Panel toolbar)
    {
        if (Document.Table is not { IsCatalog: false }) return;
        lensPicker = new ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0) };
        ToolTip.SetTip(lensPicker, "Profile view: show the table as a profile builds it, and edit that profile's overrides instead of shared source");
        lensPicker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LensChoice>((choice, _) =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (choice?.Color is { } color) row.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new(2), Background = ProfileColors.Brush(color), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = choice?.Label ?? "", VerticalAlignment = VerticalAlignment.Center });
            return row;
        });
        lensPicker.SelectionChanged += (_, _) => { if (!updatingLensPicker && lensPicker.SelectedItem is LensChoice choice) SetProfileLens(choice.Id); };
        lensReason = new TextBox { Width = 220, PlaceholderText = "Reason for new overrides", IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0) };
        ToolTip.SetTip(lensReason, "Recorded with every new override this view writes (profile rules need a reason). Changing an existing override keeps its reason unless you type a new one.");
        marksButton = EditorToolbarIcons.Create("Profile marks");
        marksButton.Click += (_, _) => ShowMarksMenu();
        lensSummary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0), FontSize = 12, Foreground = MutedText };
        toolbar.Children.Add(lensPicker); toolbar.Children.Add(lensReason); toolbar.Children.Add(marksButton); toolbar.Children.Add(lensSummary);
        overlayTimer.Tick += (_, _) => { overlayTimer.Stop(); RecomputeProfileOverlays(); };
        Document.Changed += QueueProfileOverlays;
        void Follow() => QueueProfileOverlays();
        MarkVisibilityChanged += Follow;
        DetachedFromVisualTree += (_, _) => { overlayTimer.Stop(); MarkVisibilityChanged -= Follow; };
        AttachedToVisualTree += (_, _) => { MarkVisibilityChanged -= Follow; MarkVisibilityChanged += Follow; QueueProfileOverlays(); };
    }

    /// <summary>The project's profiles changed (added, renamed, deleted, recolored) or their files did: refresh the picker and the view.</summary>
    public void ProfilesChanged()
    {
        if (lensPicker == null) return;
        var project = ProfileProject?.Invoke();
        var profiles = project?.Profiles.ToArray() ?? [];
        if (lens != null && !profiles.Contains(lens)) { lens = null; ClearProfileHistory(); }
        profileColors.Clear();
        foreach (var id in profiles) profileColors[id] = ProfileColors.For(id, ProfileChainCache.TryGet(project!, id)?.Color);
        updatingLensPicker = true;
        try
        {
            var choices = profiles.Select(p => new LensChoice(p, "View as " + p, profileColors[p])).Prepend(new LensChoice(null, "Shared source", null)).ToArray();
            lensPicker.ItemsSource = choices; lensPicker.SelectedItem = choices.First(c => c.Id == lens);
        }
        finally { updatingLensPicker = false; }
        lensPicker.IsVisible = profiles.Length > 0;
        RecomputeProfileOverlays();
    }

    public void SetProfileLens(string? profile)
    {
        if (profile == lens) return;
        if (editingCell != null) activeGrid.CancelEdit();
        lens = profile; lensPending.Clear(); ClearProfileHistory();
        if (lensPicker?.ItemsSource is IEnumerable<LensChoice> choices && choices.FirstOrDefault(c => c.Id == profile) is { } choice && !ReferenceEquals(lensPicker.SelectedItem, choice))
        {
            updatingLensPicker = true; try { lensPicker.SelectedItem = choice; } finally { updatingLensPicker = false; }
        }
        RecomputeProfileOverlays();
        Refresh();
        ProfileLensChanged?.Invoke(this);
    }
    private void ClearProfileHistory() { profileUndo.Clear(); profileRedo.Clear(); }

    private void QueueProfileOverlays() { if (lensPicker == null) return; overlayTimer.Stop(); overlayTimer.Start(); }

    private void RecomputeProfileOverlays()
    {
        overlayTimer.Stop();
        lensOverlay = null; overlayProblem = null; markedCells.Clear(); markedRows.Clear(); markSummary.Clear(); markOverlays.Clear();
        var project = ProfileProject?.Invoke();
        if (lensPicker != null && project != null && Document.Table is { IsCatalog: false } table && !Document.PendingSource)
        {
            if (lens != null)
            {
                try { lensOverlay = ProfileOverlay.Compute(ProfileChainCache.Get(project, lens), table); }
                catch (Exception e) { overlayProblem = e.Message; }
            }
            else
                foreach (var id in project.Profiles)
                {
                    if (hiddenMarkProfiles.Contains(id)) continue;
                    try
                    {
                        var overlay = ProfileOverlay.Compute(ProfileChainCache.Get(project, id), table);
                        var color = profileColors.GetValueOrDefault(id, ProfileColors.For(id, null)); int cells = 0, rows = 0;
                        foreach (var (key, state) in overlay.Cells)
                            if (state.Layer == id) { (markedCells.TryGetValue(key, out var list) ? list : markedCells[key] = []).Add(color); cells++; }
                        foreach (var (row, state) in overlay.RemovedRows)
                            if (state.Layer == id) { (markedRows.TryGetValue(row, out var list) ? list : markedRows[row] = []).Add(color); rows++; }
                        if (cells + rows > 0) markOverlays[id] = overlay;
                        if (cells + rows > 0) markSummary[id] = [$"{cells} cell(s)", .. rows > 0 ? new[] { $"{rows} row(s) left out" } : []];
                        if (overlay.StructuralIssues.FirstOrDefault() is { } issue) overlayProblem ??= issue;
                    }
                    catch (Exception e) { overlayProblem ??= $"{id}: {e.Message}"; }
                }
        }
        UpdateLensSummary();
        RefreshLiveCells(); QueuePaint();
    }

    private void UpdateLensSummary()
    {
        if (lensSummary == null) return;
        if (lensReason != null) lensReason.IsVisible = lens != null;
        if (marksButton != null) marksButton.IsVisible = lens == null && profileColors.Count > 0;
        string text;
        if (lens != null && lensOverlay is { } overlay)
        {
            int own = overlay.Cells.Values.Count(s => s.Layer == lens), inherited = overlay.CellCount - own;
            int stale = overlay.Cells.Values.Count(s => s.Stale), locked = overlay.Cells.Values.Count(s => s.Layer == lens && !s.Editable);
            var parts = new List<string> { $"{own} change(s)" };
            if (inherited > 0) parts.Add($"{inherited} inherited");
            if (overlay.RemovedCount > 0) parts.Add($"{overlay.RemovedCount} row(s) left out");
            if (locked > 0) parts.Add($"{locked} locked (generated or per bank)");
            if (stale > 0) parts.Add($"⚠ {stale} stale");
            if (Document.IsDirty) parts.Add("save the table to edit");
            text = string.Join(" · ", parts);
            if (overlay.StructuralIssues.FirstOrDefault() is { } issue) text += " · ⚠ " + issue;
        }
        else if (lens != null) text = "⚠ " + (overlayProblem ?? "Profile unavailable.");
        else
        {
            text = string.Join(" · ", markSummary.Select(p => $"{p.Key}: {string.Join(", ", p.Value)}"));
            if (overlayProblem != null) text += (text.Length > 0 ? " · " : "") + "⚠ " + overlayProblem;
        }
        lensSummary.Text = text; ToolTip.SetTip(lensSummary, text.Length > 0 ? text : null);
    }

    private void ShowMarksMenu()
    {
        if (marksButton == null) return;
        var items = new List<Control>();
        foreach (var (id, color) in profileColors)
        {
            var shown = !hiddenMarkProfiles.Contains(id);
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new(2), Background = ProfileColors.Brush(color), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = (shown ? "✓ " : "    ") + id + (markSummary.TryGetValue(id, out var summary) ? " · " + string.Join(", ", summary) : "") });
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => { if (!hiddenMarkProfiles.Remove(id)) hiddenMarkProfiles.Add(id); MarkVisibilityChanged?.Invoke(); };
            items.Add(item);
        }
        items.Add(new Separator());
        var all = new MenuItem { Header = "Show every profile's marks" }; all.Click += (_, _) => { hiddenMarkProfiles.Clear(); MarkVisibilityChanged?.Invoke(); };
        var none = new MenuItem { Header = "Hide all marks" }; none.Click += (_, _) => { hiddenMarkProfiles.UnionWith(profileColors.Keys); MarkVisibilityChanged?.Invoke(); };
        items.Add(all); items.Add(none);
        new ContextMenu { ItemsSource = items }.Open(marksButton);
    }

    private IBrush Tint(Color color, byte alpha)
    {
        var key = (color.ToUInt32(), alpha);
        if (!tintBrushes.TryGetValue(key, out var brush)) tintBrushes[key] = brush = ProfileColors.Brush(color, alpha);
        return brush;
    }
    private Color ColorOf(string profile) => profileColors.GetValueOrDefault(profile, ProfileColors.For(profile, null));

    /// <summary>How a cell shows the active profile view (tint, strike) or the shared view's marks.</summary>
    internal readonly record struct ProfileCellLook(IBrush? Tint, bool Struck, IReadOnlyList<Color>? Marks, IReadOnlyList<Color>? Strikes);
    internal ProfileCellLook ProfileLook(int row, int col)
    {
        if (Document.Table is not { } table || col < 0 || col >= table.Columns.Length) return default;
        if (lens != null)
        {
            if (lensOverlay == null) return default;
            if (lensOverlay.Removed(row) is { } removed) return new(Tint(ColorOf(removed.Layer), removed.Layer == lens ? (byte)0x30 : (byte)0x18), true, null, null);
            if (lensPending.ContainsKey((row, col))) return new(Tint(ColorOf(lens), 0x55), false, null, null);
            if (lensOverlay.Cell(row, table.Columns[col]) is not { } state) return default;
            if (state.Stale || state.Below?.Stale == true) return new(Tint(ProfileColors.Stale, 0x66), false, null, null);
            return new(Tint(ColorOf(state.Layer), state.Layer == lens ? (byte)0x55 : (byte)0x26), false, state.Editable ? null : [ColorOf(state.Layer)], null);
        }
        var marks = markedCells.GetValueOrDefault((row, table.Columns[col]));
        var strikes = markedRows.GetValueOrDefault(row);
        return marks == null && strikes == null ? default : new(null, false, marks, strikes);
    }

    /// <summary>What the profile view shows for a cell (the profile's value), or null to show shared source.</summary>
    private string? LensValue(int row, int col)
    {
        if (lens == null || Document.Table is not { } table || row < 0 || col < 0 || col >= table.Columns.Length) return null;
        if (lensPending.TryGetValue((row, col), out var pending)) return pending;
        return lensOverlay?.Cell(row, table.Columns[col])?.Value;
    }
    /// <summary>A cell as the current view shows it: the profile's value in a profile view, shared source otherwise.</summary>
    internal string CellText(int row, string column)
    {
        var table = Document.Table!;
        return lens != null && lensOverlay?.Cell(row, column) is { } state ? state.Value : table.Cell(row, column);
    }
    /// <summary>RowView's write: in a profile view, held while the cell editor is open, otherwise written as an override.</summary>
    private bool LensWrite(int row, int col, string value)
    {
        if (lens == null) return false;
        if (editingCell is { } editing && editing == (row, col)) { lensPending[(row, col)] = value; return true; }
        try { WriteProfileCells([(row, col, value)]); } catch (Exception e) { error(e); }
        return true;
    }
    /// <summary>Called when the cell editor closes: a commit writes what it held, anything else drops it.</summary>
    private void FlushLensEdit(bool commit)
    {
        if (lensPending.Count == 0) return;
        var pending = lensPending.Select(p => (p.Key.Row, p.Key.Col, p.Value)).ToArray(); lensPending.Clear();
        if (!commit) { RefreshLiveCells(); return; }
        try { WriteProfileCells(pending); } catch (Exception e) { error(e); }
    }
    /// <summary>Paste, clear and multi-cell edits in a profile view: true when the edits went to the profile.</summary>
    private bool RouteToProfile(IEnumerable<(int Row, string Column, string Value)> edits)
    {
        if (lens == null) return false;
        var table = Document.Table!;
        WriteProfileCells(edits.Select(e => (e.Row, table.ColumnIndex(e.Column), e.Value)).ToArray());
        return true;
    }
    private void WriteProfileCells(IReadOnlyList<(int Row, int Col, string Value)> cells)
    {
        var table = Document.Table!;
        var edits = cells.Where(c => c.Row >= 0 && c.Row < table.Records.Count && IsEditableCell((c.Row, c.Col)) && c.Value != CellText(c.Row, table.Columns[c.Col]))
            .Select(c => (ProfileEdit)new SetProfileCell(c.Row, table.Columns[c.Col], c.Value)).ToArray();
        if (edits.Length > 0) WriteProfile(edits); else RefreshLiveCells();
    }
    /// <summary>Writes edits of the viewed profile, remembering them for undo.</summary>
    public void WriteProfile(IReadOnlyList<ProfileEdit> edits)
    {
        Storage.Require(lens != null && ProfileProject?.Invoke() != null && Document.Table is { IsCatalog: false }, "Switch to a profile's view to edit its overrides.");
        Storage.Require(!Document.IsDirty && !Document.ExternalChange, $"Save {Document.Table!.Name} before editing the {lens} profile: its overrides record the saved shared values.");
        try
        {
            var write = ProfileEditing.Apply(ProfileProject!.Invoke()!, Document.Table!, lens!, edits, lensReason?.Text ?? "");
            if (!write.IsEmpty) { profileUndo.Add(write); profileRedo.Clear(); if (profileUndo.Count > 200) profileUndo.RemoveAt(0); }
            AfterProfileWrite(write);
        }
        catch { RecomputeProfileOverlays(); throw; }
    }
    private void AfterProfileWrite(ProfileWrite write)
    {
        RecomputeProfileOverlays();
        foreach (var row in RealizedRowViews()) row.RefreshValues();
        UpdateNote();
        if (!write.IsEmpty) ProfileWritten?.Invoke(this, write);
    }
    private IEnumerable<RowView> RealizedRowViews() =>
        new[] { TableGrid, FrozenGrid }.SelectMany(g => RealizedRows(g)).Select(r => r.DataContext).OfType<RowView>().Where(r => !r.IsPlaceholder);

    private bool UndoProfile()
    {
        if (lens == null) return false;
        if (profileUndo.Count == 0) { error(new InvalidDataException($"No {lens} override edits to undo in this view. Switch to Shared source to undo shared edits.")); return true; }
        var write = profileUndo[^1];
        try { ProfileEditing.Undo(write); profileUndo.RemoveAt(profileUndo.Count - 1); profileRedo.Add(write); AfterProfileWrite(write); }
        catch (Exception e) { profileUndo.Clear(); profileRedo.Clear(); error(e); RecomputeProfileOverlays(); }
        return true;
    }
    private bool RedoProfile()
    {
        if (lens == null) return false;
        if (profileRedo.Count == 0) return true;
        var write = profileRedo[^1];
        // Redo puts back what the write left: undoing the undo.
        var inverse = new ProfileWrite(write.Profile, [.. write.Files.Select(f => new ProfileFileChange(f.Path, f.After, f.Before))]);
        try { ProfileEditing.Undo(inverse); profileRedo.RemoveAt(profileRedo.Count - 1); profileUndo.Add(write); AfterProfileWrite(write); }
        catch (Exception e) { profileUndo.Clear(); profileRedo.Clear(); error(e); RecomputeProfileOverlays(); }
        return true;
    }

    /// <summary>Row and column structure belongs to shared source; a profile can only change cells and leave last rows out.</summary>
    private void RequireSharedView(string action) =>
        Storage.Require(lens == null, $"Switch to Shared source to {action}. In the {lens} view you change cells, or leave rows out from the row menu.");

    /// <summary>Why a cell cannot be edited in the profile view, or null.</summary>
    private string? LensEditBlock(int row, int col)
    {
        if (lens == null || Document.Table is not { } table) return null;
        if (row < 0) return $"Rows are added in Shared source. Switch views to add one.";
        if (lensOverlay == null) return overlayProblem ?? $"The {lens} profile does not load.";
        if (lensOverlay.Removed(row) is { } removed) return removed.Layer == lens ? $"{lens} leaves this row out. Restore it from the row menu to edit it." : $"{removed.Layer} leaves this row out of {lens}.";
        if (lensOverlay.Own(row, table.Columns[col]) is { Editable: false } locked) return locked.Locked;
        return null;
    }

    /// <summary>Hover text for a cell's profile state.</summary>
    internal string? ProfileTip(int row, int col)
    {
        if (Document.Table is not { } table || row < 0 || row >= table.Records.Count || col < 0 || col >= table.Columns.Length) return null;
        var column = table.Columns[col]; var shared = table.Cell(row, column);
        string Rel(string path) => ProfileProject?.Invoke() is { } p ? Storage.Relative(p.Root, path) : path;
        if (lens != null && lensOverlay != null)
        {
            if (lensOverlay.Removed(row) is { } removed) return $"Left out of {lens} by {removed.Layer}: {removed.Reason}\n{Rel(removed.RuleFile)}";
            if (lensOverlay.Cell(row, column) is not { } state) return null;
            var lines = new List<string>();
            for (var s = state; s != null; s = s.Below)
                lines.Add($"{s.Layer}: '{s.Value}' — {s.Reason}{(s.Stale ? $"\n  ⚠ stale: expects '{s.Expect}', finds '{s.Input}'" : "")}{(s.Locked != null ? "\n  🔒 " + s.Locked : "")}\n  {Rel(s.RuleFile)}");
            lines.Add($"shared: '{shared}'");
            return string.Join("\n", lines);
        }
        if (lens == null)
        {
            if (!markedCells.ContainsKey((row, column)) && !markedRows.ContainsKey(row)) return null;
            var lines = new List<string>();
            foreach (var (id, overlay) in markOverlays)
            {
                if (overlay.Removed(row) is { } removed && removed.Layer == id) lines.Add($"{id}: leaves this row out — {removed.Reason}");
                else if (overlay.Own(row, column) is { } state) lines.Add($"{id}: '{state.Value}' — {state.Reason}{(state.Stale ? " ⚠ stale" : "")}");
            }
            lines.Add("Right-click → View as profile to edit them.");
            return lines.Count == 0 ? null : $"shared: '{shared}'\n" + string.Join("\n", lines);
        }
        return null;
    }

    /// <summary>Row menu entries for the profile view (revert, accept, leave out, restore), or for jumping into a profile's view.</summary>
    private Control[] ProfileMenuItems(DataGrid grid)
    {
        if (lensPicker == null || Document.Table is not { IsCatalog: false } table || Document.PendingSource) return [];
        MenuItem Item(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, _) => { try { activeGrid = grid; action(); } catch (Exception ex) { error(ex); } };
            return item;
        }
        var cells = selectedCells.Where(c => c.Row >= 0 && c.Row < table.Records.Count && c.Col >= 0 && c.Col < table.Columns.Length).ToArray();
        var rows = SelectedRowsForCommands().Where(r => r >= 0 && r < table.Records.Count).ToArray();
        if (lens == null)
        {
            var project = ProfileProject?.Invoke(); if (project == null) return [];
            var touching = project.Profiles.Where(p => cells.Any(c => markedCells.TryGetValue((c.Row, table.Columns[c.Col]), out var marks) && marks.Contains(ColorOf(p))) || rows.Any(r => markedRows.TryGetValue(r, out var marks) && marks.Contains(ColorOf(p)))).ToArray();
            var view = new MenuItem { Header = "View as profile" };
            view.ItemsSource = project.Profiles.Select(p => Item((touching.Contains(p) ? "● " : "") + p, () => SetProfileLens(p))).ToArray();
            return [view, new Separator()];
        }
        if (lensOverlay == null) return [Item("Back to Shared source", () => SetProfileLens(null)), new Separator()];
        var overlay = lensOverlay; var profile = lens;
        var own = cells.Where(c => overlay.Own(c.Row, table.Columns[c.Col]) is { Editable: true }).ToArray();
        var stale = own.Where(c => overlay.Own(c.Row, table.Columns[c.Col])!.Stale).ToArray();
        var parent = overlay.Chain.Layers.Count > 1 ? overlay.Chain.Layers[^2].Id : "shared source";
        var removable = rows.Where(r => overlay.Removed(r) == null).ToArray();
        var restorable = rows.Where(r => overlay.Removed(r) is { Editable: true } s && s.Layer == profile).ToArray();
        string Count(int n, string one) => n == 1 ? one : $"{n} {one}s";
        var files = cells.Select(c => overlay.Cell(c.Row, table.Columns[c.Col])?.RuleFile).Concat(rows.Select(r => overlay.Removed(r)?.RuleFile)).OfType<string>().Distinct().ToArray();
        var items = new List<Control>
        {
            Item($"Revert {Count(own.Length, "cell")} to {parent}", () => WriteProfile([.. own.Select(c => (ProfileEdit)new RevertProfileCell(c.Row, table.Columns[c.Col]))]), own.Length > 0),
            Item($"Accept {(parent == "shared source" ? "the shared" : parent + "'s")} value under {Count(stale.Length, "stale change")}", () => WriteProfile([.. stale.Select(c => (ProfileEdit)new AcceptSharedValue(c.Row, table.Columns[c.Col]))]), stale.Length > 0),
            Item($"Leave {Count(removable.Length, "row")} out of {profile}", () => WriteProfile([.. removable.Select(r => (ProfileEdit)new RemoveProfileRow(r))]), removable.Length > 0),
            Item($"Restore {Count(restorable.Length, "row")} in {profile}", () => WriteProfile([.. restorable.Select(r => (ProfileEdit)new RestoreProfileRow(r))]), restorable.Length > 0),
        };
        if (files.Length > 0) items.Add(Item(files.Length == 1 ? $"Open {Path.GetFileName(files[0])}" : $"Open the {files.Length} override files", () => { foreach (var f in files) OpenFileRequested?.Invoke(f); }));
        items.Add(Item("Back to Shared source", () => SetProfileLens(null)));
        items.Add(new Separator());
        return [.. items];
    }

    /// <summary>Corner marks and row strikes a shared-view cell draws for the profiles that change it.</summary>
    private sealed class ProfileMarks : Control
    {
        public IReadOnlyList<Color>? Marks { get; private set; }
        public IReadOnlyList<Color>? Strikes { get; private set; }
        public ProfileMarks() { IsHitTestVisible = false; }
        public void Show(IReadOnlyList<Color>? marks, IReadOnlyList<Color>? strikes)
        {
            if (ReferenceEquals(marks, Marks) && ReferenceEquals(strikes, Strikes)) return;
            Marks = marks; Strikes = strikes; IsVisible = marks != null || strikes != null; InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            if (Marks != null)
                for (int i = 0; i < Marks.Count && i < 4; i++)
                {
                    double x = i * 7;
                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open()) { g.BeginFigure(new Point(x, 0), true); g.LineTo(new Point(x + 7, 0)); g.LineTo(new Point(x, 7)); g.EndFigure(true); }
                    context.DrawGeometry(new SolidColorBrush(Marks[i]), null, geometry);
                }
            if (Strikes != null)
                for (int i = 0; i < Strikes.Count && i < 3; i++)
                {
                    double y = size.Height / 2 + (i - (Strikes.Count - 1) / 2.0) * 3;
                    context.DrawLine(new Pen(new SolidColorBrush(ProfileColors.WithAlpha(Strikes[i], 0xB0)), 1.5), new Point(0, y), new Point(size.Width, y));
                }
        }
    }
}

/// <summary>
/// Loaded profile chains by project and profile, reused while none of the files they were read from changed (size and
/// write time), so the grid can recompute a profile's overlay on every edit without re-reading its rule files.
/// </summary>
internal static class ProfileChainCache
{
    private sealed record Entry(ProfileChain Chain, (string Path, long Length, long Ticks)[] Stamps);
    private static readonly Dictionary<(string Root, string Profile), Entry> entries = [];
    private static (string, long, long) Stamp(string path) { var info = new FileInfo(path); return info.Exists ? (path, info.Length, info.LastWriteTimeUtc.Ticks) : (path, -1, 0); }
    public static ProfileChain Get(ModProject project, string profile)
    {
        var key = (project.Root, profile);
        if (entries.TryGetValue(key, out var entry) && entry.Stamps.All(s => Stamp(s.Path) == s)) return entry.Chain;
        var chain = ProfileChain.Load(project.Root, profile);
        var paths = chain.Layers.SelectMany(l => l.RuleFiles.Select(f => f.Path).Prepend(l.SettingsFile));
        entries[key] = new(chain, [.. paths.Select(Stamp)]);
        return chain;
    }
    public static ProfileChain? TryGet(ModProject project, string profile)
    {
        try { return Get(project, profile); } catch (Exception) { return null; }
    }
}
