using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModStudio.Core;

public static class Storage
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, IndentSize = 4, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNameCaseInsensitive = true };
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    public static string Hash(string text) => Hash(Utf8.GetBytes(text));
    public static string Json(JsonNode node) => node.ToJsonString(Pretty) + "\n";
    public static JsonNode Read(string file) => JsonNode.Parse(File.ReadAllText(file, Utf8)) ?? throw new InvalidDataException($"Empty JSON: {file}");
    public static string S(this JsonNode? node, string key, string fallback = "") => node?[key]?.GetValue<string>() ?? fallback;
    public static int I(this JsonNode? node, string key, int fallback = 0) => node?[key]?.GetValue<int>() ?? fallback;
    public static bool B(this JsonNode? node, string key, bool fallback = false) => node?[key]?.GetValue<bool>() ?? fallback;
    public static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    public static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    public static bool Contains(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)); child = Path.GetFullPath(child);
        return child.Equals(parent, comparison) || child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, $"Linked paths are not supported: {current}");
    }
    public static string Inside(string root, string relative)
    {
        Require(!string.IsNullOrWhiteSpace(relative) && !relative.Contains('\\') && !relative.Contains(':') && !Path.IsPathRooted(relative) && relative.Split('/').All(p => p is not ("" or "." or "..")), $"Unsafe path: {relative}");
        var result = Path.GetFullPath(Path.Combine(root, relative));
        Require(Contains(root, result), "Path escapes its root."); NoLinks(result); return result;
    }
    public static IEnumerable<string> Files(string root)
    {
        NoLinks(root); if (!Directory.Exists(root)) yield break;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            NoLinks(entry);
            if (Directory.Exists(entry)) { foreach (var child in Files(entry)) yield return child; }
            else yield return entry;
        }
    }
    public static void AtomicWrite(string file, byte[] bytes, string? expectedHash = null, bool requireAbsent = false)
    {
        NoLinks(file); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".studio-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (expectedHash != null) Require(File.Exists(file) && Hash(File.ReadAllBytes(file)) == expectedHash, $"File changed externally: {file}. Reload or reconcile before saving.");
            File.Move(temporary, file, !requireAbsent);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void WriteJson(string file, JsonNode value) => AtomicWrite(file, Utf8.GetBytes(Json(value)));
}

public record Diagnostic(string File, string Message, string Severity = "Error", int Row = -1, string Field = "")
{
    public override string ToString() => $"{Severity} · {Path.GetFileName(Path.GetDirectoryName(File))}/{Path.GetFileName(File)}{(Row >= 0 ? $" · row {Row}" : "")}{(Field.Length > 0 ? $" · {Field}" : "")} — {Message}";
}
