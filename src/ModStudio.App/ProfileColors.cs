using Avalonia.Media;

namespace ModStudio.App;

/// <summary>
/// The color a profile is shown in: the "color" its profile.json sets, else one picked from its name, so every view (the
/// picker, cell markers, the profile view's tint) agrees without anyone choosing.
/// </summary>
internal static class ProfileColors
{
    /// <summary>Distinct on the dark theme and clear of the selection gold and the stale red.</summary>
    public static readonly string[] Palette = ["#4C9BE8", "#59B36B", "#B07FE0", "#E07FA8", "#3FB5B0", "#9BC15A", "#7E8FD6", "#C98A5B"];
    public static readonly Color Stale = Color.Parse("#E5534B");

    public static Color For(string id, string? configured)
    {
        if (configured != null && Color.TryParse(configured, out var parsed)) return parsed;
        int hash = 17; foreach (var c in id) hash = unchecked(hash * 31 + c);
        return Color.Parse(Palette[(int)((uint)hash % Palette.Length)]);
    }
    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    public static IBrush Brush(Color color, byte alpha = 255) => new SolidColorBrush(WithAlpha(color, alpha));
}
