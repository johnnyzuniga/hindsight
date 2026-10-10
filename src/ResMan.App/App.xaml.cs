using Microsoft.UI.Xaml;
using ResMan.App.Services;

namespace ResMan.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    public static AgentClient Agent { get; } = new();
    public static LiveClient Live { get; } = new(Agent);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Keep the window alive on a stray error; the page shows its own message.
            Log.Write($"Unhandled: {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
        _ = Live.RunAsync();

        // Optional start-up arguments, used by the tray and for demos:
        //   --page overview|ask|incidents|history|ai   --ask "my app keeps freezing"   --event <id>   --theme light|dark
        var a = Environment.GetCommandLineArgs();
        string? Arg(string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        if (Arg("--theme") is { } theme && Enum.TryParse<ThemeMode>(theme, true, out var mode)) Window.Theme.Preview(mode);
        if (Arg("--ask") is { } q) Window.Navigate("ask", new Pages.AskRequest(q, null));
        else if (Arg("--event") is { } ev && long.TryParse(ev, out var id)) Window.Navigate(Arg("--page") ?? "incidents", Arg("--page") == "ask" ? new Pages.AskRequest(null, id) : id);
        else if (Arg("--page") is { } page) Window.Navigate(page);
    }
}
