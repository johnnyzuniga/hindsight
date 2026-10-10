using System.Diagnostics;
using ResMan.Core;

namespace ResMan.Service;

/// <summary>
/// Takes whole-system snapshots. CPU and disk are rates, so each sample is computed
/// against the previous one. Handles are opened with the least privilege possible.
/// </summary>
public sealed class TelemetrySampler(ILogger<TelemetrySampler> log)
{
    private const int TopCount = 20;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, (long cpu, ulong io)> _prev = [];
    private (long idle, long kernel, long user)? _prevSystem;
    private long _prevTicks;
    private readonly GpuSampler _gpu = new(log);

    public Snapshot Sample()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var ticks = Stopwatch.GetTimestamp();
            var elapsedSec = _prevTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_prevTicks, ticks).TotalSeconds;
            _prevTicks = ticks;
            var cores = Environment.ProcessorCount;
            var gpu = _gpu.Sample();

            var samples = new List<ProcessSample>();
            var seen = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id is 0 or 4) continue;
                    seen.Add(p.Id);
                    long ws;
                    string name;
                    try { ws = p.WorkingSet64; name = p.ProcessName; }
                    catch { continue; }

                    double cpuPct = 0;
                    long diskRate = 0;
                    var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (h != 0)
                    {
                        try
                        {
                            Native.GetProcessTimes(h, out _, out _, out var k, out var u);
                            Native.GetProcessIoCounters(h, out var io);
                            var cpu = k + u;
                            var ioBytes = io.ReadTransferCount + io.WriteTransferCount;
                            if (elapsedSec > 0 && _prev.TryGetValue(p.Id, out var prev))
                            {
                                cpuPct = Math.Clamp((cpu - prev.cpu) / 1e7 / elapsedSec / cores * 100, 0, 100);
                                diskRate = (long)(Math.Max(0, (double)(ioBytes - prev.io)) / elapsedSec);
                            }
                            _prev[p.Id] = (cpu, ioBytes);
                        }
                        finally { Native.CloseHandle(h); }
                    }

                    samples.Add(new ProcessSample(p.Id, name, Math.Round(cpuPct, 1), ws, diskRate, Math.Round(gpu.GetValueOrDefault(p.Id), 1)));
                }
            }
            foreach (var gone in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(gone);

            var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            Native.GlobalMemoryStatusEx(ref mem);
            Native.GetSystemPowerStatus(out var power);

            return new Snapshot(0, now, Math.Round(SystemCpu(), 1), mem.dwMemoryLoad, (long)mem.ullAvailPhys,
                power.ACLineStatus == 0, PickTop(samples));
        }
    }

    private double SystemCpu()
    {
        Native.GetSystemTimes(out var idle, out var kernel, out var user);
        double pct = 0;
        if (_prevSystem is { } p)
        {
            var total = (kernel - p.kernel) + (user - p.user); // kernel time includes idle
            if (total > 0) pct = Math.Clamp((total - (idle - p.idle)) * 100.0 / total, 0, 100);
        }
        _prevSystem = (idle, kernel, user);
        return pct;
    }

    /// <summary>Union of the heaviest processes by each resource, capped at 20.</summary>
    internal static List<ProcessSample> PickTop(List<ProcessSample> all)
    {
        var pick = new Dictionary<int, ProcessSample>();
        void Take(IEnumerable<ProcessSample> src, int n)
        {
            foreach (var s in src.Take(n)) pick.TryAdd(s.Pid, s);
        }
        Take(all.Where(s => s.CpuPercent > 0).OrderByDescending(s => s.CpuPercent), 8);
        Take(all.OrderByDescending(s => s.WorkingSetBytes), 8);
        Take(all.Where(s => s.DiskBytesPerSec > 0).OrderByDescending(s => s.DiskBytesPerSec), 5);
        Take(all.Where(s => s.GpuPercent > 0).OrderByDescending(s => s.GpuPercent), 5);
        return pick.Values.OrderByDescending(s => s.CpuPercent).ThenByDescending(s => s.WorkingSetBytes).Take(TopCount).ToList();
    }
}

/// <summary>Per-process GPU utilization from the "GPU Engine" performance counters (max across engines).</summary>
internal sealed class GpuSampler(ILogger log)
{
    private Dictionary<string, CounterSample> _prev = [];
    private bool _disabled;

    public Dictionary<int, double> Sample()
    {
        var result = new Dictionary<int, double>();
        if (_disabled) return result;
        try
        {
            var data = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            var util = data["Utilization Percentage"];
            if (util is null) return result;
            var next = new Dictionary<string, CounterSample>(util.Count);
            foreach (InstanceData inst in util.Values)
            {
                next[inst.InstanceName] = inst.Sample;
                if (!_prev.TryGetValue(inst.InstanceName, out var before)) continue;
                var pid = ParsePid(inst.InstanceName);
                if (pid <= 0) continue;
                var v = CounterSample.Calculate(before, inst.Sample);
                if (v > result.GetValueOrDefault(pid)) result[pid] = Math.Min(v, 100);
            }
            _prev = next;
        }
        catch (Exception ex)
        {
            _disabled = true;
            log.LogWarning("GPU counters unavailable, GPU usage won't be recorded: {Message}", ex.Message);
        }
        return result;
    }

    internal static int ParsePid(string instance)
    {
        // e.g. "pid_1234_luid_0x00000000_0x0000D0F2_phys_0_eng_0_engtype_3D"
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return -1;
        var end = instance.IndexOf('_', 4);
        return end > 4 && int.TryParse(instance.AsSpan(4, end - 4), out var pid) ? pid : -1;
    }
}
