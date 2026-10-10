using System.Runtime.InteropServices;
using ResMan.Core;

namespace ResMan.Agent;

/// <summary>
/// Watches the foreground window. Windows marks a window "hung" after it ignores input for ~5 s;
/// when that happens ResMan asks the service to capture a resource snapshot and records the freeze.
/// </summary>
public sealed class HangDetector(ServiceClient service, AgentState state, DoctorHost host, ILogger<HangDetector> log) : BackgroundService
{
    // IsHungAppWindow only reports a hang after about 5 seconds of no response.
    private const double DetectionDelaySeconds = 5;
    private const double ReportStillHungAfterSeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        (nint hwnd, int pid, string app, DateTime since, bool reported)? hang = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (state.Paused) { hang = null; continue; }
            try
            {
                var fg = User32.GetForegroundWindow();
                if (fg != 0 && User32.ClassName(fg) == "Ghost") fg = User32.HungWindowFromGhostWindow(fg);
                var hungNow = fg != 0 && User32.IsHungAppWindow(fg);

                if (hungNow && hang is null)
                {
                    User32.GetWindowThreadProcessId(fg, out var pid);
                    var app = User32.ProcessName(pid) ?? "unknown";
                    if (RuleValidator.IsOwnProcess(app)) continue;
                    hang = (fg, pid, app, DateTime.UtcNow.AddSeconds(-DetectionDelaySeconds), false);
                    log.LogInformation("{App} (pid {Pid}) is not responding", app, pid);
                    await state.BroadcastAsync("hang_started", new { app, pid });
                }
                else if (hang is { } h)
                {
                    var stillHung = h.hwnd == fg ? hungNow : User32.IsHungAppWindow(h.hwnd);
                    var duration = (DateTime.UtcNow - h.since).TotalSeconds;
                    if (stillHung && !h.reported && duration >= ReportStillHungAfterSeconds)
                    {
                        await ReportAsync(h.app, h.pid, h.since, duration, recovered: false, ct);
                        hang = h with { reported = true };
                    }
                    else if (!stillHung)
                    {
                        if (!h.reported) await ReportAsync(h.app, h.pid, h.since, duration, recovered: true, ct);
                        hang = null;
                    }
                }
            }
            catch (Exception ex) { log.LogWarning("Hang check failed: {Message}", ex.Message); }
        }
    }

    private async Task ReportAsync(string app, int pid, DateTime since, double duration, bool recovered, CancellationToken ct)
    {
        Interlocked.Increment(ref state.HangsDetected);
        var report = new HangReport(app + ".exe", pid, since, Math.Round(duration, 1), recovered);
        var r = await service.SendAsync(PipeTypes.Hang, report, ResManJson.Default.HangReport, ct);
        log.LogInformation("Freeze of {App} for {Seconds:0}s {Result}", app, duration, r.Ok ? "recorded" : $"not recorded: {r.Error}");
        await state.BroadcastAsync("hang", report);

        // The instant "what just happened" note, built from the snapshot taken at the moment of the freeze.
        if (host.Doctor is not { } doctor) return;
        try
        {
            long? eventId = r.Ok && r.Payload is { } p ? p.GetInt64() : null;
            var snapshot = eventId is { } id && doctor.Health.Get(id)?.SnapshotId is { } sid ? doctor.Telemetry.Get(sid) : null;
            var explanation = Brain.Doctor.FreezeExplainer.Explain(doctor, eventId, report.AppName, duration, snapshot);
            await state.BroadcastAsync("freeze_explained", explanation);
            state.RaiseNotify(explanation.Headline, explanation.Explanation + (explanation.RecentChange is null ? "" : " " + explanation.RecentChange));
        }
        catch (Exception ex) { log.LogWarning("Couldn't explain the freeze: {Message}", ex.Message); }
    }
}

/// <summary>Decides the active profile (gaming, focus, battery, idle) from what the user is doing. No AI involved.</summary>
public sealed class ContextDetector(ServiceClient service, AgentState state, ILogger<ContextDetector> log) : BackgroundService
{
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vlc", "mpc-hc64", "potplayermini64", "powerpnt", "explorer",
        "applicationframehost", "video.ui", "netflix", "obs64", "zoom", "ms-teams", "teams",
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastSent = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (state.Paused) continue;
            try
            {
                var update = Detect();
                var changed = update.Profile != state.Profile;
                state.ForegroundApp = update.ForegroundApp;
                if (changed || DateTime.UtcNow - lastSent > TimeSpan.FromMinutes(1))
                {
                    if (changed)
                    {
                        log.LogInformation("Profile: {Old} -> {New} ({App})", state.Profile, update.Profile, update.ForegroundApp);
                        await state.BroadcastAsync("profile", update);
                    }
                    state.Profile = update.Profile;
                    await service.SendAsync(PipeTypes.Context, update, ResManJson.Default.ContextUpdate, ct);
                    lastSent = DateTime.UtcNow;
                }
            }
            catch (Exception ex) { log.LogWarning("Context check failed: {Message}", ex.Message); }
        }
    }

    public static ContextUpdate Detect()
    {
        var fg = User32.GetForegroundWindow();
        string? app = null;
        var fullscreen = false;
        if (fg != 0)
        {
            User32.GetWindowThreadProcessId(fg, out var pid);
            app = User32.ProcessName(pid);
            var cls = User32.ClassName(fg);
            if (cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd") && User32.GetWindowRect(fg, out var r))
            {
                var mon = User32.MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */);
                var info = new User32.MONITORINFO { cbSize = Marshal.SizeOf<User32.MONITORINFO>() };
                if (User32.GetMonitorInfo(mon, ref info))
                    fullscreen = r.Left <= info.rcMonitor.Left && r.Top <= info.rcMonitor.Top
                              && r.Right >= info.rcMonitor.Right && r.Bottom >= info.rcMonitor.Bottom;
            }
        }

        var lii = new User32.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<User32.LASTINPUTINFO>() };
        var idleSeconds = User32.GetLastInputInfo(ref lii) ? ((uint)Environment.TickCount - lii.dwTime) / 1000.0 : 0;
        User32.GetSystemPowerStatus(out var power);
        var onBattery = power.ACLineStatus == 0;

        var profile = idleSeconds >= 300 ? Profiles.Idle
            : fullscreen && app is not null && !NotGames.Contains(app) && !RuleValidator.IsOwnProcess(app) ? Profiles.Gaming // Hindsight's own window is never a game
            : onBattery ? Profiles.Battery
            : Profiles.Focus;
        return new ContextUpdate(profile, app, fullscreen, Math.Round(idleSeconds), onBattery);
    }
}
