using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ResMan.App.Pages;
using ResMan.App.Services;
using Windows.Graphics;

namespace ResMan.App;

public sealed partial class MainWindow : Window
{
    public ThemeService Theme { get; } = new();
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private long? _noticeEventId;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ResMan.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        if (AppWindow.Presenter is OverlappedPresenter p) { p.PreferredMinimumWidth = 1000; p.PreferredMinimumHeight = 680; }
        AppWindow.Resize(new SizeInt32(1320, 860));

        Theme.Changed += PaintThemeToggle;
        Theme.Changed += RefreshCaption;
        Theme.Attach(Root, AppWindow);
        ContentFrame.Navigated += (_, _) => RefreshCaption();

        App.Live.Message += OnLive;
        _status.Tick += async (_, _) => await RefreshStatusAsync();
        _status.Start();
        _noticeTimer.Tick += (_, _) => { Notice.Visibility = Visibility.Collapsed; _noticeTimer.Stop(); };
        _ = RefreshStatusAsync();

        Navigate("overview");
    }

    /// <summary>Navigates to a page by key, with an optional parameter (a question, or an event id).</summary>
    public void Navigate(string key, object? parameter = null)
    {
        var (page, nav) = key switch
        {
            "ask" => (typeof(AskPage), NavAsk),
            "incidents" => (typeof(IncidentsPage), NavIncidents),
            "history" => (typeof(HistoryPage), NavHistory),
            "ai" => (typeof(LocalAiPage), NavAi),
            _ => (typeof(OverviewPage), NavOverview),
        };
        nav.IsChecked = true;
        ContentFrame.Navigate(page, parameter);
    }

    private void OnNav(object sender, RoutedEventArgs e) => Navigate((string)((FrameworkElement)sender).Tag);

    private void OnTheme(object sender, RoutedEventArgs e) =>
        Theme.Set(Enum.Parse<ThemeMode>((string)((FrameworkElement)sender).Tag));

    private void PaintThemeToggle()
    {
        foreach (var (b, mode) in new[] { (ThemeSystem, ThemeMode.System), (ThemeLight, ThemeMode.Light), (ThemeDark, ThemeMode.Dark) })
        {
            var on = Theme.Mode == mode;
            b.Background = on ? Palette.Brush(Root, "InkBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            b.Foreground = Palette.Brush(Root, on ? "OnInkBrush" : "SecondaryBrush");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(b, on ? "selected" : "");
        }
    }

    /// <summary>
    /// The minimize/maximize/close buttons sit on top of each page's band, whose color changes with
    /// the PC's health and the theme. Keep them in the band's text color so they never disappear.
    /// </summary>
    public void RefreshCaption()
    {
        var level = (ContentFrame.Content as IBanded)?.BandLevel ?? "neutral";
        var fg = Palette.Band(Root, level).Text;
        var tb = AppWindow.TitleBar;
        Windows.UI.Color A(byte alpha) => Windows.UI.Color.FromArgb(alpha, fg.R, fg.G, fg.B);
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = A(0xA0);
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonHoverBackgroundColor = A(0x26);
        tb.ButtonPressedBackgroundColor = A(0x40);
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var s = await App.Agent.GetAsync("status");
            var ai = await App.Agent.GetAsync("ai/status");
            var state = ai.Str("state") switch
            {
                "ready" => "AI READY",
                "busy" => "AI WORKING",
                "loading" => "AI WAKING UP",
                _ => ai["enabled"]?.GetValue<bool>() == false ? "AI OFF" : "AI ASLEEP",
            };
            StatusText.Text = $"RECORDING\nHINDSIGHT {s["footprint"].Num("totalMb"):0} MB\n{state}\nNOTHING LEAVES THIS PC";
        }
        catch (AgentException)
        {
            StatusText.Text = "HINDSIGHT ISN'T RUNNING\nSTART IT FROM THE TRAY";
        }
        catch (Exception ex) { Log.Write($"Status: {ex.Message}"); }
    }

    private void OnLive(string type, JsonNode? data)
    {
        if (type is not ("freeze_explained" or "event")) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (type == "freeze_explained")
            {
                NoticeTitle.Text = data.Str("headline") ?? $"{data.Str("app")} froze";
                NoticeText.Text = data.Str("explanation") ?? "";
                _noticeEventId = data.Long("eventId");
            }
            else
            {
                NoticeTitle.Text = data.Str("title") ?? "Something just happened";
                NoticeText.Text = data.Str("detail") ?? "Hindsight recorded what your PC was doing.";
                _noticeEventId = data.Long("id");
            }
            NoticeAction.Visibility = _noticeEventId is null ? Visibility.Collapsed : Visibility.Visible;
            Notice.Visibility = Visibility.Visible;
            _noticeTimer.Stop();
            _noticeTimer.Start();
            if (ContentFrame.Content is IRefreshable r) _ = r.RefreshAsync();
        });
    }

    private void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        Notice.Visibility = Visibility.Collapsed;
        if (_noticeEventId is { } id) Navigate("ask", new AskRequest(null, id));
    }

    private void OnNoticeDismiss(object sender, RoutedEventArgs e) => Notice.Visibility = Visibility.Collapsed;
}

/// <summary>Pages with a health band under the window's caption buttons; the buttons take the band's text color.</summary>
public interface IBanded
{
    string BandLevel { get; }
}

/// <summary>Pages that can reload their data when something new is recorded.</summary>
public interface IRefreshable
{
    Task RefreshAsync();
}
