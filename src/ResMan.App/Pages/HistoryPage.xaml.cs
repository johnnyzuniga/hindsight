using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ResMan.App.Services;
using Windows.System;
using static ResMan.App.Controls.Ui;

namespace ResMan.App.Pages;

/// <summary>The longer view: fix outcomes, what changed on the PC, drive health, and the helper report.</summary>
public sealed partial class HistoryPage : Page, IRefreshable, IBanded
{
    private readonly SolidColorBrush _bandText = new();
    private readonly SolidColorBrush _bandAccent = new();

    public HistoryPage()
    {
        InitializeComponent();
        foreach (var t in new[] { BandLabel, Headline, IncidentNum, IncidentLabel, ChangeNum, ChangeLabel }) t.Foreground = _bandText;
        foreach (var t in new[] { BandLabel, IncidentNum, ChangeNum }) t.Foreground = _bandAccent;
        ActualThemeChanged += (_, _) => PaintBand();
        PaintBand();
    }

    public string BandLevel => "neutral";

    private void PaintBand()
    {
        var (band, text, accent) = Palette.Band(this, "neutral");
        Recolor(Band, band);
        App.Window?.RefreshCaption();
        _bandText.Color = text;
        _bandAccent.Color = accent;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await RefreshAsync();

    public async Task RefreshAsync()
    {
        try
        {
            var timeline = (await App.Agent.GetAsync("timeline?days=30")).Items().ToList();
            var changes = await App.Agent.GetAsync("changes?days=30");
            var fixes = (await App.Agent.GetAsync("fixes")).Items().ToList();
            var drives = (await App.Agent.GetAsync("drives")).Items().ToList();

            IncidentNum.Text = timeline.Sum(t => t.Num("count") ?? 1).ToString("0", CultureInfo.CurrentCulture);
            var groups = changes.Items("groups").ToList();
            ChangeNum.Text = groups.Count.ToString(CultureInfo.CurrentCulture);
            ChangeLabel.Text = groups.Count == 1 ? "change" : "changes";

            Fixes.Children.Clear();
            if (fixes.Count == 0)
                Fixes.Children.Add(Rule(strong: true, child: T("When you fix something, press “I fixed this” on the Overview. Hindsight then checks whether the problem really stopped.", "Muted")));
            foreach (var f in fixes)
            {
                var verdict = f.Str("verdict");
                var g = Columns(Star(), Auto);
                var left = new StackPanel { Spacing = 2 };
                left.Children.Add(T(f["fix"].Str("label") ?? "A fix", "ItemTitle", 16));
                left.Children.Add(T(f.Str("summary") ?? "", "Muted"));
                g.Add(left.At(0));
                var number = verdict == "too_early" ? "…" : $"{f.Num("beforePerWeek"):0.#} → {f.Num("afterPerWeek"):0.#}";
                g.Add(T(number, verdict is "worse" or "no_change" ? "SignalNumeral" : "SmallNumeral").Also(t => t.VerticalAlignment = VerticalAlignment.Center).At(1));
                Fixes.Children.Add(Rule(strong: Fixes.Children.Count == 0, child: g));
            }

            Changes.Children.Clear();
            foreach (var c in groups.Take(12))
            {
                var g = Columns(Px(110), Star());
                var when = c.Time("timeUtc") is { } t ? $"{Json.Day(t)}\n{Json.Clock(t)}" : "";
                g.Add(T(when, "Mono").At(0));
                g.Add(T(c.Str("description") ?? "", "Body", 15).At(1));
                Changes.Children.Add(Rule(strong: Changes.Children.Count == 0, child: g));
            }
            if (groups.Count == 0) Changes.Children.Add(Rule(strong: true, child: T("Nothing was installed, updated or removed in the last 30 days.", "Muted")));
            var routine = changes.Num("routineHidden") ?? 0;
            RoutineNote.Text = routine > 0 ? $"{routine:0} routine antivirus updates hidden." : "";

            Drives.Children.Clear();
            foreach (var d in drives)
            {
                var healthy = string.Equals(d.Str("healthStatus"), "Healthy", StringComparison.OrdinalIgnoreCase);
                var g = Columns(Star(), Auto);
                var left = new StackPanel { Spacing = 2 };
                left.Children.Add(T(d.Str("model") ?? "Drive", "ItemTitle", 16));
                var bits = new List<string>();
                if (d.Num("wearPercent") is { } w) bits.Add($"{w:0}% worn");
                if (d.Num("temperatureC") is { } c) bits.Add($"{c:0}°C");
                if (d.Num("readErrorsTotal") is { } re && re > 0) bits.Add($"{re:0} read errors");
                if (bits.Count == 0) bits.Add("Wear and error counters need Hindsight to run as administrator");
                left.Children.Add(T(string.Join(" · ", bits), "Muted"));
                g.Add(left.At(0));
                g.Add(T(healthy ? "Healthy" : d.Str("healthStatus") ?? "Unknown", healthy ? "ItemTitle" : "SignalNumeral", 16).Also(t => t.VerticalAlignment = VerticalAlignment.Center).At(1));
                Drives.Children.Add(Rule(strong: Drives.Children.Count == 0, child: g));
            }
            if (drives.Count == 0) Drives.Children.Add(Rule(strong: true, child: T("No drive readings yet.", "Muted")));
        }
        catch (AgentException ex) { Headline.Text = ex.Message; }
    }

    private async void OnReport(object sender, RoutedEventArgs e)
    {
        ReportButton.IsEnabled = false;
        try
        {
            var file = await App.Agent.SaveReportAsync();
            await Launcher.LaunchUriAsync(new Uri(file));
            ReportNote.Text = "Opened in your browser. Use Print, then Save as PDF to keep a copy.";
        }
        catch (AgentException ex) { ReportNote.Text = ex.Message; }
        finally
        {
            ReportNote.Visibility = Visibility.Visible;
            ReportButton.IsEnabled = true;
        }
    }
}
