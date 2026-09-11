using System.Security.Cryptography;
using System.Text.Json;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public record CachedTable(string Key, string Name, int[] StringIds, List<BuildFile> Files);
internal static class BuildCache
{
    public static FileStream Lock(ModProject project, FileShare share = FileShare.None)
    {
        Directory.CreateDirectory(project.Cache);
        return new FileStream(Inside(project.Cache, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
    }
    public static string FileHash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static bool CopyChanged(string source, string target, string hash)
    {
        if (File.Exists(target) && FileHash(target) == hash) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true); return true;
    }
    public static bool WriteChanged(string target, byte[] bytes)
    {
        if (File.Exists(target) && FileHash(target) == Hash(bytes)) return false;
        AtomicWrite(target, bytes); return true;
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
