using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>Bounded file I/O for migration. Reports serial, throttled progress from worker threads.</summary>
internal static class MigrationFiles
{
    public static IEnumerable<string> Enumerate(string root, CancellationToken token, bool skipBuildCache = false)
    {
        NoLinks(root);
        return Walk(root);
        IEnumerable<string> Walk(string directory)
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                Require((entry.Attributes & FileAttributes.ReparsePoint) == 0, $"Linked paths are not supported: {entry.FullName}");
                if (skipBuildCache && Relative(root, entry.FullName) == ".studio/builds") continue;
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                { foreach (var file in Walk(entry.FullName)) yield return file; }
                else yield return entry.FullName;
            }
        }
    }

    public static Dictionary<string, string> Fingerprint(IEnumerable<string> files, string root, string stage,
        CancellationToken token, Action<string>? progress)
    {
        var hashes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        Run(files, root, stage, token, progress, (file, report) =>
        {
            using var stream = File.OpenRead(file);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); report(read); }
            hashes[file] = Convert.ToHexStringLower(hash.GetHashAndReset());
        });
        return hashes.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
    }

    public static void Copy(string source, string target, CancellationToken token, Action<int> report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = File.OpenRead(source);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = input.Read(buffer)) > 0)
        { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); report(read); }
        output.Flush();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
    }

    public static void Verify(Dictionary<string, string> expected, IEnumerable<string> files, string root,
        CancellationToken token, Action<string>? progress, string error)
    {
        var actual = Fingerprint(files, root, "Verifying original files", token, progress);
        Require(actual.Count == expected.Count && expected.All(p => actual.TryGetValue(p.Key, out var hash) && hash == p.Value), error);
    }

    public static void Run(IEnumerable<string> files, string root, string stage, CancellationToken token,
        Action<string>? progress, Action<string, Action<int>> action)
    {
        var watch = Stopwatch.StartNew();
        var list = new List<string>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested(); list.Add(file);
            if (watch.ElapsedMilliseconds >= 250)
            { progress?.Invoke($"{stage} · finding files: {list.Count:N0} · {Relative(root, file)}"); watch.Restart(); }
        }
        var gate = new object();
        int complete = 0, workers = Math.Min(4, Math.Max(2, Environment.ProcessorCount));
        long bytes = 0;
        void Report(string file, int count, bool done = false)
        {
            lock (gate)
            {
                bytes += count; if (done) complete++;
                if (watch.ElapsedMilliseconds < 250 && complete != list.Count) return;
                progress?.Invoke($"{stage} · {complete:N0}/{list.Count:N0} files · {bytes / 1048576d:N1} MiB · {workers} workers\n{Relative(root, file)}");
                watch.Restart();
            }
        }
        progress?.Invoke($"{stage} · 0/{list.Count:N0} files · {workers} workers");
        try
        {
            Parallel.ForEach(list, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = workers }, file =>
            {
                action(file, count => Report(file, count));
                Report(file, 0, true);
            });
        }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.Count == 1)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw(); throw; }
    }
}
