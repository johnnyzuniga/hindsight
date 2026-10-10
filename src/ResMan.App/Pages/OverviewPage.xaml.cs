using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ResMan.App.Controls;
using ResMan.App.Services;
using Windows.Foundation;
using Windows.System;
using static ResMan.App.Controls.Ui;

namespace ResMan.App.Pages;

public sealed partial class OverviewPage : Page, IRefreshable, IBanded
{
    private readonly SolidColorBrush _bandText = new();
    private readonly SolidColorBrush _bandAccent = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private JsonNode? _status, _history;
    private bool _drawnOnce;

    public string BandLevel => _status?["health"].Str("level") ?? "neutral";

    public OverviewPage()
    {
        InitializeComponent();
        foreach (var t in new[] { BandLabel, BandRight, Headline, Detail, CrashNum, CrashLabel, FreezeNum, FreezeLabel, MemNum, MemLabel, ThresholdLabel, EmptyTrace })
            t.Foreground = _bandText;
        foreach (var t in new[] { BandLabel, CrashNum, FreezeNum, MemNum, ThresholdLabel }) t.Foreground = _bandAccent;
        MarkerRule.BorderBrush = _bandAccent;
        ActualThemeChanged += (_, _) => Paint(animate: false);
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await RefreshAsync();

    public async Task RefreshAsync()
    {
        try
        {
            var status = App.Agent.GetAsync("status");
            var history = App.Agent.GetAsync("history?minutes=60");
            var today = App.Agent.GetAsync("timeline?days=1");
            var issues = App.Agent.GetAsync("issues");
            var fixes = App.Agent.GetAsync("fixes");
            var drives = App.Agent.GetAsync("drives");
            _status = await status;
            _history = await history;
            ErrorText.Visibility = Visibility.Collapsed;

            var day = (await today).Items().ToList();
            CrashNum.Text = day.Where(t => t.Str("kind") == "app_crash").Sum(t => t.Num("count") ?? 1).ToString("0", CultureInfo.CurrentCulture);
            FreezeNum.Text = day.Where(t => t.Str("kind") == "app_hang").Sum(t => t.Num("count") ?? 1).ToString("0", CultureInfo.CurrentCulture);
            CrashLabel.Text = CrashNum.Text == "1" ? "crash in 24 h" : "crashes in 24 h";
            FreezeLabel.Text = FreezeNum.Text == "1" ? "freeze in 24 h" : "freezes in 24 h";
            var latest = _history.Items("samples").LastOrDefault();
            MemNum.Text = latest?.Num("memory") is { } m ? $"{m:0}%" : "–";

            FillOpen((await issues).Items().Take(3).ToList());
            FillFix((await fixes).Items().ToList(), (await drives).Items().ToList());
            Paint(animate: !_drawnOnce);
            _drawnOnce = true;
        }
        catch (AgentException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            Headline.Text = "Hindsight isn't running yet.";
            Detail.Text = "Start the Hindsight service and agent, then this page fills in by itself.";
            Paint(animate: false);
        }
        catch (Exception ex) { Log.Write($"Overview: {ex}"); }
    }

    private void Paint(bool animate)
    {
        var health = _status?["health"];
        var level = health.Str("level");
        var (band, text, accent) = Palette.Band(this, level ?? "neutral");
        Recolor(Band, band);
        App.Window?.RefreshCaption();
        _bandText.Color = text;
        _bandAccent.Color = accent;

        var now = DateTime.Now.ToString("ddd d MMM, h:mm tt", CultureInfo.CurrentCulture).ToUpperInvariant();
        BandLabel.Text = level switch
        {
            "good" => $"ALL QUIET · {now}",
            "problem" => $"NEEDS HELP · {now}",
            "attention" => $"NEEDS ATTENTION · {now}",
            _ => now,
        };
        if (health is not null)
        {
            Headline.Text = health.Str("headline") ?? "";
            Detail.Text = health.Str("detail") ?? "";
        }
        DrawTrace(accent, animate);
    }

    private void DrawTrace(Windows.UI.Color ink, bool animate)
    {
        var from = _history.Time("fromUtc") ?? DateTime.UtcNow.AddHours(-1);
        var to = _history.Time("toUtc") ?? DateTime.UtcNow;
        double X(DateTime t) => Math.Clamp((t - from).TotalSeconds / Math.Max(1, (to - from).TotalSeconds), 0, 1);

        var points = new List<Point>();
        DateTime? previous = null;
        foreach (var (t, m) in _history.Items("samples").Select(s => (t: s.Time("timeUtc"), m: s.Num("memory"))).Where(p => p.t is not null && p.m is not null))
        {
            // Samples arrive every 10 s; a longer silence means ResMan wasn't recording, so don't draw across it.
            if (previous is { } prev && (t!.Value - prev).TotalSeconds > 60) points.Add(TraceCanvas.Gap);
            points.Add(new Point(X(t!.Value), m!.Value));
            previous = t;
        }
        EmptyTrace.Visibility = points.Count < 2 && _history is not null ? Visibility.Visible : Visibility.Collapsed;

        var stroke = new SolidColorBrush(ink);
        Trace.Threshold = 90;
        Trace.ThresholdStroke = stroke;
        Trace.SetSeries([new TraceSeries(points, stroke, 3.5) { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(26, ink.R, ink.G, ink.B)), Animate = true }], animate);

        // Incident ticks: thin for crashes, wide for freezes, with the plain title as a tooltip.
        Markers.Children.Clear();
        var width = Markers.ActualWidth > 0 ? Markers.ActualWidth : MarkerRule.ActualWidth;
        foreach (var i in _history.Items("incidents"))
        {
            if (i.Time("timeUtc") is not { } t) continue;
            var hang = i.Str("kind") == "app_hang";
            var tick = new Border { Width = hang ? 9 : 3, Height = 13, Background = stroke };
            ToolTipService.SetToolTip(tick, $"{i.Str("title")} · {Json.Clock(t)}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tick, $"{i.Str("title")} at {Json.Clock(t)}");
            Canvas.SetLeft(tick, X(t) * width - tick.Width / 2);
            Canvas.SetTop(tick, 3);
            Markers.Children.Add(tick);
        }
        if (Markers.Children.Count > 0 && width <= 0) Markers.SizeChanged += RedrawMarkersOnce;

        Axis.Children.Clear();
        for (var k = 0; k <= 4; k++)
        {
            var label = new TextBlock
            {
                Text = Json.Clock(from + (to - from) * (k / 4.0)), Style = S("BandLabel"), FontSize = 11, Foreground = stroke, Opacity = 0.85,
                HorizontalAlignment = k == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Axis.Children.Add(label.At(k));
        }
    }

    private void RedrawMarkersOnce(object sender, SizeChangedEventArgs e)
    {
        Markers.SizeChanged -= RedrawMarkersOnce;
        DrawTrace(_bandAccent.Color, false);
    }

    private void FillOpen(List<JsonNode> issues)
    {
        OpenList.Children.Clear();
        if (issues.Count == 0)
        {
            OpenList.Children.Add(Rule(child: T("Nothing needs your attention. Hindsight keeps recording in the background.", "Muted")));
            return;
        }
        var delay = 0;
        foreach (var issue in issues)
        {
            var eventId = issue.Items("eventIds").Select(x => (long?)x.GetValue<long>()).FirstOrDefault();
            var row = Columns(Star(), Auto);
            row.RowDefinitions.Add(new RowDefinition { Height = Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = Auto });
            row.RowSpacing = 2;
            row.Add(T(issue.Str("title") ?? "", "ItemTitle").At(0, 0));
            var explanation = issue.Str("evidence") ?? issue.Str("explanation") ?? "";
            row.Add(T(FirstSentence(explanation), "Muted").At(0, 1));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, VerticalAlignment = VerticalAlignment.Top };
            var ask = new Button { Style = S("LinkButtonPlain"), Content = "Ask why →" };
            ask.Click += (_, _) => App.Window?.Navigate("ask", new AskRequest(null, eventId));
            actions.Children.Add(ask);
            if (eventId is { } id)
            {
                var replay = new Button { Style = S("LinkButtonPlain"), Content = "Details →" };
                replay.Click += (_, _) => App.Window?.Navigate("incidents", id);
                actions.Children.Add(replay);
            }
            var fixedIt = new Button { Style = S("LinkButtonPlain"), Content = "I fixed this", Opacity = 0.75 };
            fixedIt.Flyout = FixFlyout(issue.Str("id") ?? "", issue.Str("title") ?? "");
            actions.Children.Add(fixedIt);
            row.Add(actions.At(1, 0));
            var item = Rule(child: row);
            OpenList.Children.Add(item);
            Enter(item, delay);
            delay += 90;
        }
    }

    /// <summary>"What did you do?" Saved as a fix; ResMan then compares the problem's rate before and after.</summary>
    private Flyout FixFlyout(string issueId, string title)
    {
        var box = new TextBox { Style = S("AskBox"), PlaceholderText = "e.g. uninstalled PhotoSync", Width = 320 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "What did you do to fix it?");
        var save = new Button { Style = S("PrimaryButton"), Content = "Save", MinHeight = 40 };
        var note = T("", "Muted");
        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(T("What did you do?", "ItemTitle", 16));
        panel.Children.Add(T($"Hindsight will watch whether “{title}” stops happening.", "Muted"));
        panel.Children.Add(box);
        panel.Children.Add(save);
        panel.Children.Add(note);
        var flyout = new Flyout { Content = panel };
        save.Click += async (_, _) =>
        {
            var label = box.Text.Trim();
            if (label.Length == 0) { note.Text = "Describe what you did, in a few words."; return; }
            try
            {
                await App.Agent.PostAsync("fixes", new { issueId, label });
                flyout.Hide();
                await RefreshAsync();
            }
            catch (AgentException ex) { note.Text = ex.Message; }
        };
        return flyout;
    }

    private void FillFix(List<JsonNode> fixes, List<JsonNode> drives)
    {
        var win = fixes.Where(f => f.Str("verdict") is "fixed" or "better").OrderBy(f => f.Num("daysSince")).FirstOrDefault();
        if (win is not null)
        {
            GoodLabel.Text = "YOUR LAST FIX WORKED";
            FixRow.Visibility = Visibility.Visible;
            FixNum.Text = $"{win.Num("beforePerWeek"):0.#} → {win.Num("afterPerWeek"):0.#}";
            FixUnit.Text = "a week";
            FixText.Text = win.Str("summary") ?? "";
        }
        else
        {
            var pending = fixes.FirstOrDefault(f => f.Str("verdict") == "too_early");
            GoodLabel.Text = pending is null ? "EVERYTHING ELSE" : "CHECKING YOUR LAST FIX";
            FixRow.Visibility = Visibility.Collapsed;
            FixText.Text = pending?.Str("summary") ?? "";
        }
        var notes = new List<string>();
        if (drives.Count > 0)
            notes.Add(drives.All(d => string.Equals(d.Str("healthStatus"), "Healthy", StringComparison.OrdinalIgnoreCase)) ? "Drive healthy" : "A drive needs a look");
        if (_status?["health"].Num("hiddenWarnings") is > 0 and var hidden)
            notes.Add($"{hidden:0} harmless Windows warnings hidden");
        CalmText.Text = string.Join(" · ", notes);
    }

    private static string FirstSentence(string s)
    {
        var i = s.IndexOf(". ", StringComparison.Ordinal);
        return i > 0 && i < 140 ? s[..(i + 1)] : s.Length > 160 ? s[..157] + "..." : s;
    }

    private void OnAsk(object sender, RoutedEventArgs e) => Ask();

    private void OnAskKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; Ask(); }
    }

    private void Ask()
    {
        var q = AskBox.Text.Trim();
        if (q.Length > 0) App.Window?.Navigate("ask", new AskRequest(q, null));
    }
}
