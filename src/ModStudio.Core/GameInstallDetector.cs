using System.Text.RegularExpressions;

namespace ModStudio.Core;

public sealed record GameInstallation(string Directory, string Source, string[] Executables);

/// <summary>
/// Finds Diablo II: Resurrected installations without a full-disk search: the Windows uninstall registry,
/// the Battle.net and Steam default folders on every fixed drive, configured Steam libraries, and
/// Linux Steam/Wine defaults. Same locations the Reimagined launcher checks first.
/// </summary>
public static class GameInstallDetector
{
    public const string GameFolder = "Diablo II Resurrected";
    public static IReadOnlyList<GameInstallation> Detect(Func<string, string?>? readRegistry = null)
    {
        var found = new List<GameInstallation>();
        void Add(string? directory, string source)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                var executables = RunSettings.DetectExecutables(full);
                if (executables.Length == 0 || found.Any(f => string.Equals(f.Directory, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return;
                found.Add(new(full, source, executables));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        foreach (var (directory, source) in RegistryCandidates(readRegistry)) Add(directory, source);
        foreach (var root in ProgramRoots())
        {
            Add(Path.Combine(root, GameFolder), "Battle.net default folder");
            Add(Path.Combine(root, "Steam", "steamapps", "common", GameFolder), "Steam default library");
        }
        foreach (var library in SteamLibraries(readRegistry)) Add(Path.Combine(library, "steamapps", "common", GameFolder), "Steam library");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home)) Add(Path.Combine(home, ".wine", "drive_c", "Program Files (x86)", GameFolder), "Wine default prefix");
        }
        return found;
    }
    private static IEnumerable<(string Directory, string Source)> RegistryCandidates(Func<string, string?>? read)
    {
        read ??= ReadRegistry;
        // Battle.net writes the uninstall entry; the Steam entry is per app id (2181070 = D2R).
        foreach (var key in new[] {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Diablo II Resurrected|InstallLocation",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Diablo II Resurrected|InstallLocation",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 2181070|InstallLocation",
            @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Diablo II Resurrected|InstallLocation" })
        {
            var value = read(key);
            if (!string.IsNullOrWhiteSpace(value)) yield return (value.Trim().Trim('"'), key.Contains("Steam App") ? "Steam (registry)" : "Battle.net (registry)");
        }
    }
    private static IEnumerable<string> ProgramRoots()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var names = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path)) names.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
        }
        if (names.Count == 0) names.AddRange(["Program Files (x86)", "Program Files"]);
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var drive in drives)
        {
            bool ready; try { ready = drive.DriveType == DriveType.Fixed && drive.IsReady; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (!ready) continue;
            foreach (var name in names.Distinct()) yield return Path.Combine(drive.RootDirectory.FullName, name);
            // Common custom layouts beside the system folders.
            yield return Path.Combine(drive.RootDirectory.FullName, "Games");
            yield return drive.RootDirectory.FullName;
        }
    }
    /// <summary>Steam installation roots plus every library listed in libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> SteamLibraries(Func<string, string?>? readRegistry = null)
    {
        readRegistry ??= ReadRegistry;
        var roots = new List<string>();
        void AddRoot(string? root) { if (!string.IsNullOrWhiteSpace(root) && !roots.Contains(root.Trim(), StringComparer.OrdinalIgnoreCase)) roots.Add(root.Trim()); }
        // Honor injected registry readers on every platform, as RegistryCandidates does.
        // The default reader returns null outside Windows.
        AddRoot(readRegistry(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam|SteamPath")?.Replace('/', Path.DirectorySeparatorChar));
        AddRoot(readRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam|InstallPath"));
        if (OperatingSystem.IsWindows())
        {
            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(x86)) AddRoot(Path.Combine(x86, "Steam"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                AddRoot(Path.Combine(home, ".local", "share", "Steam"));
                AddRoot(Path.Combine(home, ".steam", "steam"));
                AddRoot(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"));
                AddRoot(Path.Combine(home, "Library", "Application Support", "Steam"));
            }
        }
        foreach (var root in roots.ToArray()) foreach (var library in ParseLibraryFolders(Path.Combine(root, "steamapps", "libraryfolders.vdf"))) AddRoot(library);
        return roots;
    }
    /// <summary>Reads the "path" entries from Steam's libraryfolders.vdf (escaped backslashes are unescaped).</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            return Regex.Matches(File.ReadAllText(file), "\"path\"\\s+\"(?<path>[^\"]+)\"").Select(m => m.Groups["path"].Value.Replace("\\\\", "\\")).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    /// <summary>Reads a registry string as "HIVE\Sub\Key|ValueName". Only available on Windows; other platforms return null.</summary>
    public static string? ReadRegistry(string keyAndValue)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var separator = keyAndValue.LastIndexOf('|'); if (separator < 0) return null;
        try { return Microsoft.Win32.Registry.GetValue(keyAndValue[..separator], keyAndValue[(separator + 1)..], null) as string; }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
