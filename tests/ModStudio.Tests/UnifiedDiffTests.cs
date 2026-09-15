using ModStudio.Core;

internal static class UnifiedDiffTests
{
    public static void Run(Action<bool, string> check)
    {
        const string patch = "diff --git a/f.txt b/f.txt\nindex 1..2 100644\n--- a/f.txt\n+++ b/f.txt\n@@ -1,6 +1,6 @@\n keep\n-old one\n-old two\n+new one\n keep2\n+added\n-gone\n keep3\n\\ No newline at end of file\n";
        var rows = UnifiedDiff.Rows(patch);
        check(rows.Count == 6 && rows[0] is { Kind: DiffRowKind.Equal, LeftLine: 1, RightLine: 1, Left: "keep" }, "Context lines become equal rows with both line numbers");
        check(rows[1] is { Kind: DiffRowKind.Modified, LeftLine: 2, Left: "old one", RightLine: 2, Right: "new one" } && rows[2] is { Kind: DiffRowKind.Removed, LeftLine: 3, Left: "old two", Right: null, RightLine: null }, "Removed and added runs pair up as modified rows, leftovers become one-sided rows");
        check(rows[3] is { Kind: DiffRowKind.Equal, LeftLine: 4, RightLine: 3 } && rows[4] is { Kind: DiffRowKind.Modified, Left: "gone", Right: "added", LeftLine: 5, RightLine: 4 } && rows[5] is { Kind: DiffRowKind.Equal, Left: "keep3" }, "Line numbers advance independently per side and the no-newline marker is ignored");
        check(UnifiedDiff.ChangeStarts(rows).SequenceEqual([1, 4]), "Change blocks start where a non-equal row follows an equal row");
        check(UnifiedDiff.Rows("").Count == 0 && UnifiedDiff.Rows("--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,2 @@\n+a\n+b\n").All(r => r.Kind == DiffRowKind.Added && r.Left == null) && UnifiedDiff.Rows("--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,2 @@\n+a\n+b\n")[1].RightLine == 2, "New files produce only added rows numbered on the right");
        check(UnifiedDiff.IsBinary("diff --git a/x.png b/x.png\nBinary files a/x.png and b/x.png differ\n") && !UnifiedDiff.IsBinary(patch), "Binary patches are recognized");
        var two = UnifiedDiff.Rows("@@ -1,2 +1,2 @@\n a\n-b\n+c\n@@ -10,2 +10,2 @@\n x\n-y\n+z\n");
        check(two.Count == 4 && two[2] is { LeftLine: 10, RightLine: 10 } && two[3] is { Kind: DiffRowKind.Modified, LeftLine: 11, Right: "z" }, "Later hunks restart numbering from their header");
    }
}
