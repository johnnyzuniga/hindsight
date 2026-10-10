namespace ResMan.Agent;

/// <summary>
/// The tray icon, owned by the agent so a UI only needs to run while its window is open.
/// Notifications are reserved for new, significant problems; everything else waits in the timeline.
/// </summary>
public static class Tray
{
    public static void Start(DoctorHost host, AgentState state, Action quit)
    {
        var thread = new Thread(() => Run(host, state, quit)) { IsBackground = true, Name = "Tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Run(DoctorHost host, AgentState state, Action quit)
    {
        Application.EnableVisualStyles();
        using var appIcon = AppLauncher.IconPath() is { } path ? new Icon(path, SystemInformation.SmallIconSize) : null;
        using var icon = new NotifyIcon { Icon = appIcon ?? SystemIcons.Shield, Visible = true, Text = "Hindsight" };
        var header = new ToolStripMenuItem("Hindsight is starting...") { Enabled = false };
        void OpenWindow(params string[] args)
        {
            if (!AppLauncher.Open(out var problem, args)) icon.ShowBalloonTip(5000, "Hindsight", problem ?? "The Hindsight window couldn't open.", ToolTipIcon.Info);
        }
        var open = new ToolStripMenuItem("Open Hindsight", null, (_, _) => OpenWindow()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        icon.DoubleClick += (_, _) => OpenWindow();
        // Clicking a notification opens the window on the incidents page.
        icon.BalloonTipClicked += (_, _) => OpenWindow("--page", "incidents");
        var pause = new ToolStripMenuItem("Pause monitoring");
        pause.Click += (_, _) =>
        {
            state.Paused = !state.Paused;
            pause.Text = state.Paused ? "Resume monitoring" : "Pause monitoring";
        };
        var exit = new ToolStripMenuItem("Quit Hindsight", null, (_, _) =>
        {
            icon.Visible = false;
            Application.ExitThread();
            quit();
        });
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([header, new ToolStripSeparator(), open, pause, exit]);
        icon.ContextMenuStrip = menu;

        // Live notifications (e.g. "Chrome froze for 12 seconds: memory was nearly full") come from other threads.
        state.Notify += (title, message) =>
        {
            if (menu.IsHandleCreated) menu.BeginInvoke(() => icon.ShowBalloonTip(10_000, title, message, ToolTipIcon.Info));
            else icon.ShowBalloonTip(10_000, title, message, ToolTipIcon.Info);
        };
        _ = menu.Handle; // create the handle so BeginInvoke can marshal to this thread

        HashSet<string>? seen = null;
        void Refresh()
        {
            try
            {
                var a = host.Doctor?.Analyze();
                var text = a?.Status.Headline ?? "Waiting for the Hindsight service";
                header.Text = text;
                icon.Text = text.Length > 63 ? text[..63] : text;

                if (a is null) return;
                var important = a.Issues.Where(i => i.Severity is "warning" or "critical").ToList();
                if (seen is not null)
                    foreach (var i in important.Where(i => !seen.Contains(i.Id)).Take(1))
                        icon.ShowBalloonTip(8000, i.Title, i.Explanation, i.Severity == "critical" ? ToolTipIcon.Warning : ToolTipIcon.Info);
                seen = important.Select(i => i.Id).ToHashSet();
            }
            catch { /* the tray must never crash the agent */ }
        }

        using var timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
        Application.Run();
    }
}
