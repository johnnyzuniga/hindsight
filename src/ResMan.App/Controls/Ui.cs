using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ResMan.App.Controls;

/// <summary>
/// Small builders for rows made in code. Everything is styled through Theme.xaml styles, whose
/// ThemeResource setters keep repainting when the user switches Light/Dark.
/// </summary>
public static class Ui
{
    public static Style S(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock T(string text, string style = "Body", double? size = null)
    {
        var t = new TextBlock { Text = text, Style = S(style) };
        if (size is { } s) t.FontSize = s;
        return t;
    }

    public static Border Rule(bool strong = false, UIElement? child = null, Thickness? padding = null) =>
        new() { Style = S(strong ? "StrongRule" : "RowRule"), Child = child, Padding = padding ?? new Thickness(0, 11, 0, 11) };

    public static Grid Columns(params GridLength[] widths)
    {
        var g = new Grid { ColumnSpacing = 14 };
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }

    public static T At<T>(this T el, int column, int row = 0) where T : FrameworkElement
    {
        Grid.SetColumn(el, column);
        Grid.SetRow(el, row);
        return el;
    }

    public static Grid Add(this Grid g, params FrameworkElement[] children)
    {
        foreach (var c in children) g.Children.Add(c);
        return g;
    }

    public static GridLength Star(double v = 1) => new(v, GridUnitType.Star);
    public static GridLength Px(double v) => new(v);
    public static readonly GridLength Auto = GridLength.Auto;

    /// <summary>A horizontal bar: track plus a filled share (0..1).</summary>
    public static Grid Bar(double share, bool signal = false, double height = 10)
    {
        var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(Math.Clamp(share, 0.004, 1)) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(Math.Max(0, 1 - share)) });
        g.Children.Add(new Border { Style = S("TrackFill") }.At(0).Also(b => Grid.SetColumnSpan(b, 2)));
        g.Children.Add(new Border { Style = S(signal ? "SignalFill" : "InkFill") }.At(0));
        return g;
    }

    public static T Also<T>(this T x, Action<T> a) { a(x); return x; }

    /// <summary>Fade and lift in, with a delay for staggering. Skipped when Windows animations are off.</summary>
    public static void Enter(UIElement el, int delayMs = 0)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var move = new TranslateTransform { Y = 10 };
        el.RenderTransform = move;
        el.Opacity = 0;
        var sb = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(420), BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        var lift = new DoubleAnimation
        {
            From = 10, To = 0, Duration = TimeSpan.FromMilliseconds(420), BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, el);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard.SetTarget(lift, move);
        Storyboard.SetTargetProperty(lift, "Y");
        sb.Children.Add(fade);
        sb.Children.Add(lift);
        sb.Begin();
    }

    /// <summary>Smoothly recolors a band when the PC's health changes.</summary>
    public static void Recolor(Border band, Windows.UI.Color to)
    {
        if (band.Background is not SolidColorBrush b) { band.Background = new SolidColorBrush(to); return; }
        if (b.Color == to) return;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) { b.Color = to; return; }
        var anim = new ColorAnimation { To = to, Duration = TimeSpan.FromMilliseconds(400), EnableDependentAnimation = true };
        Storyboard.SetTarget(anim, b);
        Storyboard.SetTargetProperty(anim, "Color");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }
}
