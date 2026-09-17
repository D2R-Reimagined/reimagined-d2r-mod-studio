using ModStudio.Core;

internal static class TextChecksTests
{
    public static void Run(Action<bool, string> check)
    {
        check(TableData.ColumnLetter(0) == "A" && TableData.ColumnLetter(25) == "Z" && TableData.ColumnLetter(26) == "AA" && TableData.ColumnLetter(51) == "AZ" && TableData.ColumnLetter(52) == "BA" && TableData.ColumnLetter(701) == "ZZ" && TableData.ColumnLetter(702) == "AAA",
            "Column letters follow spreadsheet order through AA and AAA");

        const string formula = "(((pa15*cond('Difficulty',normal)+pa16*cond('Difficulty',nightmare)+pa17*cond('Difficulty',hell))*256+pst1)*(100+clc1)/100)/256";
        check(TextChecks.BracketProblem(formula) == null && TextChecks.Describe(formula) == $"{formula.Length} / 255 chars · brackets balanced", "Balanced formula reports its length and balance");
        check(TextChecks.BracketProblem(formula[..^5]) is { } unclosed && unclosed.Contains("never closed") && unclosed.Contains("position 1"), "Missing closing parenthesis names the unclosed one");
        check(TextChecks.BracketProblem("a)") is { } stray && stray.Contains("no opening"), "A stray closing bracket is reported");
        check(TextChecks.BracketProblem("(a]") is { } crossed && crossed.Contains("closed by ‘]’"), "Crossed bracket kinds are reported");
        check(TextChecks.BracketProblem("(([") is { } several && several.StartsWith("3 brackets"), "Several unclosed brackets are counted");
        check(TextChecks.Describe("") == "0 / 255 chars" && TextChecks.Describe("plain") == "5 / 255 chars", "Text without brackets reports only its length");
        check(TextChecks.Describe(new string('x', 256)).Contains("over the game's limit"), "Cell text past 255 characters is flagged");

        const string json = "{ \"a\": [1, (2)], \"s\": \"(not [a] bracket)\", \"e\": \"esc\\\"(\" }";
        int openBrace = 0, closeBrace = json.Length - 1, openBracket = json.IndexOf('['), closeBracket = json.IndexOf(']');
        check(TextChecks.MatchBracket(json, 1) == (openBrace, closeBrace) && TextChecks.MatchBracket(json, json.Length) == (openBrace, closeBrace), "Outer braces match from either end");
        check(TextChecks.MatchBracket(json, openBracket + 1) == (openBracket, closeBracket), "Caret after an opening bracket finds its partner");
        check(TextChecks.MatchBracket(json, json.IndexOf("(not") + 1) == null, "Brackets inside strings are ignored");
        check(TextChecks.MatchBracket(json, json.IndexOf("(2") + 1) is { } inner && json[inner.Close] == ')', "Nested parentheses match each other");
        check(TextChecks.MatchBracket("(a]", 1) == null && TextChecks.MatchBracket("plain", 2) == null, "Mismatched or absent brackets give no pair");

        // Act palette hue tables: table t maps index i to i + t in this synthetic file, so the transformed colour of index 0 is the base colour of index t.
        var pl2 = new byte[PaletteShifts.FileSize];
        for (int i = 0; i < 256; i++) { pl2[i * 4] = (byte)i; pl2[i * 4 + 1] = (byte)(255 - i); pl2[i * 4 + 2] = 7; }
        for (int t = 0; t < PaletteShifts.Tables; t++) for (int i = 0; i < 256; i++) pl2[PaletteShifts.HueOffset + t * 256 + i] = (byte)((i + t) % 256);
        var shifts = PaletteShifts.Load(pl2);
        check(shifts.Base(10) == (10, 245, 7) && shifts.Transformed(3)[0] == (3, 252, 7) && shifts.Transformed(-1)[9] == shifts.Base(9), "Hue tables remap the base palette");
        check(shifts.IsIdentity(0) && !shifts.IsIdentity(1), "Identity hue tables are recognized");
        bool tooShort = false; try { PaletteShifts.Load(new byte[1000]); } catch (Exception) { tooShort = true; }
        check(tooShort, "A file that is not a pal.pl2 is rejected");
        check(PaletteShifts.IsTransformColumn("monstats", "TransLvl") && PaletteShifts.IsTransformColumn("superuniques", "Utrans(H)") && !PaletteShifts.IsTransformColumn("monstats", "hcIdx") && !PaletteShifts.IsTransformColumn("skills", "Utrans"),
            "Only monstats TransLvl and superuniques Utrans columns are colour transforms");
    }
}
