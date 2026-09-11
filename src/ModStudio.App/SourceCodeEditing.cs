using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using System.Xml;

namespace ModStudio.App;

internal static class SourceCodeEditing
{
    public static IHighlightingDefinition? Highlighting(string file)
    {
        var extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (extension is ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx") extension = ".js";
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension);
        if (extension is ".bat" or ".cmd")
        {
            const string batch = """
                <SyntaxDefinition name="Batch" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
                  <Color name="Comment" foreground="#6A9955" />
                  <Color name="Keyword" foreground="#569CD6" />
                  <Color name="String" foreground="#CE9178" />
                  <Color name="Variable" foreground="#9CDCFE" />
                  <RuleSet ignoreCase="true">
                    <Span color="Comment" begin="^\s*(?:rem\b|::)" />
                    <Span color="String" begin="&quot;" end="&quot;" />
                    <Rule color="Variable">%[^%\r\n]+%|%[0-9*]|![^!\r\n]+!</Rule>
                    <Keywords color="Keyword"><Word>echo</Word><Word>off</Word><Word>set</Word><Word>setlocal</Word><Word>endlocal</Word><Word>if</Word><Word>else</Word><Word>for</Word><Word>in</Word><Word>do</Word><Word>call</Word><Word>goto</Word><Word>exit</Word><Word>copy</Word><Word>xcopy</Word><Word>robocopy</Word><Word>del</Word><Word>mkdir</Word><Word>cd</Word><Word>pause</Word></Keywords>
                  </RuleSet>
                </SyntaxDefinition>
                """;
            using var reader = XmlReader.Create(new StringReader(batch));
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        if (definition != null)
            foreach (var color in definition.NamedHighlightingColors)
            {
                var name = color.Name.ToLowerInvariant();
                var hex = name.Contains("comment") ? "#6A9955" : name.Contains("string") || name.Contains("char") ? "#CE9178" : name.Contains("number") || name.Contains("digit") ? "#B5CEA8" : "#569CD6";
                color.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
            }
        return definition;
    }
}
