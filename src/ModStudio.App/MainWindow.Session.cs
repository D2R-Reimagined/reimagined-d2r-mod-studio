using System.Text.Json;
using Avalonia.Controls;
using ModStudio.Core;

namespace ModStudio.App;

public partial class MainWindow
{
    private sealed record OpenFileSession(string[] Files, string? SelectedFile, string? PreviewFile);
    private static string? TabFile(TabItem? tab) => (tab?.Content as EditorPane)?.Document.FilePath ?? tab?.Tag as string;

    private void SaveOpenFiles()
    {
        if (project == null) return;
        var session = new OpenFileSession(tabs.Select(TabFile).OfType<string>().ToArray(),
            TabFile(Documents.SelectedItem as TabItem), TabFile(previewTab));
        Storage.AtomicWrite(System.IO.Path.Combine(project.Cache, "open-files.json"), JsonSerializer.SerializeToUtf8Bytes(session));
    }

    private async Task RestoreOpenFilesAsync()
    {
        var restoringProject = project;
        if (restoringProject == null) return;
        var file = System.IO.Path.Combine(restoringProject.Cache, "open-files.json");
        if (!File.Exists(file)) return;
        try
        {
            var session = JsonSerializer.Deserialize<OpenFileSession>(File.ReadAllText(file));
            if (session?.Files == null) return;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var path in session.Files)
            {
                if (project != restoringProject || closingApproved) return;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                await OpenDocumentAsync(path, string.Equals(path, session.PreviewFile, comparison));
            }
            if (project != restoringProject || closingApproved) return;
            var selected = tabs.FirstOrDefault(t => string.Equals(TabFile(t), session.SelectedFile, comparison));
            if (selected != null) Documents.SelectedItem = selected;
        }
        catch (Exception ex) { ShowError(new IOException("Could not restore open files: " + ex.Message, ex)); }
    }
}
