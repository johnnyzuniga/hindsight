using ResMan.Core;
using ResMan.Data;

namespace ResMan.Service;

/// <summary>
/// The black box: the last ~2 minutes of system state at 2-second resolution, kept only in memory.
/// When something goes wrong, the minute before it is saved to the database for replay.
/// </summary>
public sealed class ReplayBuffer(ReplayStore store, ILogger<ReplayBuffer> log)
{
    public static readonly TimeSpan Keep = TimeSpan.FromSeconds(130);
    public static readonly TimeSpan Before = TimeSpan.FromSeconds(60);
    private readonly LinkedList<ReplaySample> _samples = [];
    private readonly Lock _lock = new();

    public void Add(Snapshot s)
    {
        var sample = new ReplaySample(s.TimeUtc, s.TotalCpuPercent, s.MemoryUsedPercent, s.Top.Sum(p => p.DiskBytesPerSec),
            s.Top.Where(p => !RuleValidator.IsOwnProcess(p.Name))
                .OrderByDescending(p => p.CpuPercent + p.WorkingSetBytes / 2e8 + p.DiskBytesPerSec / 1e7).Take(5)
                .Select(p => new ReplayProcess(p.Name, p.CpuPercent, p.WorkingSetBytes >> 20, p.DiskBytesPerSec)).ToList());
        lock (_lock)
        {
            _samples.AddLast(sample);
            while (_samples.First is { } f && sample.TimeUtc - f.Value.TimeUtc > Keep) _samples.RemoveFirst();
        }
    }

    /// <summary>Saves the samples from a minute before <paramref name="startUtc"/> up to now. Returns how many were saved.</summary>
    public int SaveAround(long eventId, DateTime startUtc)
    {
        List<ReplaySample> slice;
        lock (_lock) slice = _samples.Where(s => s.TimeUtc >= startUtc - Before).ToList();
        if (slice.Count == 0) return 0;
        store.Save(eventId, slice);
        log.LogInformation("Saved {Count} black-box samples for event {Id}", slice.Count, eventId);
        return slice.Count;
    }

    public bool Covers(DateTime timeUtc)
    {
        lock (_lock) return _samples.First is { } f && f.Value.TimeUtc <= timeUtc;
    }
}
