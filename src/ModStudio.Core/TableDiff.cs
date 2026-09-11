namespace ModStudio.Core;

public record CellDifference(int Row, string Column, string Before, string After)
{
    public override string ToString() => $"row {Row} · {Column}: {Before} → {After}";
}
public static class TableDiff
{
    public static List<CellDifference> Compare(TableData before, TableData after)
    {
        var result = new List<CellDifference>();
        for (int row = 0; row < Math.Max(before.Records.Count, after.Records.Count); row++)
        {
            if (row >= before.Records.Count || row >= after.Records.Count) { result.Add(new(row, "(record)", row < before.Records.Count ? "exists" : "missing", row < after.Records.Count ? "exists" : "missing")); continue; }
            foreach (var column in before.Columns.Union(after.Columns))
            {
                var old = before.Cell(row, column); var next = after.Cell(row, column);
                if (old != next) result.Add(new(row, column, old, next));
            }
            if (before.Records[row]?["columnCount"]?.ToString() != after.Records[row]?["columnCount"]?.ToString()) result.Add(new(row, "(row width)", before.Records[row]?["columnCount"]?.ToString() ?? "", after.Records[row]?["columnCount"]?.ToString() ?? ""));
        }
        return result;
    }
}
