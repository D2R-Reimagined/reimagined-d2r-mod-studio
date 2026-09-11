using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModStudio.Core;
using static ModStudio.Core.Storage;

try
{
    if (args.Length == 0) { Console.WriteLine("Reimagined D2R Mod Studio: import <data> <destination> <mod-name> | migrate <legacy-project> <destination> <mod-name> | check <project> | build <project> [profile] | benchmark <project> | compare <built-mod-root> <baseline-mod-root>"); return; }
    switch (args[0])
    {
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
