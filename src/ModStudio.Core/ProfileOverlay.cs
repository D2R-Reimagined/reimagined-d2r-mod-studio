using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// A cell one layer of a profile chain changes. <see cref="Input"/> is the value that layer sees (shared source, or what its
/// parents made of it); a change whose <see cref="Expect"/> no longer matches it is stale and blocks the build.
/// </summary>
public sealed record ProfileCellState(string Layer, string Input, string Expect, string Value, string Reason, string RuleFile, string? Locked, string[]? Targets, ProfileCellState? Below)
{
    public bool Stale => Input != Expect;
    /// <summary>Studio can rewrite this change: its file is not generated and it applies to every bank.</summary>
    public bool Editable => Locked == null;
}

/// <summary>A row one layer of a profile chain leaves out of its build.</summary>
public sealed record ProfileRowState(string Layer, string Reason, string RuleFile, string? Locked, string[]? Targets)
{
    public bool Editable => Locked == null;
}

/// <summary>
/// What a profile does to one table, cell by cell, for the editor's profile view: computed from the shared table (open
/// buffer or disk) and the profile chain. Problems the build would reject are collected as <see cref="Issues"/>, not thrown,
/// so the view can still show everything else.
/// </summary>
public sealed class ProfileOverlay
{
    private readonly Dictionary<(int Row, string Column), ProfileCellState> cells = [];
    private readonly Dictionary<int, ProfileRowState> removed = [];
    private readonly HashSet<int> changedRows = [];
    public string Profile { get; }
    public ProfileChain Chain { get; }
    public TableData Table { get; }
    public List<string> Issues { get; } = [];
    public int CellCount => cells.Count;
    public int RemovedCount => removed.Count;
    public IReadOnlyDictionary<(int Row, string Column), ProfileCellState> Cells => cells;
    public IReadOnlyDictionary<int, ProfileRowState> RemovedRows => removed;

    public ProfileCellState? Cell(int row, string column) => cells.GetValueOrDefault((row, column));
    public ProfileRowState? Removed(int row) => removed.GetValueOrDefault(row);
    public bool RowChanged(int row) => changedRows.Contains(row);
    public string Effective(int row, string column) => Cell(row, column)?.Value ?? Table.Cell(row, column);
    /// <summary>The value the profile's own layer starts from: shared source with every parent's changes applied.</summary>
    public string Inherited(int row, string column)
    {
        var state = Cell(row, column);
        while (state != null && state.Layer == Profile) state = state.Below;
        return state?.Value ?? Table.Cell(row, column);
    }
    /// <summary>The profile's own change of a cell, when it has one.</summary>
    public ProfileCellState? Own(int row, string column) => Cell(row, column) is { } state && state.Layer == Profile ? state : null;

    private ProfileOverlay(string profile, ProfileChain chain, TableData table) { Profile = profile; Chain = chain; Table = table; }

    public static ProfileOverlay Compute(ModProject project, TableData table, string profile) => Compute(ProfileChain.Load(project.Root, profile), table);
    public static ProfileOverlay Compute(ProfileChain chain, TableData table)
    {
        var overlay = new ProfileOverlay(chain.Id, chain, table);
        Require(!table.IsCatalog, "Catalog profiles use compact/full translation fields.");
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < table.Records.Count; i++) rows.TryAdd(table.Records[i].S("sourceId"), i);
        var columns = table.Columns.ToHashSet(StringComparer.Ordinal);
        int remaining = table.Records.Count;
        foreach (var layer in chain.Layers)
        {
            var occupied = new HashSet<(int, string)>(); var layerRemoved = new List<int>(); var layerChanged = new HashSet<int>();
            foreach (var file in layer.RuleFiles)
                foreach (var rule in file.Rules)
                {
                    if (rule.S("table") != table.Name) continue;
                    var where = $"{layer.Id}/{file.Reference}";
                    try
                    {
                        var reason = rule.S("reason"); Require(reason.Length > 0, $"{where}: override needs a reason.");
                        Require(rows.TryGetValue(rule.S("record"), out var row), $"{where}: unknown override record {rule.S("record")}.");
                        string[]? targets = rule["targets"] is JsonArray list ? [.. list.Select(t => t!.GetValue<string>())] : null;
                        string? locked = file.Generated ? ProfileRules.Generated(file.Root, $"{layer.Id}/{file.Reference}")
                            : targets != null ? $"This override only applies to {string.Join(", ", targets)}. Edit {where} to keep its bank scope." : null;
                        if (ProfileRules.Removes(rule))
                        {
                            Require(!layerRemoved.Contains(row), $"{where}: competing overrides remove the same row.");
                            if (overlay.removed.ContainsKey(row)) { overlay.Issues.Add($"{where}: {rule.S("record")} is already left out by a parent profile."); continue; }
                            layerRemoved.Add(row); overlay.removed[row] = new(layer.Id, reason, file.Path, locked, targets);
                            continue;
                        }
                        Require(rule["changes"] is JsonObject { Count: > 0 }, $"{where}: empty override.");
                        foreach (var change in rule["changes"]!.AsObject())
                        {
                            Require(columns.Contains(change.Key), $"{where}: unknown column {change.Key}.");
                            Require(change.Value?["expect"] is JsonValue e && e.TryGetValue<string>(out _) && change.Value?["value"] is JsonValue v && v.TryGetValue<string>(out _), $"{where}: override needs string expect/value.");
                            Require(occupied.Add((row, change.Key)), $"{where}: competing overrides for {rule.S("record")}/{change.Key}.");
                            var below = overlay.Cell(row, change.Key);
                            var input = below?.Value ?? table.Cell(row, change.Key);
                            overlay.cells[(row, change.Key)] = new(layer.Id, input, change.Value.S("expect"), change.Value.S("value"), reason, file.Path, locked, targets, below);
                            layerChanged.Add(row); overlay.changedRows.Add(row);
                        }
                    }
                    catch (InvalidDataException e) { overlay.Issues.Add(e.Message); }
                }
            if (layerChanged.Overlaps(layerRemoved)) overlay.Issues.Add($"{layer.Id}: a profile both changes and removes the same row.");
            // A layer may only leave out the last rows of what it is given, so every row it keeps stays at its position.
            if (layerRemoved.Count > 0 && layerRemoved.Any(i => i < remaining - layerRemoved.Count))
                overlay.Issues.Add($"{layer.Id}: a profile can only remove rows at the end of {table.Name}; rows it keeps after a removed row would move.");
            remaining -= layerRemoved.Count;
        }
        foreach (var (key, top) in overlay.cells)
            for (var state = top; state != null; state = state.Below)
                if (state.Stale) overlay.Issues.Add($"{state.Layer}: stale override {table.Records[key.Row].S("sourceId")}/{key.Column} expects '{state.Expect}' but finds '{state.Input}'.");
        return overlay;
    }

    /// <summary>Problems other than stale changes: what an edit must not add.</summary>
    public IEnumerable<string> StructuralIssues => Issues.Where(i => !i.Contains(": stale override ", StringComparison.Ordinal));
}
