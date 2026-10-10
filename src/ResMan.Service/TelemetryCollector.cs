using ResMan.Data;

namespace ResMan.Service;

/// <summary>
/// Samples every 2 seconds into the in-memory black box, and stores every 5th sample (every 10 s) as a
/// snapshot. Applies retention hourly.
/// </summary>
public sealed class TelemetryCollector(
    TelemetrySampler sampler, TelemetryStore store, HealthStore health, ReplayStore replays, ReplayBuffer buffer,
    ServiceState state, ILogger<TelemetryCollector> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    public const int StoreEvery = 5;
    public static readonly TimeSpan KeepRaw = TimeSpan.FromDays(7);
    public static readonly TimeSpan KeepEvents = TimeSpan.FromDays(90);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        sampler.Sample(); // baseline for rate calculations
        var lastRetention = DateTime.MinValue;
        var tick = 0;
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var snap = sampler.Sample();
                buffer.Add(snap);
                if (++tick % StoreEvery == 0)
                {
                    store.Insert(snap);
                    state.LastSnapshotUtc = snap.TimeUtc;
                }

                if (DateTime.UtcNow - lastRetention > TimeSpan.FromHours(1))
                {
                    lastRetention = DateTime.UtcNow;
                    store.ApplyRetention(DateTime.UtcNow, KeepRaw);
                    health.DeleteOlderThan(DateTime.UtcNow - KeepEvents);
                    replays.DeleteNetworkOlderThan(DateTime.UtcNow - KeepRaw);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Telemetry sample failed");
            }
        }
    }
}
