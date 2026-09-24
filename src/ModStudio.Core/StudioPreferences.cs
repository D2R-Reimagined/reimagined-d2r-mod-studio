using System.Text.Json;

namespace ModStudio.Core;

public sealed class StudioPreferences
{
    public ExternalEditorSettings ExternalEditor { get; set; } = new();
    public string? LastProject { get; set; }
    public string RowEditorSearch { get; set; } = "";
    /// <summary>Whether hovering a unique/set item row shows its rendered tooltip; the Item Preview tab is unaffected.</summary>
    public bool ItemHoverCards { get; set; } = true;
    /// <summary>Whether table headers lead with the spreadsheet letter of the column (A … Z, AA …); off by default.</summary>
    public bool ColumnLetters { get; set; }
    /// <summary>Font of the source (JSON/text) editor; empty means the built-in monospace stack.</summary>
    /// <summary>The pal.pl2 the colour-transform picker reads; empty until the user points at their extracted game data.</summary>
    public string PalettePl2 { get; set; } = "";
    /// <summary>The user's extracted game data (the folder holding hd/ and global/), read for base-game HD files the project does not override.</summary>
    public string GameDataFolder { get; set; } = "";
    public string SourceFontFamily { get; set; } = "";
    public double SourceFontSize { get; set; } = 13;
    /// <summary>Font of the table grid; empty means the app font.</summary>
    public string TableFontFamily { get; set; } = "";
    public double TableFontSize { get; set; } = 15;
    public List<string> SettingsIntroduced { get; set; } = [];
    /// <summary>The editor view last chosen for each file ("table", "source", "visual" or "preview"), by normalized full path.</summary>
    public Dictionary<string, string> EditorViews { get; set; } = [];
    /// <summary>The view remembered for a file; null when none was chosen (the file opens in its default view).</summary>
    public string? ViewFor(string file) => EditorViews.GetValueOrDefault(ViewKey(file));
    /// <summary>Remembers a file's view; the default "table" is forgotten rather than stored.</summary>
    public void RememberView(string file, string mode)
    {
        if (mode == "table") EditorViews.Remove(ViewKey(file)); else EditorViews[ViewKey(file)] = mode;
    }
    private static string ViewKey(string file) { var full = Path.GetFullPath(file); return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full; }
    /// <summary>Tool panels the user minimized to their strip: "explorer", "inspector", "bottom".</summary>
    public List<string> HiddenPanels { get; set; } = [];
    /// <summary>Last size in pixels the user dragged each tool panel to (width for "explorer"/"inspector", height for "bottom").</summary>
    public Dictionary<string, double> PanelSizes { get; set; } = [];
    /// <summary>Find in files window size and last scope, so the popup reopens the way it was left.</summary>
    public double FindInFilesWidth { get; set; }
    public double FindInFilesHeight { get; set; }
    public string FindInFilesScope { get; set; } = "source";
    /// <summary>Where Import mod creates new projects; distinct from the game's mods folder, which only receives deployed builds.</summary>
    public string? ProjectsFolder { get; set; }
    public static string DefaultProjectsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "D2R Mod Studio");
    public string ResolvedProjectsFolder => string.IsNullOrWhiteSpace(ProjectsFolder) ? DefaultProjectsFolder : ProjectsFolder;
    public static string DefaultFile => Environment.GetEnvironmentVariable("MOD_STUDIO_PREFERENCES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReimaginedD2RModStudio", "preferences.json");
    public static StudioPreferences Load(string file) => File.Exists(file)
        ? JsonSerializer.Deserialize<StudioPreferences>(File.ReadAllText(file)) ?? new()
        : new();
    public bool HasIntroduced(string root) => SettingsIntroduced.Any(p => string.Equals(Normalize(p), Normalize(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    public void Remember(string root, string file)
    {
        LastProject = Normalize(root);
        Save(file);
    }
    public void MarkIntroduced(string root, string file)
    {
        if (!HasIntroduced(root)) SettingsIntroduced.Add(Normalize(root));
        Save(file);
    }
    private static string Normalize(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public void Save(string file) => Storage.AtomicWrite(file, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
}
