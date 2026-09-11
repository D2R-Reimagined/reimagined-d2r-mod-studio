using System.Text;
using ModStudio.Core;

internal static class UiFixture
{
    // Synthetic data only: the distributable tests contain no Blizzard or mod assets.
    public static void Create(string destination)
    {
        destination = Path.GetFullPath(destination);
        Storage.Require(!Directory.Exists(destination), "UI fixture destination already exists.");
        var native = destination + ".native-" + Guid.NewGuid().ToString("N");
        try
        {
            var excel = Path.Combine(native, "global", "excel"); Directory.CreateDirectory(excel);
            foreach (var (name, count, columns) in new[] { ("sounds", 1200, 40), ("cubemain", 1600, 100), ("skills", 490, 322) })
            {
                var text = new StringBuilder("name\t");
                text.AppendLine(string.Join('\t', Enumerable.Range(1, columns - 1).Select(i => "field" + i)));
                for (int row = 0; row < count; row++)
                {
                    text.Append(name).Append('_').Append(row).Append('\t');
                    text.AppendLine(string.Join('\t', Enumerable.Range(1, columns - 1).Select(i => i % 7 == 0 ? "" : ((row + i) % 250).ToString())));
                }
                File.WriteAllText(Path.Combine(excel, name + ".txt"), text.ToString(), Storage.Utf8);
            }
            ProjectImporter.Import(native, destination, "ExampleMod");
        }
        finally { if (Directory.Exists(native)) Directory.Delete(native, true); }
    }
}
