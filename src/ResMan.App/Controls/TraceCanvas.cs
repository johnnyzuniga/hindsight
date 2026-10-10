using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ResMan.App.Controls;

/// <summary>One line on a trace. X runs 0..1 across the width; Y is in the chart's units (0..YMax).</summary>
public sealed record TraceSeries(IReadOnlyList<Point> Points, Brush Stroke, double Thickness)
{
    public bool Dotted { get; init; }
    public Brush? Fill { get; init; }
    /// <summary>Above this Y the line is redrawn in <see cref="HotStroke"/>: "memory crossed 90%".</summary>
    public double? HotAbove { get; init; }
    public Brush? HotStroke { get; init; }
    public bool Animate { get; init; }
}

/// <summary>
/// Plain polyline charts (no chart library): the hero memory trace, the small CPU/disk traces and the replay.
/// Redraws on resize; callers re-supply series when the theme changes.
/// </summary>
public sealed partial class TraceCanvas : Canvas
{
    private IReadOnlyList<TraceSeries> _series = [];
    private bool _animateNext;

    public double YMax { get; set; } = 100;
    public double? Threshold { get; set; }
    public Brush? ThresholdStroke { get; set; }

    public TraceCanvas()
    {
        SizeChanged += (_, _) => Redraw(false);
        IsHitTestVisible = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public void SetSeries(IReadOnlyList<TraceSeries> series, bool animate)
    {
        _series = series;
        _animateNext = animate && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        Redraw(_animateNext);
    }

    private Point Map(Point p) => new(p.X * ActualWidth, ActualHeight - Math.Clamp(p.Y / YMax, 0, 1) * ActualHeight);

    private void Redraw(bool animate)
    {
        Children.Clear();
        if (ActualWidth < 4 || ActualHeight < 4) return;
        Clip = new RectangleGeometry { Rect = new Rect(0, -4, ActualWidth, ActualHeight + 8) }; // never draw outside the chart
        if (Threshold is { } t && ThresholdStroke is not null)
        {
            var y = Map(new Point(0, t)).Y;
            Children.Add(new Line { X1 = 0, X2 = ActualWidth, Y1 = y, Y2 = y, Stroke = ThresholdStroke, StrokeThickness = 1, StrokeDashArray = [3, 4], Opacity = 0.6 });
        }
        foreach (var s in _series)
            foreach (var segment in Segments(s.Points))
            {
                if (segment.Count < 2) continue;
                var pts = segment.Select(Map).ToList();
                if (s.Fill is not null)
                {
                    var poly = new Polygon { Fill = s.Fill };
                    foreach (var p in pts) poly.Points.Add(p);
                    poly.Points.Add(new Point(pts[^1].X, ActualHeight));
                    poly.Points.Add(new Point(pts[0].X, ActualHeight));
                    Children.Add(poly);
                }
                var line = Polyline(pts, s.Stroke, s.Thickness, s.Dotted);
                Children.Add(line);
                if (animate && s.Animate) DrawIn(line, pts);
                if (s.HotAbove is { } hot && s.HotStroke is not null)
                    foreach (var run in RunsAbove(segment, hot))
                        Children.Add(Polyline(run.Select(Map).ToList(), s.HotStroke, s.Thickness + 0.75, false));
            }
    }

    /// <summary>A point with a NaN Y marks a gap (no recording then), so the line breaks instead of bridging it.</summary>
    public static readonly Point Gap = new(double.NaN, double.NaN);

    private static IEnumerable<List<Point>> Segments(IReadOnlyList<Point> points)
    {
        var current = new List<Point>();
        foreach (var p in points)
        {
            if (double.IsNaN(p.Y))
            {
                if (current.Count > 0) yield return current;
                current = [];
            }
            else current.Add(p);
        }
        if (current.Count > 0) yield return current;
    }

    private static Polyline Polyline(List<Point> pts, Brush stroke, double thickness, bool dotted)
    {
        var line = new Polyline { Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        if (dotted) line.StrokeDashArray = [0.5, 2.5];
        foreach (var p in pts) line.Points.Add(p);
        return line;
    }

    /// <summary>The one orchestrated motion on the Overview: the trace draws itself in.</summary>
    private static void DrawIn(Polyline line, List<Point> pts)
    {
        var length = pts.Zip(pts.Skip(1), (a, b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y))).Sum() / line.StrokeThickness;
        line.StrokeDashArray = [length + 1, length + 1];
        line.StrokeDashOffset = length;
        var anim = new DoubleAnimation
        {
            From = length, To = 0, Duration = TimeSpan.FromMilliseconds(1600), EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, line);
        Storyboard.SetTargetProperty(anim, "StrokeDashOffset");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) => line.StrokeDashArray = null;
        sb.Begin();
    }

    /// <summary>Contiguous stretches above a level, with the crossing points interpolated.</summary>
    private static IEnumerable<List<Point>> RunsAbove(IReadOnlyList<Point> pts, double level)
    {
        List<Point>? run = null;
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (p.Y >= level)
            {
                if (run is null)
                {
                    run = [];
                    if (i > 0) run.Add(Cross(pts[i - 1], p, level));
                }
                run.Add(p);
            }
            else if (run is not null)
            {
                run.Add(Cross(pts[i - 1], p, level));
                yield return run;
                run = null;
            }
        }
        if (run is { Count: > 1 }) yield return run;
    }

    private static Point Cross(Point a, Point b, double level)
    {
        if (Math.Abs(b.Y - a.Y) < 1e-9) return b;
        var t = (level - a.Y) / (b.Y - a.Y);
        return new Point(a.X + t * (b.X - a.X), level);
    }
}
