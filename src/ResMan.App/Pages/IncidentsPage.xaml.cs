using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ResMan.App.Controls;
using ResMan.App.Services;
using Windows.Foundation;
using static ResMan.App.Controls.Ui;

namespace ResMan.App.Pages;

/// <summary>Every recorded incident, and the black-box minute before the selected one.</summary>
public sealed partial class IncidentsPage : Page, IRefreshable, IBanded
{
    private readonly SolidColorBrush _bandText = new();
    private readonly SolidColorBrush _bandAccent = new();
    private readonly DispatcherTimer _play = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private List<JsonNode> _samples = [];
    private List<(double T, string What)> _moments = [];
    private long? _eventId;
    private string? _level;
    private int _playhead;

    public string BandLevel => _level ?? "neutral";

    public IncidentsPage()
    {
        InitializeComponent();
        foreach (var t in new[] { BandLabel, Headline, TimeNum, TimeLabel }) t.Foreground = _bandText;
        foreach (var t in new[] { BandLabel, TimeNum }) t.Foreground = _bandAccent;
        ActualThemeChanged += (_, _) => { PaintBand(); DrawChart(false); };
        ChartArea.SizeChanged += (_, _) => PlaceMarkers();
        _play.Tick += (_, _) =>
        {
            if (_playhead >= _samples.Count - 1) { StopPlay(); return; }
            MoveTo(_playhead + 1);
        };
        Unloaded += (_, _) => _play.Stop();
        PaintBand();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _eventId = e.Parameter as long?;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            // "Wi-Fi connected" is good news, not an incident.
            var timeline = (await App.Agent.GetAsync("timeline?days=7")).Items().Where(t => t.Str("kind") is not ("wifi_connect" or "noise")).ToList();
            List.Children.Clear();
            ListEmpty.Visibility = timeline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var entry in timeline)
            {
                var id = entry.Items("eventIds").Select(x => x.GetValue<long>()).FirstOrDefault();
                var count = (int)(entry.Num("count") ?? 1);
                var when = entry.Time("timeUtc") is { } t ? $"{Json.Day(t)}, {Json.Clock(t)}" : "";
                var content = new StackPanel { Spacing = 2 };
                content.Children.Add(T(entry.Str("title") ?? "", "ItemTitle", 15));
                content.Children.Add(T(count > 1 ? $"{when} · ×{count}" : when, "Muted", 13));
                var b = new Button { Style = S("LinkButtonPlain"), Content = content, Tag = id, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                b.Click += async (_, _) => await SelectAsync(id);
                List.Children.Add(Rule(child: b, padding: new Thickness(0, 6, 0, 6)));
            }
            _eventId ??= timeline.FirstOrDefault()?.Items("eventIds").Select(x => (long?)x.GetValue<long>()).FirstOrDefault();
            if (_eventId is { } sel) await SelectAsync(sel);
            else
            {
                Headline.Text = "Nothing has gone wrong this week.";
                BandLabel.Text = "INCIDENTS · LAST 7 DAYS";
            }
        }
        catch (AgentException ex) { Headline.Text = ex.Message; }
    }

    private async Task SelectAsync(long id)
    {
        StopPlay();
        _eventId = id;
        foreach (var b in List.Children.OfType<Border>().Select(r => r.Child).OfType<Button>())
            b.Opacity = (long)b.Tag == id ? 1 : 0.62;
        try
        {
            var ev = await App.Agent.GetAsync($"events/{id}");
            var replay = await App.Agent.GetAsync($"events/{id}/replay");
            var title = ev.Str("title") ?? "Incident";
            var time = replay.Time("incidentUtc") ?? ev["event"].Time("timeUtc") ?? DateTime.UtcNow;
            var kind = ev["event"].Str("kind");
            _level = kind is "blue_screen" or "unexpected_shutdown" or "hardware_error" or "drive_health" ? "problem" : "attention";
            PaintBand();
            // The minute *before*: samples taken after the incident (the buffer runs on) would run off the chart.
            _samples = replay.Bool("available") ? replay.Items("samples").Where(s => (s.Num("tSeconds") ?? 0) <= 0.5).ToList() : [];
            BandLabel.Text = $"INCIDENT #{id} · {Json.Day(time).ToUpperInvariant()} · {time.ToLocalTime():h:mm:ss tt}";
            // Titles start with the app's name ("SketchPad stopped responding"), so they read naturally here.
            Headline.Text = _samples.Count > 1 ? $"The minute before {title}." : title + ".";
            var local = time.ToLocalTime();
            TimeNum.Text = local.ToString("h:mm", CultureInfo.CurrentCulture);
            TimeLabel.Text = local.ToString("tt · ddd d MMM", CultureInfo.CurrentCulture).ToUpperInvariant();
            PlayButton.Visibility = _samples.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            NoReplay.Visibility = _samples.Count > 1 ? Visibility.Collapsed : Visibility.Visible;
            NarrateButton.Visibility = _samples.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            Narration.Text = _samples.Count > 1 ? "Let the local AI tell the story of this minute." : ev.Str("detail") ?? "";
            NarrationLabel.Text = _samples.Count > 1 ? "WHAT HAPPENED, IN WORDS" : "WHAT WINDOWS RECORDED";
            _moments = [];
            Moments.Children.Clear();
            DrawChart(true);
            MoveTo(Math.Max(0, (int)(_samples.Count * 0.7)));
        }
        catch (AgentException ex) { Headline.Text = ex.Message; }
    }

    private void PaintBand()
    {
        var (band, text, accent) = Palette.Band(this, _level ?? "neutral");
        Recolor(Band, band);
        App.Window?.RefreshCaption();
        _bandText.Color = text;
        _bandAccent.Color = accent;
    }

    private double MinT => _samples.Count > 0 ? _samples.Min(s => s.Num("tSeconds") ?? 0) : -60;
    private double X(double t) => (t - MinT) / Math.Max(1, 0 - MinT);

    private void DrawChart(bool animate)
    {
        Chart.Threshold = 90;
        Chart.ThresholdStroke = Palette.Brush(this, "SignalBrush");
        if (_samples.Count < 2) { Chart.SetSeries([], false); Playhead.Visibility = Readout.Visibility = Visibility.Collapsed; return; }
        Playhead.Visibility = Readout.Visibility = Visibility.Visible;
        var ink = Palette.Brush(this, "InkBrush");
        var maxDisk = Math.Max(20, _samples.Max(s => s.Num("diskMbps") ?? 0));
        List<Point> Series(Func<JsonNode, double> y) => _samples.Select(s => new Point(X(s.Num("tSeconds") ?? 0), y(s))).ToList();
        Chart.SetSeries(
        [
            new TraceSeries(Series(s => (s.Num("diskMbps") ?? 0) / maxDisk * 100), Palette.Brush(this, "SecondaryBrush"), 1.6) { Dotted = true },
            new TraceSeries(Series(s => s.Num("cpu") ?? 0), ink, 1.1),
            new TraceSeries(Series(s => s.Num("memory") ?? 0), ink, 3.5) { HotAbove = 90, HotStroke = Palette.Brush(this, "SignalBrush"), Animate = true },
        ], animate);
        PlaceMarkers();
    }

    private void PlaceMarkers()
    {
        foreach (var m in Overlay.Children.OfType<Border>().Where(b => b.Tag as string == "moment").ToList()) Overlay.Children.Remove(m);
        foreach (var (t, what) in _moments)
        {
            var line = new Border { Width = 1, Height = ChartArea.ActualHeight, Style = S("SignalFill"), Opacity = 0.55, Tag = "moment" };
            ToolTipService.SetToolTip(line, what);
            Canvas.SetLeft(line, X(t) * ChartArea.ActualWidth);
            Overlay.Children.Insert(0, line);
        }
        if (_samples.Count > 1) MoveTo(_playhead);
    }

    private void MoveTo(int index)
    {
        if (_samples.Count < 2) return;
        _playhead = Math.Clamp(index, 0, _samples.Count - 1);
        var s = _samples[_playhead];
        var t = s.Num("tSeconds") ?? 0;
        var x = X(t) * ChartArea.ActualWidth;
        Playhead.Height = ChartArea.ActualHeight;
        Canvas.SetLeft(Playhead, x - 1);
        ReadoutText.Text = $"{t:0} s · memory {s.Num("memory"):0}% · CPU {s.Num("cpu"):0}%";
        Readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = Readout.DesiredSize.Width;
        Canvas.SetLeft(Readout, x + 8 + w > ChartArea.ActualWidth ? x - w - 8 : x + 8);
        Canvas.SetTop(Readout, 6);
        ProgramsLabel.Text = $"BIGGEST PROGRAMS AT {t:0} S";
        FillPrograms(s);
    }

    private void FillPrograms(JsonNode sample)
    {
        Programs.Children.Clear();
        // Browsers run many same-named processes; keep the largest of each name.
        var first = _samples[0].Items("top").GroupBy(p => p.Str("name") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(p => p.Num("workingSetMb") ?? 0), StringComparer.OrdinalIgnoreCase);
        var max = Math.Max(1, _samples.SelectMany(s => s.Items("top")).Where(p => !IsOwn(p.Str("name"))).Select(p => p.Num("workingSetMb") ?? 0).DefaultIfEmpty(0).Max());
        var grew = new List<string>();
        foreach (var p in sample.Items("top").Where(p => !IsOwn(p.Str("name"))).OrderByDescending(p => p.Num("workingSetMb")).Take(4))
        {
            var name = Friendly(p.Str("name"));
            var mb = p.Num("workingSetMb") ?? 0;
            var up = mb - first.GetValueOrDefault(p.Str("name") ?? "", 0) >= 300;
            if (up) grew.Add(name);
            var g = Columns(Px(96), Star(), Px(64));
            g.Add(T(name, up ? "ItemTitle" : "Body", 15).Also(x => { x.TextWrapping = TextWrapping.NoWrap; x.TextTrimming = TextTrimming.CharacterEllipsis; ToolTipService.SetToolTip(x, name); }).At(0));
            g.Add(Bar(mb / max, signal: up, height: 8).At(1));
            g.Add(T(mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB", "Mono").Also(x => { x.HorizontalAlignment = HorizontalAlignment.Right; x.VerticalAlignment = VerticalAlignment.Center; }).At(2));
            Programs.Children.Add(Rule(strong: Programs.Children.Count == 0, child: g, padding: new Thickness(0, 9, 0, 9)));
        }
        GrewNote.Text = grew.Count switch
        {
            0 => "Nothing grew much during this minute.",
            1 => $"Only {grew[0]} grew during the minute.",
            _ => $"{string.Join(" and ", grew)} grew during the minute.",
        };
    }

    /// <summary>ResMan's own processes (its AI included) are never evidence about the user's problem.</summary>
    private static bool IsOwn(string? name) => name is not null && name.StartsWith("ResMan", StringComparison.OrdinalIgnoreCase);

    private static string Friendly(string? name)
    {
        var n = name ?? "";
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    private void OnScrub(object sender, PointerRoutedEventArgs e)
    {
        if (_samples.Count < 2 || ChartArea.ActualWidth <= 0) return;
        StopPlay();
        var frac = Math.Clamp(e.GetCurrentPoint(ChartArea).Position.X / ChartArea.ActualWidth, 0, 1);
        var t = MinT + frac * (0 - MinT);
        var nearest = _samples.Select((s, i) => (i, d: Math.Abs((s.Num("tSeconds") ?? 0) - t))).MinBy(x => x.d).i;
        MoveTo(nearest);
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_play.IsEnabled) { StopPlay(); return; }
        if (_playhead >= _samples.Count - 1) MoveTo(0);
        // About 6 seconds for the whole minute, whatever the sample count.
        _play.Interval = TimeSpan.FromMilliseconds(Math.Max(30, 6000.0 / Math.Max(1, _samples.Count)));
        _play.Start();
        PlayIcon.Glyph = "";
    }

    private void StopPlay()
    {
        _play.Stop();
        PlayIcon.Glyph = "";
    }

    private async void OnNarrate(object sender, RoutedEventArgs e)
    {
        if (_eventId is not { } id) return;
        NarrateButton.IsEnabled = false;
        Narration.Text = "The local AI is looking at the recording…";
        var started = DateTime.UtcNow;
        try
        {
            // A narration takes 5-30 s; if the graphics card is busy with something else (a game), stop waiting.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var n = await App.Agent.PostAsync($"events/{id}/replay/narrate", null, cts.Token);
            Narration.Text = $"“{n.Str("narration")}”";
            NarrationLabel.Text = n.Bool("usedModel")
                ? $"TOLD BY THE LOCAL AI · {(n.Num("modelMilliseconds") ?? (DateTime.UtcNow - started).TotalMilliseconds) / 1000:0.0} S"
                : "FROM THE RECORDING · AI IS OFF";
            _moments = n.Items("keyMoments")
                .Select(m => (t: ParseT(m.Str("t")), what: m.Str("what") ?? ""))
                .Where(m => m.t is not null).Select(m => (m.t!.Value, m.what)).Take(4).ToList();
            FillMoments();
            PlaceMarkers();
        }
        catch (AgentException ex) { Narration.Text = ex.Message; }
        catch (OperationCanceledException) { Narration.Text = TooSlow; }
        finally { NarrateButton.IsEnabled = true; }
    }

    internal const string TooSlow = "The AI is taking much longer than usual, so I stopped waiting. Something else is probably using the graphics card heavily (a game, say). Try again once it's free.";

    private void FillMoments()
    {
        Moments.Children.Clear();
        Moments.ColumnDefinitions.Clear();
        for (var i = 0; i < _moments.Count; i++)
        {
            Moments.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
            var late = i >= _moments.Count - 2;
            var cell = new StackPanel { Spacing = 2 };
            cell.Children.Add(T($"{_moments[i].T:0}s", late ? "SignalNumeral" : "SmallNumeral").Also(t => t.FontSize = 26));
            cell.Children.Add(T(_moments[i].What, "Body", 14).Also(t => t.LineHeight = 20));
            var top = new Border { BorderThickness = new Thickness(0, 3, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = cell };
            top.BorderBrush = Palette.Brush(this, late ? "SignalBrush" : "InkBrush");
            Moments.Children.Add(top.At(i));
            Enter(top, i * 120);
        }
    }

    private static double? ParseT(string? t)
    {
        var m = Regex.Match(t ?? "", @"([-+−]?\d+(\.\d+)?)");
        return m.Success && double.TryParse(m.Groups[1].Value.Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Min(0, v) : null;
    }

    private void OnExplain(object sender, RoutedEventArgs e)
    {
        if (_eventId is { } id) App.Window?.Navigate("ask", new AskRequest(null, id));
    }
}
