namespace ModStudio.Core;

/// <summary>Class membership for skilldesc rows comes from skills.skilldesc and skills.charclass.</summary>
public sealed class SkillDescClassFilter
{
    private readonly Dictionary<string, HashSet<string>> classesByDescription = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Classes { get; }

    public SkillDescClassFilter(TableData skills)
    {
        if (skills.ColumnIndex("skilldesc") < 0 || skills.ColumnIndex("charclass") < 0)
            throw new InvalidOperationException("skills.txt needs skilldesc and charclass columns for class filtering.");
        for (var row = 0; row < skills.Records.Count; row++)
        {
            var description = skills.Cell(row, "skilldesc").Trim();
            if (description.Length == 0) continue;
            var characterClass = skills.Cell(row, "charclass").Trim().ToLowerInvariant();
            if (!classesByDescription.TryGetValue(description, out var classes))
                classesByDescription[description] = classes = new(StringComparer.OrdinalIgnoreCase);
            classes.Add(characterClass);
        }
        Classes = classesByDescription.Values.SelectMany(c => c).Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool Matches(string description, string selectedClass)
    {
        if (selectedClass.Length == 0) return true; // All
        var classes = classesByDescription.GetValueOrDefault(description.Trim());
        return selectedClass == "*" ? classes == null || classes.Contains("") : classes?.Contains(selectedClass) == true; // Any or a class
    }
}
