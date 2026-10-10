using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ResMan.App.Services;
using static ResMan.App.Controls.Ui;

namespace ResMan.App.Pages;

/// <summary>The AI footprint, measured live: RAM, VRAM, speed, and the fact that nothing goes online.</summary>
public sealed partial class LocalAiPage : Page, IBanded
{
    private readonly SolidColorBrush _bandText = new();
    private readonly SolidColorBrush _bandAccent = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private double? _peakVramMb, _peakNowMb;

    public LocalAiPage()
    {
        InitializeComponent();
        foreach (var t in new[] { BandLabel, Headline, BytesNum, BytesLabel, TpsNum, TpsLabel }) t.Foreground = _bandText;
        foreach (var t in new[] { BandLabel, BytesNum, TpsNum }) t.Foreground = _bandAccent;
        ActualThemeChanged += (_, _) => PaintBand();
        _poll.Tick += async (_, _) => await RefreshAsync();
        Loaded += (_, _) => _poll.Start();
        Unloaded += (_, _) => _poll.Stop();
        PaintBand();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await RefreshAsync();

    public string BandLevel => "good";

    private void PaintBand()
    {
        var (band, text, accent) = Palette.Band(this, "good");
        Recolor(Band, band);
        App.Window?.RefreshCaption();
        _bandText.Color = text;
        _bandAccent.Color = accent;
    }

    private bool _refreshing;

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var ai = await App.Agent.GetAsync("ai/status");
            var state = ai.Str("state") ?? "unloaded";
            var t = ai["timings"];
            var side = ai["sidecar"];
            var me = ai["resman"];
            BandLabel.Text = state switch
            {
                "ready" => "AI READY · LOADED ON THIS PC",
                "busy" => "AI WORKING · ON THIS PC",
                "loading" => "AI WAKING UP…",
                _ => ai.Bool("enabled") ? "AI ASLEEP · LOADS WHEN YOU ASK" : "AI OFF · BUILT-IN CHECKS STILL WORK",
            };
            TpsNum.Text = t.Num("tokensPerSecond") is { } tps ? tps.ToString("0", CultureInfo.CurrentCulture) : "–";

            var without = me.Num("totalWithoutAiMb") ?? 0;
            var now = me.Num("totalNowMb") ?? without;
            // Keep the "while answering" figure even after the model unloads, so the comparison stays visible.
            if (now > without + 200) _peakNowMb = now;
            var answering = _peakNowMb ?? now;
            FillHog(without, answering, loaded: _peakNowMb is not null);

            if (side.Num("vramMb") is { } v && v > 0) _peakVramMb = v;
            // Windows can move a loaded model out of the graphics card's own memory when something else
            // (usually a game) needs it. The model still runs, many times slower; say so plainly.
            var loaded = state is "ready" or "busy";
            var paged = loaded && side.Num("vramMb") is { } vramNow && ai.Num("modelFileMb") is { } file && file > 0 && vramNow < file * 0.7;
            VramNote.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
            if (paged)
                VramNote.Text = $"Only {side.Num("vramMb") / 1024:0.0} GB of the AI is on the graphics card right now; Windows moved the rest into regular memory, " +
                                "so answers will be much slower than usual. This usually happens after a game. Restarting the PC fixes it.";
            VramNum.Text = _peakVramMb is { } pv ? (pv / 1024).ToString("0.0", CultureInfo.CurrentCulture) : "0";
            VramLabel.Text = _peakVramMb is null ? "GB · the model isn't loaded right now" : state is "ready" or "busy" ? "GB in use now" : "GB while it was loaded";

            var model = (ai.Str("model") ?? "").Replace(".gguf", "", StringComparison.OrdinalIgnoreCase);
            var rows = new List<(string, string)>
            {
                ("Model", model.Length > 0 ? model : "not found"),
                ("On disk", ai.Num("modelFileMb") is { } f and > 0 ? $"{f / 1024:0.0} GB" : "–"),
                ("Running on", ai.Str("backend")?.Replace("gpu: ", "", StringComparison.OrdinalIgnoreCase) ?? "graphics card, once loaded"),
                ("AI memory", side.Num("privateMb") is { } p ? $"{p / 1024:0.0} GB" : "none while asleep"),
                ("Load time", t.Num("loadMs") is { } l ? $"{l / 1000:0.0} s" : t.Num("coldStartMs") is { } c ? $"{c / 1000:0.0} s" : "–"),
                ("Last question", t.Num("lastQuestionMs") is { } q ? $"{q / 1000:0.0} s" : "–"),
                ("Lets go after", $"{ai.Num("idleUnloadSeconds") ?? 30:0} s idle"),
                ("Internet", "Not used"),
            };
            FillTable(Details, rows);
        }
        catch (AgentException ex)
        {
            BandLabel.Text = ex.Message.ToUpperInvariant();
        }
        finally { _refreshing = false; }
    }

    private void FillHog(double withoutMb, double answeringMb, bool loaded)
    {
        HogTable.Children.Clear();
        HogTable.RowDefinitions.Clear();
        HogTable.ColumnDefinitions.Clear();
        HogTable.ColumnDefinitions.Add(new ColumnDefinition { Width = Px(150) });
        HogTable.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
        HogTable.ColumnDefinitions.Add(new ColumnDefinition { Width = Px(110) });
        HogTable.ColumnSpacing = 14;
        var max = Math.Max(answeringMb, withoutMb);
        (string, double, bool)[] rows = [("Just recording", withoutMb, false), (loaded ? "While answering" : "Right now", answeringMb, true)];
        for (var i = 0; i < rows.Length; i++)
        {
            var (label, mb, big) = rows[i];
            HogTable.RowDefinitions.Add(new RowDefinition { Height = Auto });
            var rule = new Border { Style = S(i == 0 ? "StrongRule" : "RowRule") }.At(0, i);
            Grid.SetColumnSpan(rule, 3);
            HogTable.Children.Add(rule);
            HogTable.Children.Add(T(label, "Body").Also(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 14, 0, 14); }).At(0, i));
            HogTable.Children.Add(Bar(mb / max, height: 14).At(1, i));
            var value = mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
            HogTable.Children.Add(T(value, "SmallNumeral").Also(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }).At(2, i));
        }
    }

    private static void FillTable(Grid g, List<(string Key, string Value)> rows)
    {
        g.Children.Clear();
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
        for (var i = 0; i < rows.Count; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = Auto });
            var line = new Border { Style = S(i == 0 ? "StrongRule" : "RowRule") }.At(0, i);
            Grid.SetColumnSpan(line, 2);
            g.Children.Add(line);
            g.Children.Add(T(rows[i].Key, "Muted").Also(t => t.Margin = new Thickness(0, 10, 16, 10)).At(0, i));
            g.Children.Add(T(rows[i].Value, "Body", 15).Also(t => { t.TextAlignment = TextAlignment.Right; t.HorizontalAlignment = HorizontalAlignment.Right; t.Margin = new Thickness(0, 10, 0, 10); }).At(1, i));
        }
    }

    private async void OnPreload(object sender, RoutedEventArgs e)
    {
        PreloadButton.IsEnabled = false;
        PreloadNote.Visibility = Visibility.Visible;
        PreloadNote.Text = "Loading the model onto the graphics card…";
        try
        {
            var r = await App.Agent.PostAsync("ai/preload", new { minutes = 30 });
            PreloadNote.Text = r.Bool("ready") ? $"Ready in {(r.Num("tookMs") ?? 0) / 1000:0.0} s. It stays loaded for 30 minutes." : "The model couldn't load. Check that the model file is in place.";
        }
        catch (AgentException ex) { PreloadNote.Text = ex.Message; }
        finally { PreloadButton.IsEnabled = true; }
    }
}
