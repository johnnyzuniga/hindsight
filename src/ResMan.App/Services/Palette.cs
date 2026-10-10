using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ResMan.App.Services;

/// <summary>
/// Reads the theme tokens from Themes/Theme.xaml in code, for colors chosen at runtime
/// (the health band, chart strokes). Callers repaint on ActualThemeChanged.
/// </summary>
public static class Palette
{
    private static ResourceDictionary? _theme;

    private static ResourceDictionary Theme => _theme ??= Application.Current.Resources.MergedDictionaries
        .First(d => d.Source?.ToString().EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);

    private static ResourceDictionary For(FrameworkElement el) =>
        (ResourceDictionary)Theme.ThemeDictionaries[Dark(el) ? "Dark" : "Light"];

    public static bool Dark(FrameworkElement el) =>
        (el.XamlRoot?.Content as FrameworkElement ?? el).ActualTheme == ElementTheme.Dark;

    public static Brush Brush(FrameworkElement el, string key) => (Brush)For(el)[key];

    public static Color Color(FrameworkElement el, string key) => For(el)[key] is Color c ? c : ((SolidColorBrush)For(el)[key]).Color;

    /// <summary>
    /// Band color for a health level, the text color that stays readable on it, and the accent
    /// (label, numerals, hero trace). Light mode floods the band with the state color and the accent
    /// equals the text; dark mode tints the band near-black and lets the accent carry the state.
    /// </summary>
    public static (Color Band, Color Text, Color Accent) Band(FrameworkElement el, string? level)
    {
        var name = level switch { "good" => "Good", "problem" => "Problem", "attention" => "Attention", _ => "Neutral" };
        var band = Color(el, $"Band{name}Color");
        if (Dark(el))
            return (band, Microsoft.UI.ColorHelper.FromArgb(255, 0xEC, 0xEA, 0xE4), Color(el, $"Accent{name}Color"));
        var text = level switch
        {
            "attention" => Microsoft.UI.ColorHelper.FromArgb(255, 0x14, 0x14, 0x14),
            "good" or "problem" => Microsoft.UI.ColorHelper.FromArgb(255, 0xF5, 0xF1, 0xEA),
            _ => Color(el, "GroundColor"),
        };
        return (band, text, text);
    }
}
