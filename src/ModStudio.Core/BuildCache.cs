using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record CachedTable(string Key, string Name, int[] StringIds, List<BuildFile> Files);
public record CachedSemantics(string Key, List<Diagnostic> Diagnostics, int Rules, int Cells);
/// <summary>A file's hash, remembered together with the size and write time it was computed for.</summary>
public record FileFingerprint(long Length, long Written, string Sha256);
internal static class BuildCache
{
    public static FileStream Lock(ModProject project, FileShare share = FileShare.None)
    {
        Directory.CreateDirectory(project.Cache);
        return new FileStream(Inside(project.Cache, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
    }
    // Hashing every source and output file on each build meant reading the whole data folder several times per Play.
    // Files whose size and write time are unchanged keep their hash; a file written within the last two seconds is hashed
    // again because its timestamp cannot yet prove that a later write did not land inside the same tick.
    private static readonly ConcurrentDictionary<string, FileFingerprint> fingerprints = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly TimeSpan RecentWrite = TimeSpan.FromSeconds(2);
    public static string FingerprintFile(ModProject project) => Inside(project.Cache, "hashes.json");
    public static string FileHash(string file) => FileHash(new FileInfo(file));
    /// <summary>Hash of a file whose size and write time were already read (a FileInfo from an enumeration costs no extra syscall).</summary>
    public static string FileHash(FileInfo info)
    {
        Require(info.Exists, "File not found: " + info.FullName);
        var written = info.LastWriteTimeUtc.Ticks;
        if (fingerprints.TryGetValue(info.FullName, out var known) && known.Length == info.Length && known.Written == written && DateTime.UtcNow - info.LastWriteTimeUtc > RecentWrite) return known.Sha256;
        string hash; using (var stream = info.OpenRead()) hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        fingerprints[info.FullName] = new(info.Length, written, hash); return hash;
    }
    /// <summary>Records the hash of a file this process just wrote, so the next check does not read it back.</summary>
    private static void Remember(string file, string hash)
    {
        var info = new FileInfo(file); if (info.Exists) fingerprints[info.FullName] = new(info.Length, info.LastWriteTimeUtc.Ticks, hash);
    }
    public static void LoadFingerprints(ModProject project)
    {
        var file = FingerprintFile(project); if (!File.Exists(file)) return;
        try { foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, FileFingerprint>>(File.ReadAllText(file), Pretty) ?? []) fingerprints.TryAdd(pair.Key, pair.Value); }
        catch (JsonException) { /* A damaged cache only costs one full hashing pass. */ }
    }
    /// <summary>Persists fingerprints under the project and the given extra roots (a deployment folder), so the first build after a restart is fast too.</summary>
    public static void SaveFingerprints(ModProject project, params string[] roots)
    {
        Dictionary<string, FileFingerprint> known = [];
        try { if (File.Exists(FingerprintFile(project))) known = JsonSerializer.Deserialize<Dictionary<string, FileFingerprint>>(File.ReadAllText(FingerprintFile(project)), Pretty) ?? []; }
        catch (JsonException) { }
        var keep = roots.Append(project.Root).ToArray();
        foreach (var pair in fingerprints) if (keep.Any(r => Contains(r, pair.Key))) known[pair.Key] = pair.Value;
        AtomicWrite(FingerprintFile(project), JsonSerializer.SerializeToUtf8Bytes(known, Compact));
    }
    /// <summary>Copies unless the target already has this hash. Pass the target's enumerated FileInfo when known, or null for an absent target.</summary>
    public static bool CopyChanged(string source, string target, string hash, FileInfo? existing)
    {
        if (existing != null && FileHash(existing) == hash) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true); Remember(target, hash); return true;
    }
    public static bool CopyChanged(string source, string target, string hash) => CopyChanged(source, target, hash, File.Exists(target) ? new FileInfo(target) : null);
    public static bool WriteChanged(string target, byte[] bytes)
    {
        var hash = Hash(bytes);
        if (File.Exists(target) && FileHash(target) == hash) return false;
        AtomicWrite(target, bytes); Remember(target, hash); return true;
    }
    public static void RemoveOldBuilds(ModProject project, Action<string>? progress)
    {
        var builds = Inside(project.Cache, "builds");
        foreach (var directory in Directory.GetDirectories(builds))
        {
            // Only remove the old GUID build layout with a matching Studio manifest.
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            var manifest = Inside(directory, "build.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                var build = JsonSerializer.Deserialize<BuildResult>(File.ReadAllText(manifest), Pretty);
                if (build?.ProjectId != project.Id) continue;
                _ = Files(directory).Count(); // Reject links anywhere in this generated cache before deletion.
                Directory.Delete(directory, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { progress?.Invoke("Could not remove old build cache: " + directory + " · " + ex.Message); }
        }
    }
}
