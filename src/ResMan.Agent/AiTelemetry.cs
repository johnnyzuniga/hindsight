using System.Diagnostics;
using ResMan.Brain.Models;
using ResMan.Core;

namespace ResMan.Agent;

/// <summary>
/// Live, measured numbers for the AI footprint counter: sidecar state, its RAM and VRAM while loaded,
/// load and inference times, and what ResMan costs when the AI is unloaded.
/// </summary>
public sealed class AiTelemetry(SidecarModelProvider sidecar, ServiceClient service)
{
    private readonly List<double> _inferenceMs = [];
    private readonly List<double> _tokensPerSec = [];
    private readonly Lock _lock = new();
    public double? LastQuestionMs { get; private set; }
    public int Questions { get; private set; }

    public void RecordQuestion(double totalMs, double modelMs)
    {
        lock (_lock)
        {
            LastQuestionMs = totalMs;
            Questions++;
            _inferenceMs.Add(modelMs);
            if (_inferenceMs.Count > 50) _inferenceMs.RemoveAt(0);
        }
    }

    public async Task<object> SnapshotAsync(CancellationToken ct = default)
    {
        var state = sidecar.State;
        var pid = SidecarModelProvider.SidecarProcessId();
        long? ramMb = null, privateMb = null, vramMb = null;
        if (pid is { } p)
        {
            try
            {
                using var proc = Process.GetProcessById(p);
                ramMb = proc.WorkingSet64 >> 20;
                privateMb = proc.PrivateMemorySize64 >> 20;
            }
            catch { }
            vramMb = DedicatedGpuMemoryMb(p);
        }
        var stats = state == "ready" ? await sidecar.StatsAsync(ct) : null;
        if (stats?.TokensPerSecond is { } tps) lock (_lock) { _tokensPerSec.Add(tps); if (_tokensPerSec.Count > 50) _tokensPerSec.RemoveAt(0); }

        using var me = Process.GetCurrentProcess();
        var svc = await service.GetAsync(PipeTypes.Status, ResManJson.Default.ServiceStatus, ct);
        var agentMb = me.PrivateMemorySize64 >> 20;
        var serviceMb = (svc?.WorkingSetBytes ?? 0) >> 20;
        return new
        {
            enabled = sidecar.Enabled,
            model = Path.GetFileName(sidecar.Options.ModelPath),
            modelFileMb = sidecar.Options.ModelPath is { } mp && File.Exists(mp) ? new FileInfo(mp).Length >> 20 : 0,
            state,
            pid,
            backend = stats?.Backend,
            contextSize = stats?.ContextSize ?? sidecar.Options.ContextSize,
            sidecar = new { ramMb, privateMb, vramMb },
            timings = new
            {
                loadMs = stats?.LoadMilliseconds,
                coldStartMs = sidecar.LastColdStartMilliseconds,
                lastInferenceMs = stats?.LastInferenceMilliseconds,
                tokensPerSecond = stats?.TokensPerSecond,
                lastQuestionMs = LastQuestionMs,
                questions = Questions,
            },
            idleUnloadSeconds = stats?.IdleSeconds ?? sidecar.Options.IdleSeconds,
            resman = new
            {
                agentMb,
                serviceMb,
                totalWithoutAiMb = agentMb + serviceMb,
                totalNowMb = agentMb + serviceMb + (privateMb ?? 0),
                measure = "private memory (Task Manager's Memory column); VRAM from Windows' GPU Process Memory counters",
            },
            networkUsedByAi = "none: inference runs entirely on this PC",
            measuredUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Dedicated GPU memory used by a process, from the "GPU Process Memory" performance counters.</summary>
    private static long? DedicatedGpuMemoryMb(int pid)
    {
        try
        {
            var cat = new PerformanceCounterCategory("GPU Process Memory");
            long total = 0;
            var found = false;
            foreach (var instance in cat.GetInstanceNames().Where(n => n.StartsWith($"pid_{pid}_", StringComparison.Ordinal)))
            {
                using var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, readOnly: true);
                total += c.RawValue;
                found = true;
            }
            return found ? total >> 20 : null;
        }
        catch { return null; }
    }
}
