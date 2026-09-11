using System.Text.Json;

namespace ModStudio.Core;

public sealed class StudioPreferences
{
    public string? LastProject { get; set; }
    public string RowEditorSearch { get; set; } = "";
    public List<string> SettingsIntroduced { get; set; } = [];
    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReimaginedD2RModStudio", "preferences.json");
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
