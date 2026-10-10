using System.Management;
using ResMan.Core;
using ResMan.Data;

namespace ResMan.Service;

/// <summary>
/// Reads each physical drive's own health status and SMART-style reliability counters (wear, read/write
/// errors, temperature) every 6 hours. Raises a health event when the status gets worse or error counters rise.
/// Reliability counters need admin rights; without them only the health status is recorded.
/// </summary>
public sealed class DriveHealthCollector(DriveHealthStore store, HealthStore health, ServiceState state, ILogger<DriveHealthCollector> log)
    : BackgroundService
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try { Collect(DateTime.UtcNow); }
            catch (Exception ex)
            {
                state.Warnings["drive-health"] = $"Drive health unavailable: {ex.Message}";
                log.LogWarning("Drive health check failed: {Message}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public void Collect(DateTime nowUtc)
    {
        var counters = ReadCounters();
        var previous = store.Latest().ToDictionary(d => d.DeviceId);
        using var disks = new ManagementObjectSearcher(Scope, "SELECT DeviceId, FriendlyName, MediaType, HealthStatus FROM MSFT_PhysicalDisk");
        foreach (ManagementObject d in disks.Get())
        {
            using (d)
            {
                var id = d["DeviceId"]?.ToString() ?? "?";
                counters.TryGetValue(id, out var c);
                var reading = new DriveHealth(nowUtc, id, d["FriendlyName"]?.ToString()?.Trim() ?? "Drive " + id,
                    MediaType(d["MediaType"]), HealthText(d["HealthStatus"]),
                    c.wear, c.readErr, c.writeErr, c.temp, c.hours);
                store.Insert(reading);
                if (Assess(previous.GetValueOrDefault(id), reading) is { } problem)
                {
                    health.Insert(new HealthEvent(0, nowUtc, "ResMan", "ResMan.DriveHealth", 1, 2, HealthKind.DriveHealthWarning,
                        null, $"{reading.Model}: {problem}", new Dictionary<string, string>
                        {
                            ["Model"] = reading.Model,
                            ["HealthStatus"] = reading.HealthStatus,
                            ["WearPercent"] = reading.WearPercent?.ToString() ?? "",
                            ["ReadErrorsTotal"] = reading.ReadErrorsTotal?.ToString() ?? "",
                            ["WriteErrorsTotal"] = reading.WriteErrorsTotal?.ToString() ?? "",
                            ["TemperatureC"] = reading.TemperatureC?.ToString() ?? "",
                        }, null));
                    log.LogWarning("Drive health: {Model}: {Problem}", reading.Model, problem);
                }
            }
        }
        if (counters.Count == 0 && !state.Elevated)
            state.Warnings["drive-health"] = "Drive wear and error counters need admin rights; only the overall health status is recorded.";
    }

    /// <summary>Returns a plain-language problem if this reading is worse than the last one, otherwise null.</summary>
    internal static string? Assess(DriveHealth? before, DriveHealth now)
    {
        if (now.HealthStatus is "Warning" or "Unhealthy" && before?.HealthStatus != now.HealthStatus)
            return $"Windows reports this drive's health as '{now.HealthStatus}'.";
        if (before is not null && now.ReadErrorsTotal > before.ReadErrorsTotal)
            return $"Read errors went up from {before.ReadErrorsTotal} to {now.ReadErrorsTotal}.";
        if (before is not null && now.WriteErrorsTotal > before.WriteErrorsTotal)
            return $"Write errors went up from {before.WriteErrorsTotal} to {now.WriteErrorsTotal}.";
        if (now.WearPercent >= 90 && !(before?.WearPercent >= 90))
            return $"The drive is {now.WearPercent}% worn out.";
        return null;
    }

    private static Dictionary<string, (int? wear, long? readErr, long? writeErr, int? temp, long? hours)> ReadCounters()
    {
        var result = new Dictionary<string, (int?, long?, long?, int?, long?)>();
        try
        {
            using var s = new ManagementObjectSearcher(Scope,
                "SELECT DeviceId, Wear, ReadErrorsTotal, WriteErrorsTotal, Temperature, PowerOnHours FROM MSFT_StorageReliabilityCounter");
            foreach (ManagementObject o in s.Get())
                using (o)
                    result[o["DeviceId"]?.ToString() ?? "?"] = (ToInt(o["Wear"]), ToLong(o["ReadErrorsTotal"]), ToLong(o["WriteErrorsTotal"]),
                        ToInt(o["Temperature"]) is > 0 and var t ? t : null, ToLong(o["PowerOnHours"]));
        }
        catch (ManagementException) { /* needs admin */ }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    private static int? ToInt(object? o) => o is null ? null : Convert.ToInt32(o);
    private static long? ToLong(object? o) => o is null ? null : Convert.ToInt64(o);

    private static string MediaType(object? o) => Convert.ToInt32(o ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" };

    private static string HealthText(object? o) => Convert.ToInt32(o ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
}
