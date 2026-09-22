using System.Globalization;
using System.Text;

namespace ModStudio.Core;

/// <summary>A parsed BBE calc expression, the formula language of skills.txt, skilldesc.txt and missiles.txt calc columns.</summary>
public abstract record CalcNode;
public sealed record CalcNumber(long Value) : CalcNode;
/// <summary>A skillcalc/misscalc code such as <c>ln12</c>, <c>par8</c> or <c>lvl</c>.</summary>
public sealed record CalcCode(string Code) : CalcNode;
public sealed record CalcUnary(string Operator, CalcNode Operand) : CalcNode;
public sealed record CalcBinary(string Operator, CalcNode Left, CalcNode Right) : CalcNode;
public sealed record CalcConditional(CalcNode Condition, CalcNode WhenTrue, CalcNode WhenFalse) : CalcNode;
/// <summary><c>min</c>, <c>max</c> or <c>rand</c> over ordinary arguments.</summary>
public sealed record CalcCall(string Function, CalcNode[] Arguments) : CalcNode;
/// <summary>
/// A lookup into another row: <c>skill('Fire Ball'.blvl)</c> (<c>sksrc</c> reads the skill on the unit that created a missile), <c>miss('fireball'.edmn)</c>, <c>stat('strength'.accr)</c>,
/// or <c>sklvl('Holy Fire'.ln56.edmn)</c>, whose middle part is the level to evaluate the last part at.
/// </summary>
public sealed record CalcReference(string Function, string Name, string[] Path) : CalcNode
{
    public override string ToString() => $"{Function}('{Name}'.{string.Join('.', Path)})";
}

/// <summary>What an expression's codes and lookups mean; the evaluator itself only knows arithmetic.</summary>
public interface ICalcScope
{
    long Code(string code);
    long Reference(CalcReference reference);
    /// <summary><c>rand(low, high)</c>: the game rolls it, so a preview has to pick a value and say so.</summary>
    long Random(long low, long high);
}

/// <summary>
/// Parser and evaluator for calc expressions. Arithmetic is integer, as in the game: division truncates toward zero and
/// division by zero gives 0. Comparisons give 1 or 0; <c>a ? b : c</c> takes <c>b</c> when <c>a</c> is not 0.
/// </summary>
public static class Calc
{
    public static readonly string[] ReferenceFunctions = ["skill", "sksrc", "miss", "stat", "sklvl"];
    public static readonly string[] Functions = ["min", "max", "rand"];

    /// <summary>
    /// Parses one calculation. Spreadsheet exports wrap cells that contain a comma in double quotes
    /// (<c>"min(ln12,24)"</c>) and the game reads them as the formula inside, so one surrounding pair is accepted.
    /// </summary>
    /// <remarks>
    /// The game is lenient in two ways vanilla data relies on, so the parser is too and reports them in
    /// <paramref name="warnings"/>: closing parentheses missing at the very end are supplied, and a decimal is read as its
    /// whole part.
    /// </remarks>
    public static CalcNode Parse(string text, List<string>? warnings = null)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"') text = trimmed[1..^1].Replace("\"\"", "\"");
        return new Parser(text, warnings ?? []).ParseAll();
    }
    public static bool TryParse(string text, out CalcNode? node, out string error) => TryParse(text, out node, out error, []);
    public static bool TryParse(string text, out CalcNode? node, out string error, List<string> warnings)
    {
        try { node = Parse(text, warnings); error = ""; return true; }
        catch (FormatException e) { node = null; error = e.Message; return false; }
    }

    public static long Evaluate(CalcNode node, ICalcScope scope) => node switch
    {
        CalcNumber n => n.Value,
        CalcCode c => scope.Code(c.Code),
        CalcReference r => scope.Reference(r),
        CalcUnary { Operator: "-" } u => -Evaluate(u.Operand, scope),
        CalcUnary u => Evaluate(u.Operand, scope),
        CalcConditional c => Evaluate(c.Condition, scope) != 0 ? Evaluate(c.WhenTrue, scope) : Evaluate(c.WhenFalse, scope),
        CalcCall { Function: "min" } c => c.Arguments.Select(a => Evaluate(a, scope)).Min(),
        CalcCall { Function: "max" } c => c.Arguments.Select(a => Evaluate(a, scope)).Max(),
        CalcCall { Function: "rand" } c => scope.Random(Evaluate(c.Arguments[0], scope), Evaluate(c.Arguments[1], scope)),
        CalcBinary b => Binary(b.Operator, Evaluate(b.Left, scope), Evaluate(b.Right, scope)),
        _ => throw new InvalidOperationException("Unknown calc node " + node.GetType().Name)
    };
    private static long Binary(string op, long a, long b) => op switch
    {
        "+" => a + b, "-" => a - b, "*" => a * b,
        "/" => b == 0 ? 0 : a / b,
        "<" => a < b ? 1 : 0, ">" => a > b ? 1 : 0, "<=" => a <= b ? 1 : 0, ">=" => a >= b ? 1 : 0,
        "==" => a == b ? 1 : 0, "!=" => a != b ? 1 : 0,
        _ => throw new InvalidOperationException("Unknown calc operator " + op)
    };

    /// <summary>Every node of an expression, parents before children.</summary>
    public static IEnumerable<CalcNode> Walk(CalcNode node)
    {
        yield return node;
        IEnumerable<CalcNode> children = node switch
        {
            CalcUnary u => [u.Operand], CalcBinary b => [b.Left, b.Right], CalcConditional c => [c.Condition, c.WhenTrue, c.WhenFalse],
            CalcCall c => c.Arguments, _ => []
        };
        foreach (var child in children) foreach (var descendant in Walk(child)) yield return descendant;
    }

    private sealed class Parser(string text, List<string> warnings)
    {
        private int position;
        private int supplied;
        public CalcNode ParseAll()
        {
            Skip();
            if (position >= text.Length) throw Error("The calculation is empty");
            var node = Conditional();
            Skip();
            if (position < text.Length) throw Error($"Unexpected '{text[position]}'");
            if (supplied > 0) warnings.Add($"{supplied} closing parenthes{(supplied == 1 ? "is is" : "es are")} missing at the end; the game closes {(supplied == 1 ? "it" : "them")} there.");
            return node;
        }
        private CalcNode Conditional()
        {
            var condition = Comparison();
            if (!Accept("?")) return condition;
            var whenTrue = Conditional();
            Expect(":");
            return new CalcConditional(condition, whenTrue, Conditional());
        }
        private CalcNode Comparison()
        {
            var left = Additive();
            while (AcceptAny(["<=", ">=", "==", "!=", "<", ">"], out var op)) left = new CalcBinary(op, left, Additive());
            return left;
        }
        private CalcNode Additive()
        {
            var left = Term();
            while (AcceptAny(["+", "-"], out var op)) left = new CalcBinary(op, left, Term());
            return left;
        }
        private CalcNode Term()
        {
            var left = Unary();
            while (AcceptAny(["*", "/"], out var op)) left = new CalcBinary(op, left, Unary());
            return left;
        }
        private CalcNode Unary() => AcceptAny(["-", "+"], out var op) ? new CalcUnary(op, Unary()) : Primary();
        private CalcNode Primary()
        {
            Skip();
            if (position >= text.Length) throw Error("The calculation ends early");
            char c = text[position];
            if (Accept("(")) { var inner = Conditional(); Expect(")"); return inner; }
            if (char.IsDigit(c)) return new CalcNumber(Number());
            if (!IsIdentifierStart(c)) throw Error($"Unexpected '{c}'");
            var name = Identifier();
            Skip();
            if (position >= text.Length || text[position] != '(') return new CalcCode(name);
            var function = name.ToLowerInvariant();
            position++;
            if (ReferenceFunctions.Contains(function))
            {
                Skip();
                if (position >= text.Length || text[position] != '\'') throw Error($"{function}() needs a quoted name, as in {function}('Name'.code)");
                var target = Quoted();
                var path = new List<string>();
                while (Accept(".")) { Skip(); path.Add(position < text.Length && char.IsDigit(text[position]) ? Number().ToString(CultureInfo.InvariantCulture) : Identifier()); }
                if (path.Count == 0) throw Error($"{function}('{target}') needs a code after the name, as in {function}('{target}'.blvl)");
                if (function == "sklvl" && path.Count != 2) throw Error($"sklvl('{target}'…) needs a level and a code, as in sklvl('{target}'.lvl.edmn)");
                if (function != "sklvl" && path.Count != 1) throw Error($"{function}('{target}'…) takes one code after the name");
                Expect(")");
                return new CalcReference(function, target, [.. path]);
            }
            if (!Functions.Contains(function)) throw Error($"Unknown function {name}()");
            var arguments = new List<CalcNode>();
            if (!Accept(")"))
            {
                do arguments.Add(Conditional()); while (Accept(","));
                Expect(")");
            }
            if (function == "rand" ? arguments.Count != 2 : arguments.Count == 0) throw Error($"{function}() takes {(function == "rand" ? "two arguments" : "at least one argument")}");
            return new CalcCall(function, [.. arguments]);
        }
        private long Number()
        {
            int start = position;
            while (position < text.Length && char.IsDigit(text[position])) position++;
            if (!long.TryParse(text.AsSpan(start, position - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) throw Error("Number is too large");
            if (position < text.Length && text[position] == '.' && position + 1 < text.Length && char.IsDigit(text[position + 1]))
            {
                int fraction = ++position;
                while (position < text.Length && char.IsDigit(text[position])) position++;
                warnings.Add($"Calculations are whole numbers: {value}.{text[fraction..position]} counts as {value}.");
            }
            return value;
        }
        private string Identifier()
        {
            int start = position;
            if (position >= text.Length || !IsIdentifierStart(text[position])) throw Error("Expected a name");
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_')) position++;
            return text[start..position];
        }
        private string Quoted()
        {
            position++; var value = new StringBuilder();
            while (position < text.Length && text[position] != '\'') value.Append(text[position++]);
            if (position >= text.Length) throw Error("Unclosed quote");
            position++;
            return value.ToString();
        }
        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
        private void Skip() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        private bool Accept(string token)
        {
            Skip();
            if (string.CompareOrdinal(text, position, token, 0, token.Length) != 0) return false;
            position += token.Length; return true;
        }
        private bool AcceptAny(string[] tokens, out string token)
        {
            foreach (var candidate in tokens) if (Accept(candidate)) { token = candidate; return true; }
            token = ""; return false;
        }
        private void Expect(string token)
        {
            if (Accept(token)) return;
            Skip();
            if (token == ")" && position >= text.Length) { supplied++; return; }
            throw Error(position < text.Length ? $"Expected '{token}' but found '{text[position]}'" : $"Expected '{token}' before the end");
        }
        private FormatException Error(string message) => new($"{message} (column {Math.Min(position, text.Length) + 1}).");
    }
}
