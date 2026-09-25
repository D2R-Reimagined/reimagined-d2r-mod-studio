using System.Globalization;
using System.Text.Json.Nodes;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// How the game is set up when it loads a layout: which profile files supply the $variables, and the reference screen the
/// layout's anchors are fractions of. HD layouts are authored against a 2160 pixel tall screen whose width follows the
/// aspect ratio; the legacy SD layouts against 800 × 600.
/// </summary>
public sealed record UiLayoutMode(string Id, string Label, bool Controller, bool LargeFont, bool Legacy)
{
    public static readonly UiLayoutMode PcHd = new("pc-hd", "PC · HD", false, false, false);
    public static readonly UiLayoutMode PcLargeFont = new("pc-lv", "PC · Large font", false, true, false);
    public static readonly UiLayoutMode ControllerHd = new("controller-hd", "Controller · HD", true, false, false);
    public static readonly UiLayoutMode ControllerLargeFont = new("controller-lv", "Controller · Large font", true, true, false);
    public static readonly UiLayoutMode Sd = new("sd", "Legacy (SD)", false, false, true);
    public static readonly UiLayoutMode[] All = [PcHd, PcLargeFont, ControllerHd, ControllerLargeFont, Sd];

    /// <summary>The mode a layout file is written for: controller/ files for a controller, *hd.json for HD, anything else legacy.</summary>
    public static UiLayoutMode For(string relative)
    {
        relative = relative.Replace('\\', '/').ToLowerInvariant();
        if (!Path.GetFileNameWithoutExtension(relative).EndsWith("hd", StringComparison.Ordinal) && !relative.StartsWith("controller/", StringComparison.Ordinal)) return Sd;
        return relative.StartsWith("controller/", StringComparison.Ordinal) ? ControllerHd : PcHd;
    }

    /// <summary>The profile files, parent first. Each file's own basedOn is followed as well.</summary>
    public string[] ProfileFiles => Legacy ? ["_profilesd.json"]
        : Controller ? (LargeFont ? ["_profilelv.json", "controller/_profilehd.json", "controller/_profilelv.json"] : ["controller/_profilehd.json"])
        : LargeFont ? ["_profilelv.json"] : ["_profilehd.json"];
}

/// <summary>A screen shape to preview against. HD layouts keep their height (2160) and widen or narrow with the aspect ratio.</summary>
public sealed record UiScreen(string Label, double Aspect)
{
    public static readonly UiScreen[] All = [new("16:9", 16.0 / 9), new("16:10", 16.0 / 10), new("21:9", 21.0 / 9), new("32:9", 32.0 / 9), new("4:3", 4.0 / 3)];
    public (double Width, double Height) Size(UiLayoutMode mode) => mode.Legacy ? (Math.Round(600 * Aspect), 600) : (Math.Round(2160 * Aspect), 2160);
}

/// <summary>A layout or profile file as it was read: where it came from and its parsed text.</summary>
public sealed record UiLayoutFile(string Relative, string Path, string Text, UiJsonNode Root, bool IsDocument, bool InProject)
{
    public string Origin => IsDocument ? "this file" : InProject ? "project" : "game data";
    public string Name => System.IO.Path.GetFileName(Relative);
}

/// <summary>A profile $variable: the value as written and the file whose definition wins.</summary>
public sealed record UiProfileEntry(string Name, UiJsonNode Raw, UiLayoutFile File);

/// <summary>A field of a widget: the value as written, where it was written, and the value with every $variable substituted.</summary>
public sealed class UiField
{
    public required string Key { get; init; }
    public required UiJsonNode Raw { get; init; }
    public required UiLayoutFile File { get; init; }
    public JsonNode? Value { get; internal set; }
    /// <summary>The $variable the whole value refers to, when it is one; the profile entry that supplied it.</summary>
    public string? Variable { get; internal set; }
    public UiProfileEntry? VariableEntry { get; internal set; }
    public bool InDocument => File.IsDocument;
    public string RawText => File.Text[Raw.Start..Raw.End];
}

/// <summary>An axis-aligned rectangle in reference screen pixels.</summary>
public readonly record struct UiRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && y >= Y && x <= Right && y <= Bottom;
    public UiRect Union(UiRect other)
    {
        if (Width <= 0 && Height <= 0) return other;
        if (other.Width <= 0 && other.Height <= 0) return this;
        double x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        return new(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}

/// <summary>
/// One widget of a resolved layout. Its fields are merged from every file that defines it (a basedOn parent first, this file
/// last); <see cref="Definitions"/> lists those definitions. Geometry is in reference screen pixels.
/// </summary>
public sealed class UiWidget
{
    public required string Type { get; init; }
    public required string Name { get; init; }
    public UiWidget? Parent { get; init; }
    public List<UiWidget> Children { get; } = [];
    public Dictionary<string, UiField> Fields { get; } = new(StringComparer.Ordinal);
    public List<(UiLayoutFile File, UiJsonNode Node)> Definitions { get; } = [];
    /// <summary>The widget's name among its siblings: its name, and which occurrence of that name it is.</summary>
    public (string Name, int Occurrence) Key { get; internal set; }
    public int Index { get; internal set; }
    public int Depth => Parent == null ? 0 : Parent.Depth + 1;
    public string Path => Parent == null ? Name : Parent.Path + "/" + Name;
    /// <summary>Whether this file defines the widget (true for the root); false when it only comes from a basedOn parent.</summary>
    public bool InDocument => Parent == null || Definitions.Any(d => d.File.IsDocument);
    /// <summary>Whether a basedOn parent defines it too, so this file can override but not remove it.</summary>
    public bool Inherited => Definitions.Any(d => !d.File.IsDocument);

    /// <summary>The widget's position: its parent's, plus its anchor on the parent and its rect offset, in the parent's scale.</summary>
    public double X { get; internal set; }
    public double Y { get; internal set; }
    /// <summary>Its own size before scaling (rect width and height, or the parent's with fitToParent).</summary>
    public double LocalWidth { get; internal set; }
    public double LocalHeight { get; internal set; }
    public double Scale { get; internal set; } = 1;
    public bool HasSize => LocalWidth > 0 || LocalHeight > 0;
    public UiRect Bounds => new(X, Y, LocalWidth * Scale, LocalHeight * Scale);
    /// <summary>The point the anchor names on the parent, which the rect offset is measured from.</summary>
    public (double X, double Y) AnchorPoint { get; internal set; }
    /// <summary>The scale the widget's rect offset is multiplied by (its parent's).</summary>
    public double ParentScale => Parent?.Scale ?? 1;

    public JsonNode? Value(string key) => Fields.TryGetValue(key, out var f) ? f.Value : null;
    public string? String(string key) => Value(key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public double? Number(string key) => UiLayout.Number(Value(key));
    public bool Flag(string key) => Value(key) is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public IEnumerable<UiWidget> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
    public override string ToString() => $"{Type} {Path}";
}

/// <summary>A resolved layout, ready to draw: its widgets in draw order, the profile it was resolved with and what went wrong.</summary>
public sealed class UiScene
{
    public required UiWidget Root { get; init; }
    public required UiLayoutMode Mode { get; init; }
    public required UiScreen Screen { get; init; }
    public required UiLayoutFile Document { get; init; }
    public required IReadOnlyList<UiLayoutFile> Files { get; init; }
    public required IReadOnlyDictionary<string, UiProfileEntry> Profile { get; init; }
    public required IReadOnlyList<UiLayoutFile> ProfileFiles { get; init; }
    public List<string> Issues { get; } = [];
    public double Width { get; init; }
    public double Height { get; init; }
    /// <summary>Every widget, parents before children (draw order).</summary>
    public List<UiWidget> Widgets { get; } = [];
    public UiWidget? Find(string path) => Widgets.FirstOrDefault(w => w.Path == path);
    /// <summary>The widget whose own definition in the document starts at or contains the offset (the innermost one).</summary>
    public UiWidget? AtOffset(int offset) => Widgets.Where(w => w.Definitions.Any(d => d.File.IsDocument && d.Node.Start <= offset && offset < d.Node.End))
        .OrderByDescending(w => w.Depth).FirstOrDefault();
}

/// <summary>Where a layout file and its relatives (basedOn parents, profiles) are read from.</summary>
public sealed class UiLayoutSources
{
    private readonly ModProject? project;
    private readonly IReadOnlyList<string> gameData;
    private readonly string? documentRoot;
    private readonly Func<string, string?>? openText;
    private readonly Dictionary<string, UiLayoutFile?> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="documentPath">The layout being edited; its layouts folder is searched first.</param>
    /// <param name="openText">Unsaved text of a file open in another tab, by full path; null when it is not open.</param>
    public UiLayoutSources(ModProject? project, IReadOnlyList<string> gameData, string documentPath, Func<string, string?>? openText = null)
    {
        this.project = project; this.gameData = gameData; this.openText = openText;
        documentRoot = LayoutsRoot(documentPath);
    }

    /// <summary>The global/ui/layouts folder a file sits in (the folder named layouts nearest above it); null when it is not in one.</summary>
    public static string? LayoutsRoot(string path)
    {
        for (var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)); dir != null; dir = System.IO.Path.GetDirectoryName(dir))
            if (System.IO.Path.GetFileName(dir).Equals("layouts", StringComparison.OrdinalIgnoreCase)) return dir;
        return null;
    }

    /// <summary>The file's path inside its layouts folder ("controller/x.json"); its file name when it is not in one.</summary>
    public static string RelativeName(string path) => LayoutsRoot(path) is { } root ? Relative(root, System.IO.Path.GetFullPath(path)).ToLowerInvariant() : System.IO.Path.GetFileName(path).ToLowerInvariant();

    /// <summary>Whether a file is a UI layout the designer can open: a .json file under global/ui/layouts.</summary>
    public static bool IsLayout(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && LayoutsRoot(path) is { } root &&
        System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(root) ?? "").Equals("ui", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a layout file by its path inside the layouts folder, the way the game would find it: this project, then the game data.</summary>
    public UiLayoutFile? Open(string relative)
    {
        relative = relative.Replace('\\', '/').TrimStart('/');
        if (cache.TryGetValue(relative, out var cached)) return cached;
        string? path = null; bool inProject = false;
        if (documentRoot != null && HdAppearance.Locate(documentRoot, relative) is { } local) { path = local; inProject = project != null && Contains(project.Root, local); }
        if (path == null && project != null && HdAppearance.Locate(project.Root, "data/global/ui/layouts/" + relative) is { } own) { path = own; inProject = true; }
        if (path == null) foreach (var folder in gameData) if (HdAppearance.Locate(folder, "data/global/ui/layouts/" + relative) is { } found) { path = found; break; }
        UiLayoutFile? file = null;
        if (path != null)
        {
            var text = openText?.Invoke(System.IO.Path.GetFullPath(path)) ?? File.ReadAllText(path, Utf8);
            file = new(relative.ToLowerInvariant(), path, text, UiJsonParser.Parse(text), false, inProject);
        }
        return cache[relative] = file;
    }

    /// <summary>Every layout file name available (project and game data), for reference layers and pickers.</summary>
    public IReadOnlyList<string> Available()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string? root)
        {
            if (root == null || !Directory.Exists(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                if (!System.IO.Path.GetFileName(file).StartsWith('_')) names.Add(Relative(root, file).ToLowerInvariant());
        }
        Scan(documentRoot);
        if (project != null) Scan(HdAppearance.LocateFolder(project.Root, "data/global/ui/layouts"));
        foreach (var folder in gameData) Scan(HdAppearance.LocateFolder(folder, "data/global/ui/layouts"));
        return [.. names];
    }
}

/// <summary>
/// Resolves D2R UI layout files the way the game assembles them. A layout names its type, name, fields and children; basedOn
/// names a parent layout whose widgets it merges into by name (fields override one by one, children the file does not list
/// are kept, new ones are added). "$Name" values are read from the profile files (_profilehd.json and the files it is based
/// on, per <see cref="UiLayoutMode"/>), and a widget sits at its parent's position plus its anchor (a fraction of the parent's
/// size) plus its rect offset, all scaled by the parent's scale.
/// </summary>
public static class UiLayout
{
    public static UiScene Resolve(UiLayoutSources sources, string documentPath, string documentText, UiLayoutMode mode, UiScreen screen, Func<string, (double Width, double Height)?>? spriteSize = null)
    {
        var relative = UiLayoutSources.RelativeName(documentPath);
        var document = new UiLayoutFile(relative, documentPath, documentText, UiJsonParser.Parse(documentText), true, false);
        var issues = new List<string>();
        var (profile, profileFiles) = Profile(sources, mode, issues);
        var files = new List<UiLayoutFile>();
        var chain = Chain(sources, document, files, issues);
        var root = Merge(chain, null, issues);
        if (root == null) throw new InvalidDataException("The layout has no root widget object.");
        var (width, height) = screen.Size(mode);
        var scene = new UiScene { Root = root, Mode = mode, Screen = screen, Document = document, Files = files, Profile = profile, ProfileFiles = profileFiles, Width = width, Height = height };
        scene.Issues.AddRange(issues);
        foreach (var widget in Flatten(root)) { widget.Index = scene.Widgets.Count; scene.Widgets.Add(widget); ResolveFields(widget, profile, scene.Issues); }
        Place(root, 0, 0, width, height, 1, spriteSize);
        return scene;
    }

    // ── Files and profiles ────────────────────────────────────────────────────────────────────────

    /// <summary>The document and its basedOn parents, parent first.</summary>
    private static List<UiLayoutFile> Chain(UiLayoutSources sources, UiLayoutFile document, List<UiLayoutFile> files, List<string> issues)
    {
        var chain = new List<UiLayoutFile> { document };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { document.Relative };
        for (var current = document; current.Root["basedOn"]?.String is { Length: > 0 } parent;)
        {
            if (!seen.Add(parent.ToLowerInvariant())) { issues.Add($"basedOn loops back to {parent}."); break; }
            var next = sources.Open(parent);
            if (next == null) { issues.Add($"basedOn names {parent}, which is not in the project or the game data."); break; }
            chain.Insert(0, next); files.Add(next); current = next;
        }
        files.Insert(0, document);
        return chain;
    }

    /// <summary>A profile name as basedOn writes it ("HD", "ControllerHD") to its file.</summary>
    private static string ProfileFile(string name) =>
        name.StartsWith("Controller", StringComparison.OrdinalIgnoreCase) ? $"controller/_profile{name[10..].ToLowerInvariant()}.json" : $"_profile{name.ToLowerInvariant()}.json";

    private static (Dictionary<string, UiProfileEntry>, List<UiLayoutFile>) Profile(UiLayoutSources sources, UiLayoutMode mode, List<string> issues)
    {
        var ordered = new List<UiLayoutFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Load(string relative, bool required)
        {
            if (!seen.Add(relative)) return;
            UiLayoutFile? file;
            try { file = sources.Open(relative); }
            catch (UiJsonException e) { issues.Add($"{relative}: {e.Message}"); return; }
            if (file == null) { if (required) issues.Add($"Profile {relative} is not in the project or the game data, so $variables from it cannot be shown. Choose your extracted game data folder."); return; }
            if (file.Root["basedOn"]?.String is { Length: > 0 } parent) Load(ProfileFile(parent), true);
            ordered.Add(file);
        }
        foreach (var relative in mode.ProfileFiles) Load(relative, true);
        var entries = new Dictionary<string, UiProfileEntry>(StringComparer.Ordinal);
        foreach (var file in ordered)
            foreach (var member in file.Root.Members.Where(m => m.Key != "basedOn"))
                entries[member.Key] = new(member.Key, member.Value, file);
        return (entries, ordered);
    }

    // ── Merging ───────────────────────────────────────────────────────────────────────────────────

    private sealed class Definition(UiLayoutFile file, UiJsonNode node)
    {
        public UiLayoutFile File { get; } = file;
        public UiJsonNode Node { get; } = node;
    }

    /// <summary>Merges one widget's definitions (parent file first) and, by name, their children.</summary>
    private static UiWidget? Merge(List<UiLayoutFile> chain, UiWidget? parent, List<string> issues) =>
        MergeDefinitions(chain.Select(f => new Definition(f, f.Root)).ToList(), parent, issues);

    private static UiWidget? MergeDefinitions(List<Definition> definitions, UiWidget? parent, List<string> issues)
    {
        if (definitions.Count == 0) return null;
        string type = definitions.Select(d => d.Node["type"]?.String).LastOrDefault(t => t != null) ?? "Widget";
        string name = definitions.Select(d => d.Node["name"]?.String).LastOrDefault(t => t != null) ?? "(unnamed)";
        var widget = new UiWidget { Type = type, Name = name, Parent = parent };
        foreach (var d in definitions)
        {
            widget.Definitions.Add((d.File, d.Node));
            if (d.Node["fields"] is { IsObject: true } fields)
                foreach (var member in fields.Members) widget.Fields[member.Key] = new UiField { Key = member.Key, Raw = member.Value, File = d.File };
        }
        // Children: the first definition's order, later files' same-named children merged in, new ones appended.
        var groups = new List<(string Name, List<Definition> Parts)>();
        foreach (var d in definitions)
        {
            if (d.Node["children"] is not { IsArray: true } children) continue;
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in children.Items.Where(c => c.IsObject))
            {
                var childName = child["name"]?.String ?? "";
                int occurrence = used[childName] = used.GetValueOrDefault(childName) + 1;
                int found = -1, seen = 0;
                for (int k = 0; k < groups.Count; k++)
                    if (groups[k].Name == childName && ++seen == occurrence) { found = k; break; }
                if (found >= 0) groups[found].Parts.Add(new(d.File, child));
                else groups.Add((childName, [new(d.File, child)]));
            }
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (childName, parts) in groups)
        {
            var child = MergeDefinitions(parts, widget, issues);
            if (child == null) continue;
            int occurrence = counts.GetValueOrDefault(childName);
            counts[childName] = occurrence + 1;
            child.Key = (childName, occurrence);
            widget.Children.Add(child);
        }
        return widget;
    }

    private static IEnumerable<UiWidget> Flatten(UiWidget widget) => widget.Children.SelectMany(Flatten).Prepend(widget);

    // ── Values ────────────────────────────────────────────────────────────────────────────────────

    private static void ResolveFields(UiWidget widget, IReadOnlyDictionary<string, UiProfileEntry> profile, List<string> issues)
    {
        foreach (var field in widget.Fields.Values)
        {
            if (field.Raw.String is { } s && s.StartsWith('$'))
            {
                field.Variable = s;
                field.VariableEntry = profile.GetValueOrDefault(s[1..]);
            }
            field.Value = Substitute(field.Raw.ToJsonNode(), profile, issues, $"{widget.Path}.{field.Key}", 0);
        }
    }

    /// <summary>A value with every "$Name" (at any depth) replaced by the profile's value.</summary>
    public static JsonNode? Substitute(JsonNode? value, IReadOnlyDictionary<string, UiProfileEntry> profile, List<string>? issues, string where, int depth = 0)
    {
        if (depth > 32) { issues?.Add($"{where}: $variables nest too deeply (a loop?)."); return value; }
        switch (value)
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith('$') && s.Length > 1:
                if (profile.TryGetValue(s[1..], out var entry)) return Substitute(entry.Raw.ToJsonNode(), profile, issues, where, depth + 1);
                issues?.Add($"{where}: {s} is not defined by the profile.");
                return null;
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToArray()) o[key] = Substitute(o[key]?.DeepClone(), profile, issues, where, depth + 1);
                return o;
            case JsonArray a:
                for (int k = 0; k < a.Count; k++) a[k] = Substitute(a[k]?.DeepClone(), profile, issues, where, depth + 1);
                return a;
            default: return value;
        }
    }

    public static double? Number(JsonNode? node) => node is JsonValue v ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : null : null;
    public static double Member(JsonNode? obj, string key, double fallback = 0) => Number((obj as JsonObject)?[key]) ?? fallback;

    /// <summary>A colour as the files write it: { "r", "g", "b", "a" } in 0–255, or [r, g, b, a] in 0–1 (or 0–255 when any part is above 1).</summary>
    public static (byte R, byte G, byte B, byte A)? Color(JsonNode? node)
    {
        static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        if (node is JsonObject o && (o.ContainsKey("r") || o.ContainsKey("g") || o.ContainsKey("b")))
            return (Clamp(Member(o, "r")), Clamp(Member(o, "g")), Clamp(Member(o, "b")), Clamp(Member(o, "a", 255)));
        if (node is JsonArray a && a.Count >= 3)
        {
            var parts = a.Select(Number).ToArray();
            if (parts.Any(p => p == null)) return null;
            bool bytes = parts.Any(p => p > 1);
            double F(int k, double fallback) => k < parts.Length ? parts[k]!.Value * (bytes ? 1 : 255) : fallback;
            return (Clamp(F(0, 0)), Clamp(F(1, 0)), Clamp(F(2, 0)), Clamp(F(3, 255)));
        }
        return null;
    }

    // ── Geometry ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sprite a widget draws as its body, by type: most draw "filename"; inventory slots their background.</summary>
    public static string? SpriteField(UiWidget widget) =>
        widget.Type is "InventorySlotWidget" && widget.String("backgroundFilename") is { Length: > 0 } ? "backgroundFilename"
        : widget.Type is "TextBoxWidget" or "InventoryGridWidget" or "RectangleWidget" ? null
        : widget.String("filename") is { Length: > 0 } s && !s.Contains('%') && !s.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "filename" : null;

    /// <summary>The sprite frame a widget shows at rest.</summary>
    public static int SpriteFrame(UiWidget widget)
    {
        foreach (var key in new[] { "frame", "normalFrame", "untoggledFrame", "inactiveNormalFrame" })
            if (widget.Number(key) is { } n) return Math.Max(0, (int)n);
        return 0;
    }

    private static void Place(UiWidget widget, double parentX, double parentY, double parentWidth, double parentHeight, double parentScale, Func<string, (double Width, double Height)?>? spriteSize)
    {
        var rect = widget.Value("rect") as JsonObject;
        var anchor = widget.Value("anchor") as JsonObject;
        double ax = Member(anchor, "x"), ay = Member(anchor, "y");
        var anchorPoint = (X: parentX + ax * parentWidth * parentScale, Y: parentY + ay * parentHeight * parentScale);
        double scale = parentScale * Member(rect, "scale", 1);
        double width = Member(rect, "width"), height = Member(rect, "height");
        double x, y;
        if (widget.Flag("fitToParent") && widget.Parent != null)
        {
            (x, y) = (parentX, parentY); width = parentWidth; height = parentHeight; scale = parentScale;
            anchorPoint = (parentX, parentY);
        }
        else { x = anchorPoint.X + Member(rect, "x") * parentScale; y = anchorPoint.Y + Member(rect, "y") * parentScale; }
        // A widget with no size of its own is as big as the picture it draws; the root fills the screen.
        if (widget.Parent == null && width <= 0 && height <= 0) (width, height) = (parentWidth, parentHeight);
        else if (width <= 0 && height <= 0 && SpriteField(widget) is { } field && spriteSize?.Invoke(widget.String(field)!) is { } sprite)
            (width, height) = (sprite.Width, sprite.Height);
        if (widget.Type == "InventoryGridWidget" && width <= 0 && widget.Value("cellCount") is JsonObject count && widget.Value("cellSize") is JsonObject cell)
            (width, height) = (Member(count, "x") * Member(cell, "x"), Member(count, "y") * Member(cell, "y"));
        widget.X = x; widget.Y = y; widget.LocalWidth = width; widget.LocalHeight = height; widget.Scale = scale; widget.AnchorPoint = anchorPoint;
        foreach (var child in widget.Children) Place(child, x, y, width, height, scale, spriteSize);
    }
}

/// <summary>
/// Plans text edits for a layout in the designer. Everything is written into the document being edited: a field set on a
/// widget that only a basedOn parent defines gets an override entry for that widget in this file (created with its type and
/// name, and its parents', as the merge needs), so parent files and the profile are never changed from here.
/// </summary>
public static class UiLayoutEdit
{
    /// <summary>Sets one field of a widget to a literal (JSON text).</summary>
    public static string SetField(string text, UiWidget widget, string key, string literal)
    {
        (text, var node) = EnsureWidget(text, widget);
        if (node["fields"] is { IsObject: true } fields) return UiJsonEditor.Apply(text, UiJsonEditor.SetMember(text, fields, key, literal));
        return UiJsonEditor.Apply(text, UiJsonEditor.SetMember(text, node, "fields", FieldsBlock(text, node, [(key, literal)])));
    }

    /// <summary>Removes a field this file sets on a widget (a parent file's or the default value then applies).</summary>
    public static string RemoveField(string text, UiWidget widget, string key)
    {
        var node = Locate(UiJsonParser.Parse(text), widget) ?? throw new InvalidDataException($"{widget.Path} is not written in this file.");
        var fields = node["fields"];
        Require(fields is { IsObject: true } && fields.Member(key) != null, $"This file does not set {key} on {widget.Name}.");
        return UiJsonEditor.Apply(text, [UiJsonEditor.RemoveMember(text, fields!, key)]);
    }

    /// <summary>
    /// Sets members of an object field (rect x and y, anchor) keeping the rest. A literal object written in this file is
    /// patched member by member; otherwise (a $variable, a parent file's value, no value) the field is written here in full,
    /// starting from its current resolved value.
    /// </summary>
    public static string SetMembers(string text, UiWidget widget, string key, IReadOnlyList<(string Member, double Value)> changes)
    {
        var doc = UiJsonParser.Parse(text);
        var node = Locate(doc, widget);
        if (node?["fields"]?[key] is { IsObject: true } own)
        {
            var edits = new List<UiTextEdit>();
            // Several new members go in one pass: each insertion is planned against the same parse, so they are written together.
            var missing = changes.Where(c => own.Member(c.Member) == null).ToList();
            foreach (var (member, value) in changes.Where(c => own.Member(c.Member) != null)) edits.Add(UiJsonEditor.Replace(own[member]!, UiJsonEditor.Number(value)));
            text = UiJsonEditor.Apply(text, edits);
            foreach (var (member, value) in missing)
            {
                var again = Locate(UiJsonParser.Parse(text), widget)!["fields"]![key]!;
                text = UiJsonEditor.Apply(text, UiJsonEditor.SetMember(text, again, member, UiJsonEditor.Number(value)));
            }
            return text;
        }
        var current = widget.Value(key) as JsonObject ?? [];
        var merged = new JsonObject();
        foreach (var name in RectOrder.Concat(current.Select(p => p.Key)).Distinct())
        {
            if (changes.FirstOrDefault(c => c.Member == name) is { Member: not null } change) merged[name] = change.Value;
            else if (current[name] is { } existing) merged[name] = existing.DeepClone();
        }
        return SetField(text, widget, key, UiJsonEditor.Literal(merged));
    }
    private static readonly string[] RectOrder = ["x", "y", "width", "height", "scale"];

    /// <summary>Adds a new child widget (type, name and fields as literals) at the end of a widget's children.</summary>
    public static string AddChild(string text, UiWidget parent, string type, string name, IReadOnlyList<(string Key, string Literal)> fields)
    {
        (text, var node) = EnsureWidget(text, parent);
        return AppendChild(text, node, type, name, fields);
    }

    /// <summary>
    /// Copies a widget as a new sibling right after it, under a new name. A widget written only in this file is copied as
    /// written (comments and children included); one that inherits fields gets every merged field written out.
    /// </summary>
    public static string Duplicate(string text, UiWidget widget, string newName)
    {
        Require(widget.Parent != null, "The root widget cannot be duplicated.");
        var doc = UiJsonParser.Parse(text);
        var own = Locate(doc, widget);
        if (own != null && !widget.Inherited && Locate(doc, widget.Parent!)?["children"] is { IsArray: true } siblings)
        {
            int index = siblings.Items.IndexOf(own);
            var indent = UiJsonEditor.IndentOf(text, own.Start);
            var copy = UiJsonEditor.Reindent(text, own, indent);
            // Rename the copy: its own "name" member is the first one written at its top level.
            var parsed = UiJsonParser.Parse(copy);
            if (parsed.Member("name") is { } nameMember) copy = UiJsonEditor.Apply(copy, [UiJsonEditor.Replace(nameMember.Value, UiJsonEditor.Quote(newName))]);
            return UiJsonEditor.Apply(text, UiJsonEditor.InsertItemAfter(text, siblings, index, copy));
        }
        var fields = widget.Fields.Values.Select(f => (f.Key, f.RawText)).ToList();
        (text, var parentNode) = EnsureWidget(text, widget.Parent!);
        return AppendChild(text, parentNode, widget.Type, newName, fields);
    }

    /// <summary>Removes a widget this file adds (one a parent file also defines can only be overridden, not removed).</summary>
    public static string Delete(string text, UiWidget widget)
    {
        Require(widget.Parent != null, "The root widget cannot be deleted.");
        Require(!widget.Inherited, $"{widget.Name} comes from {widget.Definitions[0].File.Name}; this file can override its fields but cannot remove it.");
        var doc = UiJsonParser.Parse(text);
        var node = Locate(doc, widget) ?? throw new InvalidDataException($"{widget.Path} is not written in this file.");
        var siblings = Locate(doc, widget.Parent!)!["children"]!;
        return UiJsonEditor.Apply(text, [UiJsonEditor.RemoveItem(text, siblings, siblings.Items.IndexOf(node))]);
    }

    /// <summary>Moves a widget one place earlier (drawn below) or later (drawn above) among the siblings written in this file.</summary>
    public static string Move(string text, UiWidget widget, int direction)
    {
        Require(widget.Parent != null, "The root widget cannot move.");
        var doc = UiJsonParser.Parse(text);
        var node = Locate(doc, widget) ?? throw new InvalidDataException($"{widget.Name} is not written in this file, so its order comes from {widget.Definitions[0].File.Name}.");
        var siblings = Locate(doc, widget.Parent!)!["children"]!;
        int index = siblings.Items.IndexOf(node), other = index + Math.Sign(direction);
        Require(other >= 0 && other < siblings.Items.Count, direction < 0 ? $"{widget.Name} is already drawn first." : $"{widget.Name} is already drawn last.");
        return UiJsonEditor.Apply(text, UiJsonEditor.SwapItems(text, siblings, index, other));
    }

    /// <summary>Renames a widget this file adds.</summary>
    public static string Rename(string text, UiWidget widget, string newName)
    {
        Require(newName.Length > 0, "A widget needs a name.");
        Require(!widget.Inherited || widget.Parent == null, $"{widget.Name} merges with the widget of that name in {widget.Definitions[0].File.Name}; renaming it here would add a new widget instead.");
        var node = Locate(UiJsonParser.Parse(text), widget) ?? throw new InvalidDataException($"{widget.Path} is not written in this file.");
        return node.Member("name") is { } member ? UiJsonEditor.Apply(text, [UiJsonEditor.Replace(member.Value, UiJsonEditor.Quote(newName))])
            : UiJsonEditor.Apply(text, UiJsonEditor.SetMember(text, node, "name", UiJsonEditor.Quote(newName)));
    }

    /// <summary>A name no sibling of the parent uses yet: base, base_2, base_3…</summary>
    public static string FreeName(UiWidget parent, string name)
    {
        var taken = parent.Children.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;
        var stem = System.Text.RegularExpressions.Regex.Replace(name, "_\\d+$", "");
        for (int k = 2; ; k++) if (!taken.Contains($"{stem}_{k}")) return $"{stem}_{k}";
    }

    /// <summary>The object that defines a widget in this file, found by its path of names from the root; null when the file does not list it.</summary>
    public static UiJsonNode? Locate(UiJsonNode root, UiWidget widget)
    {
        var path = new List<UiWidget>();
        for (var w = widget; w.Parent != null; w = w.Parent) path.Insert(0, w);
        var node = root;
        foreach (var step in path)
        {
            if (node["children"] is not { IsArray: true } children) return null;
            node = children.Items.Where(c => c.IsObject && (c["name"]?.String ?? "") == step.Key.Name).Skip(step.Key.Occurrence).FirstOrDefault();
            if (node == null) return null;
        }
        return node;
    }

    /// <summary>Makes sure this file lists the widget (and its parents), adding type-and-name entries that merge with a parent file's widgets.</summary>
    private static (string Text, UiJsonNode Node) EnsureWidget(string text, UiWidget widget)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            var root = UiJsonParser.Parse(text);
            var path = new List<UiWidget>();
            for (var w = widget; w.Parent != null; w = w.Parent) path.Insert(0, w);
            var node = root; bool changed = false;
            foreach (var step in path)
            {
                if (node["children"] is not { IsArray: true } children)
                {
                    text = AppendChild(text, node, step.Type, step.Name, []);
                    changed = true; break;
                }
                var match = children.Items.Where(c => c.IsObject && (c["name"]?.String ?? "") == step.Key.Name).Skip(step.Key.Occurrence).FirstOrDefault();
                if (match == null)
                {
                    // Earlier same-named siblings must be listed first so the occurrence lines up.
                    text = UiJsonEditor.Apply(text, UiJsonEditor.AppendItem(text, children, StubBlock(text, children, step)));
                    changed = true; break;
                }
                node = match;
            }
            if (!changed) return (text, node);
        }
        throw new InvalidDataException($"Could not add an entry for {widget.Path} to this file.");
    }

    /// <summary>An entry for a widget laid out like the array it goes into: on its own lines when the siblings are.</summary>
    private static string StubBlock(string text, UiJsonNode children, UiWidget widget) => ChildBlock(text, children, widget.Type, widget.Name, []);

    private static string AppendChild(string text, UiJsonNode node, string type, string name, IReadOnlyList<(string Key, string Literal)> fields)
    {
        if (node["children"] is { IsArray: true } children)
            return UiJsonEditor.Apply(text, UiJsonEditor.AppendItem(text, children, ChildBlock(text, children, type, name, fields)));
        var indent = MemberIndent(text, node); var nl = UiJsonEditor.NewLine(text); var unit = Unit(indent);
        var block = "[" + nl + indent + unit + Block(type, name, fields, indent + unit, unit, nl) + "," + nl + indent + "]";
        return UiJsonEditor.Apply(text, UiJsonEditor.SetMember(text, node, "children", block));
    }

    private static string ChildBlock(string text, UiJsonNode children, string type, string name, IReadOnlyList<(string Key, string Literal)> fields)
    {
        var nl = UiJsonEditor.NewLine(text);
        bool multiline = text.IndexOf('\n', children.Start, children.End - children.Start) >= 0;
        if (!multiline) return fields.Count == 0 ? "{ \"type\": " + UiJsonEditor.Quote(type) + ", \"name\": " + UiJsonEditor.Quote(name) + " }"
            : "{ \"type\": " + UiJsonEditor.Quote(type) + ", \"name\": " + UiJsonEditor.Quote(name) + ", \"fields\": { " + string.Join(", ", fields.Select(f => UiJsonEditor.Quote(f.Key) + ": " + f.Literal)) + " } }";
        var indent = children.Items.Count > 0 ? UiJsonEditor.IndentOf(text, children.Items[^1].Start) : UiJsonEditor.IndentOf(text, children.End - 1) + "    ";
        return Block(type, name, fields, indent, Unit(indent), nl);
    }

    /// <summary>A widget object whose first line is already indented by the caller; the rest is indented from <paramref name="indent"/>.</summary>
    private static string Block(string type, string name, IReadOnlyList<(string Key, string Literal)> fields, string indent, string unit, string nl)
    {
        var sb = new System.Text.StringBuilder("{").Append(nl);
        sb.Append(indent + unit).Append("\"type\": ").Append(UiJsonEditor.Quote(type)).Append(", \"name\": ").Append(UiJsonEditor.Quote(name)).Append(',').Append(nl);
        if (fields.Count > 0)
        {
            sb.Append(indent + unit).Append("\"fields\": {").Append(nl);
            foreach (var (key, literal) in fields) sb.Append(indent + unit + unit).Append(UiJsonEditor.Quote(key)).Append(": ").Append(Reflow(literal, indent + unit + unit, nl)).Append(',').Append(nl);
            sb.Append(indent + unit).Append("},").Append(nl);
        }
        return sb.Append(indent).Append('}').ToString();
    }

    /// <summary>A copied multi-line literal, its later lines re-indented under the key it now follows.</summary>
    private static string Reflow(string literal, string indent, string nl)
    {
        if (!literal.Contains('\n')) return literal;
        var lines = literal.Replace("\r\n", "\n").Split('\n');
        // The closing line sits one level shallower than the members, so it sets the common indentation.
        int shallow = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.TakeWhile(char.IsWhiteSpace).Count()).DefaultIfEmpty(0).Min();
        return lines[0] + string.Concat(lines.Skip(1).Select(l => nl + indent + (l.Length >= shallow ? l[shallow..] : l.TrimStart())));
    }

    /// <summary>The fields object for a widget that has none yet, laid out like the widget.</summary>
    private static string FieldsBlock(string text, UiJsonNode node, IReadOnlyList<(string Key, string Literal)> fields)
    {
        bool multiline = text.IndexOf('\n', node.Start, node.End - node.Start) >= 0;
        if (!multiline) return "{ " + string.Join(", ", fields.Select(f => UiJsonEditor.Quote(f.Key) + ": " + f.Literal)) + " }";
        var indent = MemberIndent(text, node); var nl = UiJsonEditor.NewLine(text); var unit = Unit(indent);
        return "{" + string.Concat(fields.Select(f => nl + indent + unit + UiJsonEditor.Quote(f.Key) + ": " + f.Literal + ",")) + nl + indent + "}";
    }

    /// <summary>The indentation of an object's members (one level in from the object's own line when it has none).</summary>
    private static string MemberIndent(string text, UiJsonNode node) =>
        node.Members.Count > 0 && UiJsonEditor.IndentOf(text, node.Members[^1].Start) is var i && text.IndexOf('\n', node.Start, node.Members[^1].Start - node.Start) >= 0 ? i
        : UiJsonEditor.IndentOf(text, node.Start) + Unit(UiJsonEditor.IndentOf(text, node.Start));

    private static string Unit(string indent) => indent.Contains('\t') ? "\t" : "    ";
}
