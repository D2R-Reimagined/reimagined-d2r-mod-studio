// Shared protocol source. Keep byte-identical to D2RLevel.Core/Companion/EditorIntegration.cs.
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reimagined.Integration;

public sealed record EditorProject(string Id, string Root, string Profile = "standard", string? BaseData = null, string? Snapshot = null);
public sealed record EditorTarget(string? Table = null, string? KeyColumn = null, string? KeyValue = null, string? SourceId = null,
    string? Column = null, string? Preset = null, string? Map = null, string? LogicalMap = null, string? Assets = null);
public sealed record EditorRequest(string Action = "activate", EditorProject? Project = null, EditorTarget? Target = null,
    int ProtocolVersion = 1, string? RequestId = null)
{
    public void Validate()
    {
        if (ProtocolVersion != 1) throw new InvalidDataException("This editor integration version is unsupported. Update both applications.");
        if (Action is not ("activate" or "open-scene" or "open-table-record" or "refresh")) throw new InvalidDataException("Unknown editor action.");
        if (Action != "activate" && Project is null) throw new InvalidDataException("A project is required.");
        if (Project is { } p)
        {
            if (!Path.IsPathFullyQualified(p.Root) || !Directory.Exists(p.Root)) throw new DirectoryNotFoundException("The linked project is unavailable: " + p.Root);
            if (string.IsNullOrWhiteSpace(p.Id) || p.Profile.Length == 0 || p.Profile.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new InvalidDataException("Invalid project identity/profile.");
            if (p.Snapshot != null) IntegrationFiles.Inside(p.Root, p.Snapshot);
            if (Target?.Preset != null) IntegrationFiles.Inside(p.Root, Target.Preset);
            if (Target?.Map != null) IntegrationFiles.Inside(p.Root, Target.Map);
        }
    }
}
public sealed record EditorReply(string Status, string Message = "", int ProtocolVersion = 1, int ProcessId = 0)
{
    public bool Success => Status == "opened";
}
public sealed record EditorInstallation(string App, string Path, string Version, string Kind, string Source, int ProtocolVersion = 1)
{
    public string Platform => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public string Architecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string[] Capabilities => App == CompanionApps.Studio ? ["activate", "open-table-record", "table-snapshots"] : ["activate", "open-scene", "table-snapshots"];
    public string Identity => IntegrationFiles.Hash(App + IntegrationFiles.Canonical(Path));
}

public static class IntegrationFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Root => Environment.GetEnvironmentVariable("REIMAGINED_INTEGRATION_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D2RReimagined", "EditorIntegration");
    public static string Canonical(string path) => OperatingSystem.IsWindows() ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant() : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    public static string Inside(string root, string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!Canonical(full).StartsWith(Canonical(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Integration path is outside the linked project: " + path);
        for (var info = new FileInfo(full) as FileSystemInfo; info != null && Canonical(info.FullName) != Canonical(root); info = Directory.GetParent(info.FullName))
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Integration paths cannot traverse links.");
        return full;
    }
    public static void Write<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(file)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T Read<T>(string file, int maximumBytes = 1024 * 1024)
    {
        if (new FileInfo(file).Length > maximumBytes) throw new InvalidDataException("Integration message is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("Empty integration message.");
    }
    public static string? Option(string[] args, string option)
    {
        int i = Array.IndexOf(args, option);
        return i < 0 ? null : i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : throw new ArgumentException("Missing value for " + option);
    }
}

public static class CompanionApps
{
    public const string Studio = "ModStudio", Level = "LevelEditor";
    public static string Executable(string app) => app == Studio ? "ModStudio.App" : "D2RLevel.App";
    private static string Settings(string app) => Path.Combine(IntegrationFiles.Root, app + ".settings.json");
    public static string? Override(string app) => File.Exists(Settings(app)) ? IntegrationFiles.Read<Dictionary<string, string>>(Settings(app)).GetValueOrDefault("path") : null;
    public static void SetOverride(string app, string? path) => IntegrationFiles.Write(Settings(app), new Dictionary<string, string> { ["path"] = path ?? "" });
    public static EditorInstallation Describe(string app, string path, string source)
    {
        path = Path.GetFullPath(path);
        string kind = path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path) ? "bundle" : path.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) ? "appimage" : "portable";
        if (kind == "bundle")
        {
            if (!File.Exists(Path.Combine(path, "Contents", "MacOS", Executable(app)))) throw new FileNotFoundException("Selected bundle is not the companion application.");
        }
        else if (!File.Exists(path)) throw new FileNotFoundException("Companion application is unavailable. Browse to it or reset automatic detection.", path);
        if (kind == "portable" && Path.GetFileName(Path.GetDirectoryName(path)) == "current" && File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, "Update.exe"))) kind = "velopack";
        string version = kind is "portable" or "velopack" ? FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "unknown" : "unknown";
        return new(app, path, version, kind, source);
    }
    public static EditorInstallation Current(string app)
    {
        var path = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath ?? throw new InvalidOperationException("Application path unavailable.");
        var bundle = path.IndexOf(".app/", StringComparison.Ordinal);
        if (bundle >= 0) path = path[..(bundle + 4)];
        return Describe(app, path, "Registered application");
    }
    public static void Register(EditorInstallation app)
    {
        IntegrationFiles.Write(Path.Combine(IntegrationFiles.Root, "apps", app.Identity + ".json"), app);
    }
    public static EditorInstallation Resolve(string app)
    {
        if (Override(app) is { Length: > 0 } manual) return Describe(app, manual, "Manual selection");
        var candidates = new List<EditorInstallation>();
        var root = Path.Combine(IntegrationFiles.Root, "apps");
        if (Directory.Exists(root)) foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            try { var saved = IntegrationFiles.Read<EditorInstallation>(file); if (saved.App == app) candidates.Add(Describe(app, saved.Path, "Registered application")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D2RReimagined." + app, "current", Executable(app) + ".exe");
            if (File.Exists(path)) candidates.Add(Describe(app, path, "Velopack installation"));
        }
        if (OperatingSystem.IsMacOS()) foreach (var folder in new[] { "/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") })
        {
            var path = Path.Combine(folder, app == Studio ? "Reimagined D2R Mod Studio.app" : "Reimagined Level Editor.app");
            if (Directory.Exists(path)) candidates.Add(Describe(app, path, "Applications folder"));
        }
        var ordered = candidates.DistinctBy(x => x.Identity).OrderBy(x => x.Kind == "velopack" ? 0 : x.Path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? 2 : 1).ToArray();
        if (ordered.Length == 0) throw new FileNotFoundException("Companion application was not detected. Open Companion applications and browse to its launch application.");
        if (ordered.Length > 1 && ordered[0].Kind != "velopack") throw new InvalidOperationException("Several companion copies were found. Choose one in Companion applications.");
        return ordered[0];
    }
    public static ProcessStartInfo StartInfo(EditorInstallation app, string option, string file)
    {
        var start = new ProcessStartInfo { UseShellExecute = false };
        if (app.Kind == "velopack")
        {
            start.FileName = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(app.Path))!, "Update.exe");
            start.ArgumentList.Add("start"); start.ArgumentList.Add(Path.GetFileName(app.Path)); start.ArgumentList.Add("--");
        }
        else start.FileName = app.Kind == "bundle" ? Path.Combine(app.Path, "Contents", "MacOS", Executable(app.App)) : app.Path;
        start.ArgumentList.Add(option); start.ArgumentList.Add(file);
        start.WorkingDirectory = Path.GetDirectoryName(start.FileName)!;
        foreach (var key in new[] { "APPIMAGE", "APPDIR", "ARGV0", "OWD" }) start.Environment.Remove(key);
        return start;
    }
    public static async Task<EditorInstallation> ProbeAsync(EditorInstallation app)
    {
        var file = Path.Combine(IntegrationFiles.Root, "requests", Guid.NewGuid().ToString("N") + ".probe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            using var process = Process.Start(StartInfo(app, "--integration-probe", file));
            for (int i = 0; i < 150 && !File.Exists(file); i++) await Task.Delay(100);
            if (!File.Exists(file)) throw new InvalidOperationException("The selected application did not answer. Install a version supporting editor integration.");
            var reply = IntegrationFiles.Read<EditorInstallation>(file);
            if (reply.App != app.App || reply.ProtocolVersion != 1) throw new InvalidOperationException("The selected application is not a compatible companion.");
            return reply;
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
    public static async Task<EditorReply> SendAsync(EditorInstallation app, EditorRequest request)
    {
        request = request with { RequestId = request.RequestId ?? Guid.NewGuid().ToString("N") }; request.Validate();
        var response = await ActivationHost.TrySendAsync(app, request);
        if (response != null) return response;
        await ProbeAsync(app);
        var file = Path.Combine(IntegrationFiles.Root, "requests", request.RequestId + ".json");
        IntegrationFiles.Write(file, request);
        try
        {
            using var process = Process.Start(StartInfo(app, "--integration-request", file));
            for (int i = 0; i < 1200; i++)
            {
                if (File.Exists(file + ".reply")) return IntegrationFiles.Read<EditorReply>(file + ".reply");
                await Task.Delay(100);
            }
            throw new TimeoutException("The companion has not finished opening. Check its window for a prompt before trying again.");
        }
        finally
        {
            // The receiving app owns request cleanup: a prompt may still be open after the caller times out.
            if (File.Exists(file + ".reply")) { File.Delete(file + ".reply"); if (File.Exists(file)) File.Delete(file); }
        }
    }
}

/// <summary>One UI instance per selected installation. Current-user IPC, serialized UI activation, bounded messages.</summary>
public sealed class ActivationHost : IDisposable
{
    private readonly EditorInstallation installation;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<Func<EditorRequest, Task<EditorReply>>> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FileStream lease;
    private FileStream? projectLease;
    private string? projectRoot;
    private readonly Dictionary<string, EditorReply> completed = [];
    public EditorRequest? Initial { get; }
    private readonly string? initialFile;
    private bool started;
    public static string Pipe(EditorInstallation app) => "reimagined-editors-" + IntegrationFiles.Hash(Environment.UserName + IntegrationFiles.Root + app.Identity);
    private ActivationHost(EditorInstallation app, FileStream lease, EditorRequest? initial, string? file)
    {
        installation = app; this.lease = lease; Initial = initial; initialFile = file; CompanionApps.Register(app); _ = Listen();
    }
    public static bool Initialize(string app, string[] args, out ActivationHost? host, EditorRequest? startup = null)
    {
        host = null;
        var current = CompanionApps.Current(app);
        if (IntegrationFiles.Option(args, "--integration-probe") is { } probe) { IntegrationFiles.Write(probe, current); return false; }
        string? file = IntegrationFiles.Option(args, "--integration-request");
        var request = file == null ? startup : IntegrationFiles.Read<EditorRequest>(file); request?.Validate();
        Directory.CreateDirectory(IntegrationFiles.Root);
        FileStream acquired;
        try { acquired = new FileStream(Path.Combine(IntegrationFiles.Root, current.Identity + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            var response = TrySendAsync(current, request ?? new()).GetAwaiter().GetResult() ?? new EditorReply("failed", "The other application instance is still starting. Try again.");
            if (file != null) IntegrationFiles.Write(file + ".reply", response);
            return false;
        }
        host = new(current, acquired, request, file); return true;
    }
    public void ReleaseProject() { projectLease?.Dispose(); projectLease = null; projectRoot = null; }
    public void ClaimProject(string root)
    {
        var canonical = IntegrationFiles.Canonical(root);
        if (canonical == projectRoot) return;
        FileStream next;
        try { next = new FileStream(Path.Combine(IntegrationFiles.Root, IntegrationFiles.Hash(installation.App + canonical) + ".project.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("This project is already open in another copy of this editor. Close that copy first."); }
        projectLease?.Dispose(); projectLease = next; projectRoot = canonical;
    }
    public async Task StartAsync(Func<EditorRequest, Task<EditorReply>> handler)
    {
        if (started) return;
        started = true;
        if (Initial != null)
        {
            var response = await Handle(handler, Initial);
            if (initialFile != null) IntegrationFiles.Write(initialFile + ".reply", response);
        }
        ready.TrySetResult(handler);
    }
    private async Task<EditorReply> Handle(Func<EditorRequest, Task<EditorReply>> handler, EditorRequest request)
    {
        try
        {
            request.Validate();
            if (request.RequestId != null && completed.TryGetValue(request.RequestId, out var previous)) return previous;
            var reply = (await handler(request)) with { ProcessId = Environment.ProcessId };
            if (request.RequestId != null) { if (completed.Count > 100) completed.Clear(); completed[request.RequestId] = reply; }
            return reply;
        }
        catch (Exception ex) { return new("failed", ex.Message); }
    }
    private async Task Listen()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Pipe(installation), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader = new BinaryReader(pipe, Encoding.UTF8, true);
                using var writer = new BinaryWriter(pipe, Encoding.UTF8, true);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(125));
                var sizeBuffer = new byte[4]; await pipe.ReadExactlyAsync(sizeBuffer, deadline.Token);
                int size = BitConverter.ToInt32(sizeBuffer); if (size is < 1 or > 1024 * 1024) continue;
                var bytes = new byte[size]; await pipe.ReadExactlyAsync(bytes, deadline.Token);
                var request = JsonSerializer.Deserialize<EditorRequest>(bytes, IntegrationFiles.Json) ?? throw new InvalidDataException("Empty request.");
                var handler = await ready.Task.WaitAsync(deadline.Token);
                var reply = await Handle(handler, request);
                var result = JsonSerializer.SerializeToUtf8Bytes(reply, IntegrationFiles.Json); writer.Write(result.Length); writer.Write(result); writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
        }
    }
    public static async Task<EditorReply?> TrySendAsync(EditorInstallation app, EditorRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", Pipe(app), PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(500); } catch (TimeoutException) { return null; } catch (IOException) { return null; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, IntegrationFiles.Json);
        await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token); await pipe.WriteAsync(bytes, timeout.Token); await pipe.FlushAsync(timeout.Token);
        var size = new byte[4]; await pipe.ReadExactlyAsync(size, timeout.Token); int count = BitConverter.ToInt32(size);
        if (count is < 1 or > 1024 * 1024) throw new InvalidDataException("Invalid companion response.");
        var response = new byte[count]; await pipe.ReadExactlyAsync(response, timeout.Token);
        return JsonSerializer.Deserialize<EditorReply>(response, IntegrationFiles.Json) ?? throw new InvalidDataException("Empty companion response.");
    }
    public void Dispose() { stop.Cancel(); projectLease?.Dispose(); lease.Dispose(); }
}
