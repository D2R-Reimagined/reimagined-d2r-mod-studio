using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

try
{
    if (args.Length == 0) { Console.WriteLine("Reimagined D2R Mod Studio: import <data> <destination> <mod-name> | migrate <legacy-project> <destination> <mod-name> | check <project> | build <project> [profile] | preview-info <file> | item-preview <project> <uniqueitems|setitems> <row> [profile] [level] [locale] | item-preview-audit <project> [profile] [level] [locale] | benchmark <project> | compare <built-mod-root> <baseline-mod-root>"); return; }
    switch (args[0])
    {
        case "item-preview":
            var itemProject = ModProject.Open(args[1]); var itemTable = TableData.Load(Inside(itemProject.Root, "source/tables/" + args[2] + "/records.json"));
            var itemRow = int.Parse(args[3]); Require(itemRow >= 0 && itemRow < itemTable.Records.Count, "Invalid item row.");
            var itemResult = new ItemPreviewResolver().Resolve(itemProject, args[2], (JsonObject)itemTable.Records[itemRow]!, args.ElementAtOrDefault(4) ?? "standard", int.Parse(args.ElementAtOrDefault(5) ?? "80"), args.ElementAtOrDefault(6) ?? "enUS", default);
            Console.WriteLine(JsonSerializer.Serialize(itemResult, Pretty)); break;
        case "item-preview-audit":
            var auditProject = ModProject.Open(args[1]);
            var auditRows = new List<(string Table, JsonObject Row)>();
            foreach (var auditTableName in new[] { "uniqueitems", "setitems" })
            {
                var auditFile = Inside(auditProject.Root, $"source/tables/{auditTableName}/records.json");
                if (!File.Exists(auditFile)) continue;
                auditRows.AddRange(TableData.Load(auditFile).Records.OfType<JsonObject>().Select(row => (auditTableName, row)));
            }
            var issueCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            int completedPreviews = 0;
            Parallel.ForEach(auditRows, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount)) },
                () => new ItemPreviewResolver(),
                (entry, _, resolver) =>
                {
                    try
                    {
                        var result = resolver.Resolve(auditProject, entry.Table, entry.Row, args.ElementAtOrDefault(2) ?? "standard", int.Parse(args.ElementAtOrDefault(3) ?? "80"), args.ElementAtOrDefault(4) ?? "enUS", default);
                        if (result.Issues.Length == 0) Interlocked.Increment(ref completedPreviews);
                        foreach (var issue in result.Issues) issueCounts.AddOrUpdate(issue, 1, (_, count) => count + 1);
                    }
                    catch (Exception ex) { issueCounts.AddOrUpdate("Preview failed: " + ex.Message, 1, (_, count) => count + 1); }
                    return resolver;
                }, resolver => resolver.Clear());
            Console.WriteLine(JsonSerializer.Serialize(new { Items = auditRows.Count, Complete = completedPreviews, Incomplete = auditRows.Count - completedPreviews, Issues = issueCounts.OrderByDescending(x => x.Value).Select(x => new { Count = x.Value, Issue = x.Key }) }, Pretty));
            break;
        case "preview-info":
            var preview = SpecialistPreview.Load(args[1]); var pixels = preview.Decode(preview.InitialFrame);
            Console.WriteLine(JsonSerializer.Serialize(new { preview.Summary, Frames = preview.Frames.Length, pixels.Width, pixels.Height }, Pretty)); break;
        case "check":
            var check = Semantics.Check(ModProject.Open(args[1])); Console.WriteLine(JsonSerializer.Serialize(check, Pretty));
            if (check.Diagnostics.Any(d => d.Severity == "Error")) Environment.ExitCode = 1; break;
        case "migrate":
            var candidates = LegacyMigration.Detect(args[1]);
            Require(candidates.Count == 1, "Choose a single mod/data folder. Candidates: " + string.Join("; ", candidates));
            Console.WriteLine(JsonSerializer.Serialize(LegacyMigration.Migrate(candidates[0], args[2], args[3], progress: Console.WriteLine), Pretty)); break;
        case "import": Console.WriteLine(JsonSerializer.Serialize(ProjectImporter.Import(args[1], args[2], args[3], progress: Console.WriteLine), Pretty)); break;
        case "build": Console.WriteLine(JsonSerializer.Serialize(BuildService.Build(ModProject.Open(args[1]), args.ElementAtOrDefault(2) ?? "standard", progress: Console.WriteLine), Pretty)); break;
        case "benchmark":
            var report = new JsonArray();
            foreach (var name in new[] { "sounds", "cubemain", "skills" })
            {
                GC.Collect(); var before = GC.GetTotalMemory(true); var timer = Stopwatch.StartNew();
                var doc = new Document(Path.Combine(args[1], "source/tables", name, "records.json")); timer.Stop();
                report.Add(new JsonObject { ["table"] = name, ["rows"] = doc.Table?.Records.Count, ["columns"] = doc.Table?.Columns.Length, ["loadAndValidateMs"] = timer.Elapsed.TotalMilliseconds, ["managedBytesAdded"] = GC.GetTotalMemory(false) - before, ["errors"] = doc.Diagnostics.Count });
            }
            Console.WriteLine(Json(report)); break;
        case "compare":
            var generatedFiles = Files(args[1]).ToArray(); var baselineFiles = Files(args[2]).ToArray();
            Require(generatedFiles.Length == baselineFiles.Length, "Output file counts differ."); int exact = 0, semantic = 0;
            foreach (var file in baselineFiles)
            {
                var next = Inside(args[1], Relative(args[2], file)); var old = File.ReadAllBytes(file); var actual = File.ReadAllBytes(next);
                if (old.SequenceEqual(actual)) exact++;
                else if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && JsonNode.DeepEquals(Read(file), Read(next))) semantic++;
                else throw new InvalidDataException("Output differs: " + Relative(args[2], file));
            }
            Console.WriteLine($"Matched {exact} byte-identical files and {semantic} equivalent JSON files."); break;
        default: throw new ArgumentException("Unknown command.");
    }
}
catch (Exception e) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; }
