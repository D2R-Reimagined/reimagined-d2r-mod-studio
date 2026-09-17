using Avalonia.Controls;
using ModStudio.Core;

namespace ModStudio.App;

/// <summary>Feeds <see cref="TextChecks"/> (length against the game's 255-character cell limit, bracket balance) from a TextBox to a status line as the text is typed.</summary>
internal static class EditorTextInfo
{
    public static string Describe(string? text) => TextChecks.Describe(text);

    /// <summary>Calls <paramref name="changed"/> now and for every keystroke in the box until it leaves the tree.</summary>
    public static void Attach(TextBox box, Action changed)
    {
        void OnText(object? sender, TextChangedEventArgs e) => changed();
        box.TextChanged += OnText;
        box.DetachedFromVisualTree += (_, _) => box.TextChanged -= OnText;
        changed();
    }
}
