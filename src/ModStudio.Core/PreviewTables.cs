using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// The reading side every preview shares: a bounded file cache, the selected profile's table overrides, row lookups
/// and the localized string catalogs. Worker-owned; reads authored data only and never changes records or starts a build.
/// </summary>
public sealed class PreviewTables
{
    private sealed record Cached(long Length, DateTime Modified, JsonNode Data) { public Dictionary<string, Dictionary<string, string>> Locales { get; } = new(); }
    private readonly Dictionary<string, Cached> cache = new();
    private long retained;
    public void Clear() { cache.Clear(); retained = 0; }
    private JsonNode ReadCached(string file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); NoLinks(file); var info = new FileInfo(file);
        Require(info.Exists, "Missing data: " + file);
        Require(info.Length <= 32 * 1024 * 1024, "Preview input exceeds 32 MiB: " + file);
        if (cache.TryGetValue(file, out var old) && old.Length == info.Length && old.Modified == info.LastWriteTimeUtc) return old.Data;
        if (old != null) { retained -= old.Length; cache.Remove(file); }
        if (retained + info.Length > 64 * 1024 * 1024) { cache.Clear(); retained = 0; }
        var data = Read(file); cache[file] = new(info.Length, info.LastWriteTimeUtc, data); retained += info.Length; return data;
    }

    /// <summary>Opens one resolution against a project, profile and locale. Issues raised while reading are appended to <paramref name="issues"/>.</summary>
    public Session Open(ModProject project, string profile, string locale, List<string> issues, CancellationToken token) => new(this, project, profile, locale, issues, token);

    /// <summary>One preview resolution's view of the authored data. Not reusable across projects, profiles or locales.</summary>
    public sealed class Session
    {
        private readonly PreviewTables owner;
        private readonly ModProject project;
        private readonly string locale;
        private readonly List<string> issues;
        private readonly CancellationToken token;
        private readonly List<JsonNode> rules = [];
        private readonly Dictionary<(string Table, string Column), ILookup<string, JsonObject>> lookups = [];
        private readonly List<Dictionary<string, string>> catalogs = [];
        internal Session(PreviewTables owner, ModProject project, string profile, string locale, List<string> issues, CancellationToken token)
        {
            this.owner = owner; this.project = project; this.locale = locale; this.issues = issues; this.token = token;
            var profilePath = Inside(project.Root, $"compatibility/{profile}/profile.json");
            bool standard = File.Exists(profilePath) ? owner.ReadCached(profilePath, token).S("stringMode", "standard") == "standard" : profile == "standard";
            if (File.Exists(profilePath))
                foreach (var path in (JsonArray?)owner.ReadCached(profilePath, token)["tableOverrides"] ?? [])
                    rules.Add(owner.ReadCached(Inside(Path.GetDirectoryName(profilePath)!, path!.GetValue<string>()), token));
            foreach (var file in Files(Inside(project.Root, "source/strings")).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            {
                var data = owner.ReadCached(file, token)["records"]!; var cached = owner.cache[file];
                if (!cached.Locales.TryGetValue(locale + standard, out var translations))
                {
                    translations = new(StringComparer.Ordinal);
                    foreach (var row in data.AsArray())
                    {
                        token.ThrowIfCancellationRequested(); var key = row.S("Key");
                        var full = row?["translations"]?.S(locale) ?? "";
                        var compact = standard ? row?["standardTranslations"]?[locale] : null;
                        if (compact != null) Require(row?["standardReviewedAgainst"].S(locale) == Hash(full), "Compact localization needs review: " + key);
                        var translation = compact?.GetValue<string>() ?? full;
                        if (translation.Length > 0) translations.TryAdd(key, Regex.Replace(translation, "ÿc.", ""));
                    }
                    if (cached.Locales.Count >= 4) cached.Locales.Clear();
                    cached.Locales[locale + standard] = translations;
                }
                catalogs.Add(translations);
            }
        }
        /// <summary>A row's fields with the selected profile's overrides applied. The authored record is never changed.</summary>
        public JsonObject Effective(string name, JsonObject row)
        {
            var fields = (JsonObject)row["fields"]!.DeepClone(); var occupied = new HashSet<string>();
            foreach (var rule in rules.Where(r => r.S("table") == name && r.S("record") == row.S("sourceId")))
            {
                Require(rule["targets"] == null, $"{name}: bank-specific overrides need a bank selection; preview is unavailable.");
                foreach (var change in rule["changes"]!.AsObject())
                {
                    Require(occupied.Add(change.Key), $"{name}/{change.Key}: competing overrides.");
                    Require(fields.S(change.Key) == change.Value.S("expect"), $"{name}/{change.Key}: stale override. Save or reconcile the shared value.");
                    fields[change.Key] = change.Value.S("value");
                }
            }
            return fields;
        }
        /// <summary>The one row of a table whose column holds a value, with overrides applied; null (and an issue) when it is not there.</summary>
        public JsonObject? Find(string name, string column, string value, bool required = true)
        {
            token.ThrowIfCancellationRequested(); var file = TableData.FileFor(project, "tables", name);
            if (!File.Exists(file)) { if (required) issues.Add($"Missing table: {name}."); return null; }
            if (!lookups.TryGetValue((name, column), out var lookup))
            {
                var rows = new List<JsonObject>();
                foreach (var row in owner.ReadCached(file, token)["records"]!.AsArray().OfType<JsonObject>())
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(rules.Any(r => r.S("table") == name && r.S("record") == row.S("sourceId")) ? Effective(name, row) : row["fields"]!.AsObject());
                }
                lookup = rows.ToLookup(r => r.S(column), StringComparer.Ordinal); lookups[(name, column)] = lookup;
            }
            var matches = lookup[value].Take(2).ToArray(); Require(matches.Length <= 1, $"Ambiguous {name}/{column}: {value}.");
            var found = matches.FirstOrDefault();
            if (found == null && required) issues.Add($"Unresolved {name}/{column}: {value}."); return found;
        }
        /// <summary>The selected locale's string for a key; the key itself (and an issue) when it is not translated.</summary>
        public string Localize(string key)
        {
            foreach (var strings in catalogs) if (strings.TryGetValue(key, out var text)) return text;
            issues.Add($"Missing {locale} localization: {key}."); return key;
        }
    }
}
