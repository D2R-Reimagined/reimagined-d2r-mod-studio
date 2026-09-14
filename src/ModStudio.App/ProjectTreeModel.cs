using System.ComponentModel;
using ModStudio.Core;

namespace ModStudio.App;

public sealed class ProjectEntry(string name, string path, bool directory, bool isTable = false) : INotifyPropertyChanged
{
    private bool isExpanded;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool Directory { get; } = directory;
    /// <summary>A table or string catalog source file (source/tables or source/strings), shown with the table icon and opened in the table editor.</summary>
    public bool IsTable { get; } = isTable;
    public List<ProjectEntry> Children { get; } = [];
    public bool IsExpanded { get => isExpanded; set { if (isExpanded == value) return; isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }

    public static List<ProjectEntry> Filter(IEnumerable<ProjectEntry> entries, string query)
    {
        query = query.Trim().Replace('\\', '/');
        List<ProjectEntry> Visit(IEnumerable<ProjectEntry> nodes, string parent)
        {
            var result = new List<ProjectEntry>();
            foreach (var node in nodes)
            {
                var path = parent + node.Name;
                var children = Visit(node.Children, path + "/");
                if (!path.Contains(query, StringComparison.OrdinalIgnoreCase) && children.Count == 0) continue;
                var match = new ProjectEntry(node.Name, node.Path, node.Directory, node.IsTable) { IsExpanded = true };
                match.Children.AddRange(children); result.Add(match);
            }
            return result;
        }
        return Visit(entries, "");
    }

    public static List<ProjectEntry> Read(string root)
    {
        ProjectEntry Make(string dir)
        {
            Storage.NoLinks(dir);
            var name = System.IO.Path.GetFileName(dir); var parent = System.IO.Directory.GetParent(dir);
            bool tableBank = name is "tables" or "strings" && parent?.Name == "source" && parent.Parent?.FullName == System.IO.Path.GetFullPath(root);
            var item = new ProjectEntry(name, dir, true);
            foreach (var sub in System.IO.Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
            {
                if (System.IO.Path.GetFileName(sub) is ".git" or ".studio" or "build" or "node_modules" or ".idea") continue;
                item.Children.Add(Make(sub));
            }
            foreach (var file in System.IO.Directory.GetFiles(dir).Order(StringComparer.Ordinal))
            {
                Storage.NoLinks(file);
                item.Children.Add(new(System.IO.Path.GetFileName(file), file, false, tableBank && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
            }
            return item;
        }
        return Make(root).Children;
    }
}
