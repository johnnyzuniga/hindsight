using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ResMan.App.Controls;
using ResMan.App.Services;
using Windows.System;
using static ResMan.App.Controls.Ui;

namespace ResMan.App.Pages;

/// <summary>What to investigate: a complaint in the user's words, or one recorded event.</summary>
public sealed record AskRequest(string? Symptom, long? EventId);

/// <summary>
/// The demo centerpiece: the local AI's investigation, step by step as it happens, then its answer
/// with evidence, fixes and follow-up questions.
/// </summary>
public sealed partial class AskPage : Page, IBanded
{
    private readonly SolidColorBrush _bandText = new();
    private readonly SolidColorBrush _bandAccent = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<int, Border> _rows = [];
    private string? _requestId, _sessionId, _level;
    private int _checks;

    public string BandLevel => _level ?? "neutral";
    private bool _busy, _gotSteps;

    private static readonly string[] Samples =
    [
        "My drawing app keeps freezing and closing",
        "Why did my internet drop?",
        "My PC randomly turned off last night",
        "Everything has been slow since yesterday",
    ];

    public AskPage()
    {
        InitializeComponent();
        foreach (var t in new[] { BandLabel, Headline, SecondsNum, SecondsLabel, StepsNum, StepsLabel }) t.Foreground = _bandText;
        foreach (var t in new[] { BandLabel, SecondsNum, StepsNum }) t.Foreground = _bandAccent;
        ActualThemeChanged += (_, _) => PaintBand();
        _tick.Tick += (_, _) => SecondsNum.Text = _clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture);
        App.Live.Message += OnLive;
        Unloaded += (_, _) => { App.Live.Message -= OnLive; _tick.Stop(); };
        foreach (var s in Samples)
        {
            var b = new Button { Style = S("LinkButtonPlain"), Content = $"“{s}” →", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Normal };
            b.Click += async (_, _) => await InvestigateAsync(new AskRequest(s, null));
            Examples.Children.Add(Rule(child: b, padding: new Thickness(0, 6, 0, 6)));
        }
        PaintBand();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await LoadRunningAsync();
        if (e.Parameter is AskRequest r) await InvestigateAsync(r);
    }

    private void PaintBand()
    {
        var (band, text, accent) = Palette.Band(this, _level ?? "neutral");
        Recolor(Band, band);
        App.Window?.RefreshCaption();
        _bandText.Color = text;
        _bandAccent.Color = accent;
        // While investigating, the label is the signal color; once answered it takes the state accent.
        BandLabel.Foreground = _level is null ? Palette.Brush(this, Palette.Dark(this) ? "SignalTextBrush" : "SignalBrush") : _bandAccent;
    }

    private async Task LoadRunningAsync()
    {
        try
        {
            var ai = await App.Agent.GetAsync("ai/status");
            var model = (ai.Str("model") ?? "local model").Replace(".gguf", "", StringComparison.OrdinalIgnoreCase);
            var backend = ai.Str("backend") is { } b ? b.Replace("gpu: ", "", StringComparison.OrdinalIgnoreCase) : "Graphics card, when it's loaded";
            var tps = ai["timings"].Num("tokensPerSecond");
            FillTable(RunningTable,
            [
                ("Model", model),
                ("Hardware", backend),
                ("Speed", tps is { } t ? $"{t:0} tokens/s" : "measured on first use"),
                ("Internet", "Not used"),
            ]);
        }
        catch (Exception ex) when (ex is AgentException or InvalidOperationException)
        {
            FillTable(RunningTable, [("Model", "not reachable"), ("Internet", "Not used")]);
        }
    }

    private static void FillTable(Grid g, (string Key, string Value)[] rows)
    {
        g.Children.Clear();
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = Star() });
        for (var i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = Auto });
            var line = new Border { Style = S(i == 0 ? "StrongRule" : "RowRule") }.At(0, i);
            Grid.SetColumnSpan(line, 2);
            g.Children.Add(line);
            g.Children.Add(T(rows[i].Key, "Muted").Also(t => t.Margin = new Thickness(0, 10, 16, 10)).At(0, i));
            g.Children.Add(T(rows[i].Value, "Body", 15).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.TextAlignment = TextAlignment.Right; t.Margin = new Thickness(0, 10, 0, 10); }).At(1, i));
        }
    }

    // ---- investigation ----

    private async Task InvestigateAsync(AskRequest req)
    {
        if (_busy) return;
        _busy = true;
        AskButton.IsEnabled = false;
        _requestId = Guid.NewGuid().ToString("N")[..10];
        _sessionId = null;
        _level = null;
        _checks = 0;
        _gotSteps = false;
        _rows.Clear();
        StepList.Children.Clear();
        Thread.Children.Clear();
        ErrorText.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Collapsed;
        AnswerPanel.Visibility = Visibility.Collapsed;
        FollowPanel.Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Visible;
        WorkPanel.Visibility = Visibility.Visible;
        Numbers.Visibility = Visibility.Visible;
        StepsNum.Text = "0";
        StepsLabel.Text = "checks, up to 5";
        SecondsLabel.Text = "seconds so far";
        BandLabel.Text = "INVESTIGATING · ON THIS PC";
        SetHeadline(req.Symptom is { } s ? $"“{s}”" : "What happened here?");
        PaintBand();
        _clock.Restart();
        _tick.Start();

        SlowNote.Visibility = Visibility.Collapsed;
        try
        {
            // The step feed is a WebSocket; make sure it's up so the first steps aren't missed.
            await App.Live.WaitConnectedAsync(TimeSpan.FromSeconds(2));
            await WarnIfBusyAsync();
            // A normal investigation takes 7-25 s; a starved graphics card can make it crawl, so cap the wait.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            JsonNode answer;
            if (req.EventId is { } id)
            {
                var ev = await App.Agent.GetAsync($"events/{id}");
                SetHeadline($"What happened: {ev.Str("title")}");
                answer = await App.Agent.PostAsync($"events/{id}/explain?requestId={_requestId}", null, cts.Token);
            }
            else answer = await App.Agent.PostAsync($"why?requestId={_requestId}", new { symptom = req.Symptom }, cts.Token);
            ShowAnswer(answer);
        }
        catch (AgentException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            BandLabel.Text = "COULDN'T FINISH";
        }
        catch (OperationCanceledException)
        {
            ErrorText.Text = IncidentsPage.TooSlow;
            ErrorText.Visibility = Visibility.Visible;
            BandLabel.Text = "TOOK TOO LONG";
        }
        finally
        {
            _tick.Stop();
            _busy = false;
            AskButton.IsEnabled = true;
            foreach (var pending in _rows.Where(r => r.Value.Tag as string == "pending").Select(r => r.Key).ToList())
            {
                StepList.Children.Remove(_rows[pending]);
                _rows.Remove(pending);
            }
        }
    }

    /// <summary>
    /// The model shares the PC with everything else. When memory is nearly full (a big game, say),
    /// say up front that answers will be slow rather than letting a step look stuck.
    /// </summary>
    private async Task WarnIfBusyAsync()
    {
        try
        {
            var h = await App.Agent.GetAsync("history?minutes=5");
            var latest = h.Items("samples").LastOrDefault();
            if (latest?.Num("memory") is { } mem && mem >= 90)
            {
                var status = await App.Agent.GetAsync("status");
                var hog = status["agent"].Str("profile") == "gaming" ? " A game is running." : "";
                SlowNote.Text = $"Memory is {mem:0}% full right now.{hog} The AI has to share it, so this will take longer than usual.";
                SlowNote.Visibility = Visibility.Visible;
            }
        }
        catch (AgentException) { }
    }

    private void OnLive(string type, JsonNode? data)
    {
        if (type != "investigation_step" || data.Str("requestId") is not { } rid) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (rid == _requestId) OnStep(data!, StepList, _rows, main: true);
            else if (rid == _followId) OnFollowStep(data!);
        });
    }

    private void OnStep(JsonNode s, StackPanel list, Dictionary<int, Border> rows, bool main)
    {
        _gotSteps = true;
        var step = (int)(s.Num("step") ?? 0);
        var kind = s.Str("kind");
        if (kind == "thinking")
        {
            if (rows.ContainsKey(step)) return;
            var row = StepRow(step, "Deciding what to check next…", null, null, pending: true);
            rows[step] = row;
            list.Children.Add(row);
            Enter(row);
            return;
        }
        if (rows.Remove(step, out var old)) list.Children.Remove(old);
        if (kind is "tool" or "repeat")
        {
            if (kind == "tool" && main) StepsNum.Text = (++_checks).ToString(CultureInfo.CurrentCulture);
            var row = StepRow(step, s.Str("label") ?? s.Str("tool") ?? "", s.Str("finding"), s.Num("ms"), pending: false, muted: kind == "repeat");
            rows[step] = row;
            list.Children.Add(row);
            Enter(row);
        }
    }

    private static Border StepRow(int step, string label, string? finding, double? ms, bool pending, bool muted = false)
    {
        var g = Columns(Px(52), Star(), Px(56));
        g.Add(T(step.ToString("00", CultureInfo.InvariantCulture), pending ? "SignalNumeral" : "SmallNumeral").Also(t => t.VerticalAlignment = VerticalAlignment.Top).At(0));
        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(T(label, "Muted"));
        if (finding is { Length: > 0 }) body.Children.Add(T(finding, "Body", 17).Also(t => { t.LineHeight = 25; if (muted) t.Opacity = 0.6; }));
        if (pending) body.Children.Add(new ProgressBar { Style = S("StepProgress"), IsIndeterminate = true });
        g.Add(body.At(1));
        g.Add(T(ms is { } m ? $"{m / 1000:0.0} s" : "", "Mono").Also(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.Margin = new Thickness(0, 4, 0, 0); }).At(2));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(g, $"Step {step}: {label}. {finding}");
        return Rule(strong: step == 1, child: g, padding: new Thickness(0, 14, 0, 14)).Also(b => b.Tag = pending ? "pending" : "done");
    }

    private void ShowAnswer(JsonNode a)
    {
        _sessionId = a.Str("sessionId");
        var seconds = _clock.Elapsed.TotalSeconds;
        var confidence = a.Str("confidence") ?? "low";
        var usedModel = a.Bool("usedModel");
        _level = !usedModel || confidence == "low" ? "neutral" : a.Bool("needsHuman") ? "problem" : "attention";
        PaintBand();

        var calls = (int)(a.Num("modelCalls") ?? 0);
        var trace = a.Items("toolTrace").Select(x => x.ToString()).ToList();
        if (!_gotSteps && trace.Count > 0)
        {
            // The live feed wasn't connected: show the raw trace instead of nothing.
            for (var i = 0; i < trace.Count; i++) StepList.Children.Add(StepRow(i + 1, trace[i], null, null, false));
        }
        _checks = Math.Max(_checks, trace.Count);
        BandLabel.Text = usedModel
            ? $"LIKELY CAUSE · {_checks} CHECK{(_checks == 1 ? "" : "S")} · {seconds:0.0} S · {confidence.ToUpperInvariant()} CONFIDENCE"
            : "FROM RESMAN'S BUILT-IN CHECKS · AI IS OFF";
        var cause = a.Str("likelyCause");
        SetHeadline(cause is { Length: > 3 } && !cause.StartsWith("Not sure", StringComparison.OrdinalIgnoreCase) ? cause : a.Str("summary") ?? "");
        SecondsNum.Text = seconds.ToString("0.0", CultureInfo.CurrentCulture);
        SecondsLabel.Text = "seconds";
        StepsNum.Text = _checks.ToString(CultureInfo.CurrentCulture);
        StepsLabel.Text = _checks == 1 ? "check" : "checks";

        Summary.Text = a.Str("summary") ?? "";
        EvidenceList.Children.Clear();
        foreach (var ev in a.Strings("evidence"))
            EvidenceList.Children.Add(Rule(child: T(ev, "Body", 15), padding: new Thickness(0, 9, 0, 9)));
        EvidenceSection.Visibility = EvidenceList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        FixSteps.Children.Clear();
        var n = 0;
        foreach (var step in a.Strings("steps"))
        {
            var g = Columns(Px(40), Star());
            g.Add(T((++n).ToString(CultureInfo.CurrentCulture), "SmallNumeral").Also(t => t.FontSize = 28).At(0));
            g.Add(T(step, "Body", 17).Also(t => t.VerticalAlignment = VerticalAlignment.Center).At(1));
            FixSteps.Children.Add(Rule(strong: n == 1, child: g, padding: new Thickness(0, 8, 0, 8)));
        }
        Actions.Children.Clear();
        var first = true;
        // Only shortcuts that open something become buttons; plain advice is already in the steps.
        foreach (var act in a.Items("actions"))
        {
            if (act.Str("uri") is not { } uri || !Uri.TryCreate(uri, UriKind.Absolute, out var u)) continue;
            var b = new Button { Style = S(first ? "PrimaryButton" : "OutlineButton"), Content = act.Str("label") };
            first = false;
            b.Click += async (_, _) => await Launcher.LaunchUriAsync(u);
            Actions.Children.Add(b);
        }
        StepsSection.Visibility = FixSteps.Children.Count + Actions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Note.Text = a.Str("note") ?? "";
        Note.Visibility = string.IsNullOrEmpty(Note.Text) ? Visibility.Collapsed : Visibility.Visible;

        AnswerPanel.Visibility = Visibility.Visible;
        WorkPanel.Visibility = Visibility.Collapsed;
        ShowWork.Content = $"Show how the AI worked this out ({_checks} check{(_checks == 1 ? "" : "s")})";
        ShowWork.Visibility = StepList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Collapsed;
        FollowPanel.Visibility = _sessionId is null || !usedModel ? Visibility.Collapsed : Visibility.Visible;
        Enter(AnswerPanel);
        Scroller.ChangeView(null, 0, null);
    }

    /// <summary>The model's sentences vary in length; step the size down so the band stays at most three lines.</summary>
    private void SetHeadline(string text)
    {
        Headline.Text = text;
        (Headline.FontSize, Headline.LineHeight) = text.Length switch
        {
            <= 70 => (44d, 46d),
            <= 120 => (36d, 39d),
            _ => (30d, 34d),
        };
    }

    private void OnShowWork(object sender, RoutedEventArgs e)
    {
        var show = WorkPanel.Visibility != Visibility.Visible;
        WorkPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ShowWork.Content = show ? "Hide the AI's steps" : $"Show how the AI worked this out ({_checks} check{(_checks == 1 ? "" : "s")})";
    }

    // ---- follow-up ----

    private string? _followId;

    private async Task FollowAsync()
    {
        var q = FollowBox.Text.Trim();
        if (q.Length == 0 || _sessionId is null || _busy) return;
        _busy = true;
        FollowBox.Text = "";
        _followId = Guid.NewGuid().ToString("N")[..10];
        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(Rule(strong: true, child: T(q, "ItemTitle", 16), padding: new Thickness(0, 12, 0, 0)));
        Thread.Children.Add(block);
        FollowStatus.Text = "THINKING…";
        FollowStatus.Visibility = Visibility.Visible;
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var a = await App.Agent.PostAsync($"followup?requestId={_followId}", new { sessionId = _sessionId, question = q }, cts.Token);
            block.Children.Add(T(a.Str("summary") ?? "", "Body", 21).Also(t => { t.FontWeight = Microsoft.UI.Text.FontWeights.Bold; t.LineHeight = 27; t.CharacterSpacing = -15; }));
            foreach (var step in a.Strings("steps").Take(3)) block.Children.Add(T(step, "Muted"));
            var looked = a.Items("toolTrace").Count();
            block.Children.Add(T(looked > 0 ? $"CHECKED {looked} MORE THING{(looked == 1 ? "" : "S")} · {sw.Elapsed.TotalSeconds:0.0} S"
                : $"ANSWERED FROM WHAT IT ALREADY FOUND · {sw.Elapsed.TotalSeconds:0.0} S", "Mono").Also(t => t.FontSize = 11));
            Enter(block);
        }
        catch (AgentException ex)
        {
            block.Children.Add(T(ex.Message, "Muted"));
        }
        catch (OperationCanceledException)
        {
            block.Children.Add(T(IncidentsPage.TooSlow, "Muted"));
        }
        finally
        {
            FollowStatus.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }

    private void OnFollowStep(JsonNode s)
    {
        if (s.Str("kind") == "tool") FollowStatus.Text = $"CHECKING: {s.Str("label")?.ToUpperInvariant()}";
    }

    private async void OnFollow(object sender, RoutedEventArgs e) => await FollowAsync();

    private async void OnFollowKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; await FollowAsync(); }
    }

    private async void OnAsk(object sender, RoutedEventArgs e) => await AskFromBoxAsync();

    private async void OnAskKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; await AskFromBoxAsync(); }
    }

    private async Task AskFromBoxAsync()
    {
        var q = AskBox.Text.Trim();
        if (q.Length == 0) return;
        AskBox.Text = "";
        await InvestigateAsync(new AskRequest(q, null));
    }
}
