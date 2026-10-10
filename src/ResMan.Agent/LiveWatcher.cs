using ResMan.Brain.Doctor;

namespace ResMan.Agent;

/// <summary>Pushes newly recorded health events to connected UI clients over the /live WebSocket.</summary>
public sealed class LiveWatcher(DoctorHost host, AgentState state) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        long lastId = -1;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var doctor = host.Doctor;
            if (doctor is null) continue;
            try
            {
                if (lastId < 0) { lastId = doctor.Health.MaxId(); continue; }
                foreach (var e in doctor.Health.After(lastId))
                {
                    lastId = e.Id;
                    if (e.Kind == Core.HealthKind.Noise) continue;
                    var (title, detail) = Reports.Describe(e, host.Knowledge);
                    await state.BroadcastAsync("event", new { id = e.Id, e.TimeUtc, e.Kind, title, detail });
                }
            }
            catch { /* database busy; try again next tick */ }
        }
    }
}
