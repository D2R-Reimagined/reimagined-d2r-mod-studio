using Avalonia.Threading;

namespace ModStudio.App;

public sealed partial class EditorPane
{
    /// <summary>
    /// Shows the source text with a line (1-based) on screen and the given span selected, so a text hit from Find in files lands
    /// on the match itself. Table documents switch to Source view the way the toolbar button does.
    /// </summary>
    public void ShowSourceLine(int line, int column = 0, int length = 0)
    {
        if (MarkdownPreview != null) { markdownHost!.IsVisible = false; MarkdownPreview.IsVisible = false; }
        if (Document.Table != null) { syncing = true; Source.Text = Document.Text.TrimStart('﻿'); syncing = false; Source.IsVisible = true; tableHost.IsVisible = false; UpdateNote(); }
        else if (!Source.IsVisible) { Source.IsVisible = true; Refresh(); }
        var lines = Source.Document.LineCount; if (lines == 0) return;
        var target = Source.Document.GetLineByNumber(Math.Clamp(line, 1, lines));
        int offset = target.Offset + Math.Clamp(column, 0, target.Length);
        Source.Select(offset, Math.Clamp(length, 0, target.EndOffset - offset));
        Source.TextArea.Caret.Offset = offset + Math.Clamp(length, 0, target.EndOffset - offset);
        Source.ScrollToLine(target.LineNumber);
        Dispatcher.UIThread.Post(() => { Source.ScrollToLine(target.LineNumber); Source.TextArea.Focus(); }, DispatcherPriority.Background);
    }
}
