using System.Text.RegularExpressions;

namespace ModStudio.Core;

/// <summary>One numbered game function as the data guide documents it.</summary>
/// <param name="Parameters">The guide's parameter names, spelled as documented (<c>desctexta#</c>, <c>op stat#</c>, <c>Param1</c>).</param>
/// <param name="Supplemental">True when Studio filled a gap in the guide rather than quoting it.</param>
public sealed record FunctionCode(string Table, string Column, string Code, string Name, string Description, string[] Parameters, bool Supplemental = false)
{
    /// <summary>"srvdofunc 27 · Teleport", or "descfunc 19 · Sprintf Num" when the guide only has a description.</summary>
    public string Title => Name.Length > 0 ? $"{Column} {Code} · {Name}" : $"{Column} {Code}";
    /// <summary>The description without the name line some guide pages start it with.</summary>
    public string Summary => (Name.Length > 0 && Description.StartsWith(Name, StringComparison.Ordinal) ? Description[Name.Length..] : Description).TrimStart('\n', ' ', '.', ':', '·');
}

/// <summary>A function a row uses, and the columns of that row the function reads.</summary>
public sealed record FunctionUse(FunctionCode Function, string[] Fields);

/// <summary>
/// Decodes the numeric function columns of the game tables (<c>srvdofunc</c>, <c>pSrvHitFunc</c>, <c>descfunc</c>,
/// <c>func1</c>, <c>op</c>, <c>descline1</c>…) through the bundled data guide, and works out which other columns of a row
/// each function reads. Columns that reuse another column's function list (<c>srvprgfunc1</c> runs Server Do functions,
/// <c>dsc2line1</c> uses the descline functions) borrow its table and rename the parameters to their own spelling.
/// </summary>
public static class FunctionGuide
{
    private sealed record Alias(string Source, Dictionary<string, string>? Rename = null);
    private static readonly Dictionary<(string Table, string Column), Alias> Aliases = new()
    {
        [("skills", "srvprgfunc")] = new("srvdofunc"), [("skills", "cltprgfunc")] = new("cltdofunc"),
        [("skills", "itemeffect")] = new("srvdofunc"), [("skills", "itemclteffect")] = new("cltdofunc"),
        [("skilldesc", "dsc2line")] = new("descline1", new() { ["desctexta"] = "dsc2texta", ["desctextb"] = "dsc2textb", ["desccalca"] = "dsc2calca", ["desccalcb"] = "dsc2calcb" }),
        [("skilldesc", "dsc3line")] = new("descline1", new() { ["desctexta"] = "dsc3texta", ["desctextb"] = "dsc3textb", ["desccalca"] = "dsc3calca", ["desccalcb"] = "dsc3calcb" }),
        [("itemstatcost", "dgrpfunc")] = new("descfunc", new() { ["descval"] = "dgrpval", ["descstrpos"] = "dgrpstrpos", ["descstrneg"] = "dgrpstrneg", ["descstr2"] = "dgrpstr2" }),
    };
    /// <summary>skilldesc functions the guide does not list, described from how vanilla rows use them.</summary>
    private static readonly Dictionary<string, (string Name, string Description, string[] Parameters)> DescLineNotes = new()
    {
        ["12"] = ("Duration", "Shows the skill's duration: the auralencalc of the linked skill, in frames, as seconds.", []),
        ["18"] = ("Heading", "Shows [desctexta#] as it is, with no values. Used for headings such as the synergy list.", ["desctexta#"]),
        ["41"] = ("Two values", "Inserts [desccalca#] and [desccalcb#] into [desctexta#], like function 75.", ["desctexta#", "desccalca#", "desccalcb#"]),
        ["78"] = ("Text in text", "Inserts [desctextb#] into [desctexta#] and outputs that string.", ["desctexta#", "desctextb#"]),
        ["79"] = ("Text and ratio", "Inserts [desctextb#] and [desccalca#] / [desccalcb#] into [desctexta#] and outputs that string.", ["desctexta#", "desctextb#", "desccalca#", "desccalcb#"]),
    };
    private static readonly Dictionary<(string, string), Dictionary<string, FunctionCode>?> tables = new();
    private static readonly Lock gate = new();

    /// <summary>Whether a column holds a documented function code.</summary>
    public static bool IsFunctionColumn(string table, string column) => Codes(table, column) != null;

    /// <summary>The function a cell's value selects; null when the column is not a function column, the cell is empty or the code is undocumented.</summary>
    public static FunctionCode? Describe(string table, string column, string value)
    {
        value = value.Trim();
        if (value.Length == 0 || Codes(table, column) is not { } codes) return null;
        return codes.TryGetValue(value, out var code) ? code with { Column = column } : null;
    }

    /// <summary>Every function column of a row that selects a documented function, with the row's columns each one reads.</summary>
    public static IReadOnlyList<FunctionUse> ForRow(string table, IReadOnlyList<string> columns, Func<string, string> cell)
    {
        var uses = new List<FunctionUse>();
        foreach (var column in columns)
        {
            var value = cell(column).Trim();
            if (value is "" or "0") continue;
            if (Describe(table, column, value) is { } function) uses.Add(new(function, Fields(table, column, function, columns)));
        }
        return uses;
    }

    private static readonly Dictionary<string, (string[] Columns, HashSet<string> Hints)> possibleHints = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Columns that can ever carry a function hint in this table: function columns, and every column some documented
    /// function of the table can read. Fixed per column set, so an editor can reserve room for hints up front.
    /// </summary>
    public static IReadOnlySet<string> PossibleHints(string table, IReadOnlyList<string> columns)
    {
        lock (gate)
            if (possibleHints.TryGetValue(table, out var cached) && cached.Columns.SequenceEqual(columns)) return cached.Hints;
        var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (Codes(table, column) is not { } codes) continue;
            hints.Add(column);
            foreach (var function in codes.Values) hints.UnionWith(Fields(table, column, function, columns));
        }
        lock (gate) possibleHints[table] = ([.. columns], hints);
        return hints;
    }

    /// <summary>Which functions read each column, for marking parameters in an editor.</summary>
    public static IReadOnlyDictionary<string, List<FunctionUse>> Readers(IEnumerable<FunctionUse> uses)
    {
        var readers = new Dictionary<string, List<FunctionUse>>(StringComparer.OrdinalIgnoreCase);
        foreach (var use in uses) foreach (var field in use.Fields)
        {
            if (!readers.TryGetValue(field, out var list)) readers[field] = list = [];
            list.Add(use);
        }
        return readers;
    }

    /// <summary>
    /// The row columns a function's documented parameters name. <c>#</c> takes the function column's own number
    /// (<c>desctexta#</c> for <c>descline3</c> is <c>desctexta3</c>); a column without a number reads every numbered one
    /// (<c>op stat#</c> is <c>op stat1</c> to <c>op stat3</c>). Parameters in other tables are left out.
    /// </summary>
    public static string[] Fields(string table, string column, FunctionCode function, IReadOnlyList<string> columns)
    {
        var index = Regex.Match(column, "[0-9]+$").Value;
        var rename = AliasFor(table, column)?.Rename;
        var byName = columns.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var fields = new List<string>();
        foreach (var raw in function.Parameters)
        {
            var parameter = raw.Trim();
            if (parameter.Length == 0) continue;
            if (rename != null)
                foreach (var (from, to) in rename)
                    if (parameter.StartsWith(from, StringComparison.OrdinalIgnoreCase)) { parameter = to + parameter[from.Length..]; break; }
            if (!parameter.Contains('#')) { if (byName.TryGetValue(parameter, out var exact)) fields.Add(exact); continue; }
            if (index.Length > 0 && byName.TryGetValue(parameter.Replace("#", index), out var numbered)) { fields.Add(numbered); continue; }
            var pattern = new Regex("^" + Regex.Escape(parameter).Replace("\\#", "[0-9]+").Replace("#", "[0-9]+") + "$", RegexOptions.IgnoreCase);
            fields.AddRange(columns.Where(c => pattern.IsMatch(c)));
        }
        return fields.Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !f.Equals(column, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static Alias? AliasFor(string table, string column)
    {
        var bare = Regex.Replace(column, "[0-9]+$", "").ToLowerInvariant();
        return Aliases.GetValueOrDefault((table.ToLowerInvariant(), bare)) ?? Aliases.GetValueOrDefault((table.ToLowerInvariant(), column.ToLowerInvariant()));
    }

    private static Dictionary<string, FunctionCode>? Codes(string table, string column)
    {
        table = table.ToLowerInvariant();
        var source = AliasFor(table, column)?.Source ?? column;
        var entry = ColumnGuide.Find(table, source);
        var key = (table, entry?.Name ?? source.ToLowerInvariant());
        lock (gate)
        {
            if (tables.TryGetValue(key, out var cached)) return cached;
            return tables[key] = entry == null ? null : Build(table, entry);
        }
    }

    private static Dictionary<string, FunctionCode>? Build(string table, ColumnGuideEntry entry)
    {
        var rows = entry.Table;
        if (rows is not { Length: > 1 }) return null;
        int codeColumn = 0, nameColumn = -1, parameterColumn, descriptionColumn;
        var body = rows.AsEnumerable();
        if (entry.TableHasHeading)
        {
            var heading = rows[0];
            parameterColumn = Array.FindIndex(heading, h => h.Contains("param", StringComparison.OrdinalIgnoreCase));
            if (parameterColumn < 0) return null;
            nameColumn = Array.FindIndex(heading, h => h.Equals("Name", StringComparison.OrdinalIgnoreCase));
            descriptionColumn = Array.FindIndex(heading, h => h.StartsWith("Desc", StringComparison.OrdinalIgnoreCase));
            if (descriptionColumn < 0) descriptionColumn = heading.Length - 1;
            body = rows.Skip(1);
        }
        else
        {
            // Unheaded function tables are Code | Parameters | Description, or Code | Name | Parameters | Description.
            int width = rows.Max(r => r.Length);
            if (width is not (3 or 4) || !rows.All(r => r.Length > 0 && Regex.IsMatch(r[0], "^[0-9]"))) return null;
            nameColumn = width == 4 ? 1 : -1; parameterColumn = width - 2; descriptionColumn = width - 1;
        }
        var codes = new Dictionary<string, FunctionCode>(StringComparer.Ordinal);
        foreach (var row in body)
        {
            var code = Regex.Match(Cell(row, codeColumn), "^-?[0-9]+").Value;
            if (code.Length == 0) continue;
            var description = Cell(row, descriptionColumn).Trim();
            var name = nameColumn >= 0 ? Cell(row, nameColumn).Trim() : ShortName(description);
            var parameters = Cell(row, parameterColumn).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            codes.TryAdd(code, new(table, entry.Name, code, name, description, parameters));
        }
        if (table == "skilldesc" && entry.Name.StartsWith("descline", StringComparison.OrdinalIgnoreCase))
            foreach (var (code, note) in DescLineNotes)
                codes.TryAdd(code, new(table, entry.Name, code, note.Name, note.Description, note.Parameters, Supplemental: true));
        return codes;
    }
    private static string Cell(string[] row, int index) => index >= 0 && index < row.Length ? row[index] : "";
    /// <summary>Pages without a Name column often open the description with one ("Plus or Minus\n…", "Percent Operator. Gets…").</summary>
    private static string ShortName(string description)
    {
        var firstLine = description.Split('\n')[0].Trim();
        if (description.Contains('\n') && firstLine.Length is > 0 and <= 40 && !firstLine.Contains('[')) return firstLine.TrimEnd('.');
        var sentence = firstLine.Split(". ")[0].TrimEnd('.');
        return sentence.Length is > 0 and <= 32 && sentence != firstLine.TrimEnd('.') ? sentence : "";
    }
}
