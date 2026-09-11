using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;

namespace ModStudio.App;

internal static class MarkdownPreviewText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseTaskLists().UsePreciseSourceLocation().Build();
    public static string Prepare(string source)
    {
        // The native renderer handles blocks and inline formatting, but has no task-list control.
        // Parse GFM task markers so code fences and ordinary bracketed text stay untouched.
        var parsed = Markdig.Markdown.Parse(source, Pipeline);
        foreach (var task in parsed.Descendants<TaskList>().OrderByDescending(t => t.Span.Start))
            source = source.Remove(task.Span.Start, task.Span.Length).Insert(task.Span.Start, task.Checked ? "☑" : "☐");
        return source;
    }
}
