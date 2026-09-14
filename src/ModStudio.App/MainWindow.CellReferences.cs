using Avalonia.Controls;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private int cellReferenceGeneration;
    private CancellationTokenSource? cellReferenceCancellation;
    private CellReferenceIndex referenceIndex = new();
    private (ModProject? Project, int Workspace) referenceContext;
    private readonly Dictionary<Document, (int Revision, TableData Origin, TableData Table)> referenceSnapshots = [];

    private CellReferenceIndex CurrentReferenceIndex()
    {
        if (referenceContext != (project, workspaceRevision))
        {
            referenceIndex = new(); referenceContext = (project, workspaceRevision); referenceSnapshots.Clear();
        }
        return referenceIndex;
    }

    private Dictionary<string, TableData> ReferenceBuffers(IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var panes = tabs.Select(t => t.Content).OfType<EditorPane>().ToArray();
        foreach (var stale in referenceSnapshots.Keys.Where(d => !panes.Any(p => p.Document == d)).ToArray()) referenceSnapshots.Remove(stale);
        var result = new Dictionary<string, TableData>(StringComparer.OrdinalIgnoreCase);
        foreach (var pane in panes)
        {
            var doc = pane.Document;
            if (doc.PendingSource || doc.Table is not { } table || !(wanted.Contains(table.Name) || table.IsCatalog && wanted.Contains("strings"))) continue;
            if (!referenceSnapshots.TryGetValue(doc, out var snapshot) || snapshot.Revision != doc.Revision || snapshot.Origin != table)
                referenceSnapshots[doc] = snapshot = (doc.Revision, table, new((System.Text.Json.Nodes.JsonObject)table.Schema.DeepClone(), (System.Text.Json.Nodes.JsonArray)table.Records.DeepClone()));
            result[doc.FilePath] = snapshot.Table;
        }
        return result;
    }

    private string? ReferenceDocumentName(Document document)
    {
        if (project == null) return null;
        string name = Path.GetFileNameWithoutExtension(document.FilePath);
        if (string.Equals(document.FilePath, Semantics.TableFile(project, name), StringComparison.OrdinalIgnoreCase)) return name;
        return string.Equals(Path.GetDirectoryName(document.FilePath), Path.Combine(project.Root, "source", "strings"), StringComparison.OrdinalIgnoreCase) ? "strings" : null;
    }

    private HashSet<string> PendingReferenceTables() => tabs.Select(t => t.Content).OfType<EditorPane>()
        .Where(p => p.Document.PendingSource).Select(p => ReferenceDocumentName(p.Document)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task NavigateCellReferenceAsync(EditorPane source, int row, string column, Control anchor)
    {
        int generation = ++cellReferenceGeneration;
        cellReferenceCancellation?.Cancel();
        cellReferenceCancellation = null;
        if (project is not { } selectedProject || source.Document.Table is not { } table ||
            source.Document.PendingSource || row < 0 || row >= table.Records.Count ||
            CellReferences.Rule(table.Name, column) is not { } rule) return;
        var value = table.Cell(row, column);
        if (!CellReferences.CanNavigate(rule, value)) return;
        using var cancellation = new CancellationTokenSource(); cellReferenceCancellation = cancellation;
        int revision = source.Document.Revision, workspace = workspaceRevision;
        var targetNames = rule.ReferenceTables!.ToHashSet(StringComparer.OrdinalIgnoreCase);
        EditorPane[] Targets() => tabs.Select(t => t.Content).OfType<EditorPane>()
            .Where(p => ReferenceDocumentName(p.Document) is { } name && targetNames.Contains(name)).ToArray();
        var targets = Targets().Select(p => (Pane: p, Revision: p.Document.Revision, Table: p.Document.Table, Pending: p.Document.PendingSource)).ToArray();
        bool Current() => generation == cellReferenceGeneration && project == selectedProject && Active == source &&
            tabs.Any(t => t.Content == source) && source.Document.Revision == revision && source.Document.Table == table && !source.Document.PendingSource && workspaceRevision == workspace &&
            Targets().SequenceEqual(targets.Select(t => t.Pane)) && targets.All(t => t.Pane.Document.Revision == t.Revision && t.Pane.Document.Table == t.Table && t.Pane.Document.PendingSource == t.Pending);
        try
        {
            Status.Text = $"Finding reference ‘{value}’…";
            var index = CurrentReferenceIndex();
            var buffers = ReferenceBuffers(targetNames);
            var pending = PendingReferenceTables();
            var fields = table.Columns.ToDictionary(c => c, c => table.Cell(row, c));
            var result = await Task.Run(() => index.Resolve(selectedProject, rule, value, buffers, pending, cancellation.Token, fields), cancellation.Token);
            if (!Current()) { if (generation == cellReferenceGeneration) Status.Text = "Reference source changed. Click its arrow again."; return; }

            async Task OpenHit(ReferenceHit hit)
            {
                if (!Current()) { Status.Text = "Reference source changed. Click its arrow again."; return; }
                try
                {
                    var destination = await OpenDocumentAsync(hit.File);
                    if (generation != cellReferenceGeneration || project != selectedProject || destination == null) return;
                    if (source.Document.Revision != revision || workspaceRevision != workspace || destination.Document.PendingSource || destination.Document.Table is not { } target ||
                        hit.FindRow(target) < 0)
                    { Status.Text = "Reference changed while opening. Return to the source and click its arrow again."; return; }
                    int targetRow = hit.FindRow(target);
                    destination.JumpToReference(targetRow, hit.Column);
                    Status.Text = $"Opened {target.Name} · {hit.Column} = {hit.Value} · row {targetRow}";
                }
                catch (Exception ex) { ShowError(ex); }
            }

            if (result.Hits.Count == 1 && result.Issues.Count == 0) { await OpenHit(result.Hits[0]); return; }
            var items = new List<Control>();
            foreach (var hit in result.Hits)
            {
                var name = targetNames.FirstOrDefault(n => Semantics.TableFile(selectedProject, n) == hit.File) ?? Path.GetFileName(hit.File);
                var item = new MenuItem { Header = $"{name} · {hit.Column} = {hit.Value} · row {hit.Row}" };
                item.Click += async (_, _) => await OpenHit(hit); items.Add(item);
            }
            if (items.Count == 0) items.Add(new MenuItem { Header = $"No record matches ‘{value}’", IsEnabled = false });
            if (result.Issues.Count > 0)
            {
                items.Add(new Separator());
                foreach (var issue in result.Issues) items.Add(new MenuItem { Header = issue, IsEnabled = false });
            }
            Status.Text = result.Hits.Count == 0 ? $"No record matches ‘{value}’ in the available reference tables." : $"Choose the record referenced by ‘{value}’.";
            var menu = new ContextMenu { ItemsSource = items };
            anchor.ContextMenu = menu;
            menu.Closed += (_, _) => { if (anchor.ContextMenu == menu) anchor.ContextMenu = null; };
            menu.Open(anchor);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current()) ShowError(ex); }
        finally { if (cellReferenceCancellation == cancellation) cellReferenceCancellation = null; }
    }
}
