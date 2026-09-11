using ModStudio.Core;

namespace ModStudio.App;

public sealed class ProjectEntry(string name, string path, bool directory, string? schemaPath = null)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool Directory { get; } = directory;
    public string? SchemaPath { get; } = schemaPath;
    public List<ProjectEntry> Children { get; } = [];
    public bool IsExpanded { get; set; }

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
                var match = new ProjectEntry(node.Name, node.Path, node.Directory, node.SchemaPath) { IsExpanded = true };
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
            var records = System.IO.Path.Combine(dir, "records.json");
            var schema = System.IO.Path.Combine(dir, "schema.json");
            var parent = System.IO.Directory.GetParent(dir);
            var logicalTable = parent?.Name is "tables" or "strings" && parent.Parent?.Name == "source" && File.Exists(records) && File.Exists(schema);
            var item = new ProjectEntry(System.IO.Path.GetFileName(dir), logicalTable ? records : dir, !logicalTable, logicalTable ? schema : null);
            foreach (var sub in System.IO.Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
            {
                if (System.IO.Path.GetFileName(sub) is ".git" or ".studio" or "build" or "node_modules" or ".idea") continue;
                item.Children.Add(Make(sub));
            }
            foreach (var file in System.IO.Directory.GetFiles(dir).Order(StringComparer.Ordinal))
            {
                Storage.NoLinks(file);
                if (logicalTable && (file == records || file == schema)) continue;
                item.Children.Add(new(System.IO.Path.GetFileName(file), file, false));
            }
            return item;
        }
        return Make(root).Children;
    }
}
