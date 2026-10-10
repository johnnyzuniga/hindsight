using System.Globalization;
using ResMan.Core;

namespace ResMan.Brain.Analyzers;

/// <summary>Everything an analyzer may look at. Built once per analysis pass.</summary>
public sealed record AnalysisContext(
    DateTime NowUtc,
    IReadOnlyList<HealthEvent> Events,
    IReadOnlyList<Snapshot> RecentSnapshots,
    Func<long, Snapshot?> GetSnapshot,
    Func<string?, string> FriendlyName,
    IReadOnlyList<SystemChange>? Changes = null);

public interface IAnalyzer
{
    IEnumerable<Finding> Analyze(AnalysisContext ctx);
}

/// <summary>The deterministic "thinking in code" layer. The model only explains what these find.</summary>
public static class AnalyzerSuite
{
    public static readonly IAnalyzer[] All =
    [
        new FreezeCorrelationAnalyzer(),
        new ChangeCorrelationAnalyzer(),
        new CrashLoopAnalyzer(),
        new StartupRegressionAnalyzer(),
        new ThermalTrendAnalyzer(),
        new MemoryPressureAnalyzer(),
        new RecurringHogAnalyzer(),
    ];

    public static List<Finding> Run(AnalysisContext ctx) =>
        All.SelectMany(a => a.Analyze(ctx))
           .OrderByDescending(f => f.Severity).ThenByDescending(f => f.LastSeenUtc)
           .ToList();

    internal static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20} MB";
}

/// <summary>For each app that froze, what was the PC doing at that moment?</summary>
public sealed class FreezeCorrelationAnalyzer : IAnalyzer
{
    public IEnumerable<Finding> Analyze(AnalysisContext ctx)
    {
        var hangs = ctx.Events.Where(e => e.Kind == HealthKind.AppHang && e.TimeUtc >= ctx.NowUtc.AddDays(-7));
        foreach (var g in hangs.GroupBy(e => RuleValidator.NormalizeProcessName(e.AppName ?? "unknown"), StringComparer.OrdinalIgnoreCase))
        {
            var list = g.ToList();
            var causes = new Dictionary<string, int>();
            var withSnapshot = 0;
            foreach (var e in list)
            {
                if (e.SnapshotId is not { } sid || ctx.GetSnapshot(sid) is not { } s) continue;
                withSnapshot++;
                foreach (var c in Causes(s, g.Key, ctx.FriendlyName)) causes[c] = causes.GetValueOrDefault(c) + 1;
            }
            var app = ctx.FriendlyName(g.Key);
            string evidence;
            if (withSnapshot == 0)
                evidence = "No resource snapshot was captured for these freezes.";
            else if (causes.Count == 0)
                evidence = $"In {withSnapshot} freeze(s) with data, memory, CPU and disk all looked normal, so the cause is likely inside {app} itself.";
            else
                evidence = string.Join("; ", causes.OrderByDescending(c => c.Value).Take(3).Select(c => $"{c.Key} in {c.Value} of {withSnapshot} freezes"));

            yield return new Finding("freeze_correlation", list.Count >= 3 ? Severity.Warning : Severity.Notice,
                $"{app} froze {list.Count} time{(list.Count == 1 ? "" : "s")} this week", evidence,
                list.Min(e => e.TimeUtc), list.Max(e => e.TimeUtc), list.Count, g.Key, list.Select(e => e.Id).ToList());
        }
    }

    internal static IEnumerable<string> Causes(Snapshot s, string hungApp, Func<string?, string> friendly)
    {
        if (s.MemoryUsedPercent >= 90) yield return $"memory was nearly full ({s.MemoryUsedPercent:0}%)";
        if (s.TotalCpuPercent >= 90) yield return $"the processor was maxed out ({s.TotalCpuPercent:0}%)";
        var others = s.Top.Where(p => !p.Name.Equals(hungApp, StringComparison.OrdinalIgnoreCase) && !RuleValidator.IsOwnProcess(p.Name)).ToList();
        if (others.Where(p => p.CpuPercent >= 40).MaxBy(p => p.CpuPercent) is { } cpu)
            yield return $"{friendly(cpu.Name)} was using {cpu.CpuPercent:0}% CPU";
        if (others.Where(p => p.DiskBytesPerSec >= 20L << 20).MaxBy(p => p.DiskBytesPerSec) is { } disk)
            yield return $"{friendly(disk.Name)} was busy with the disk ({disk.DiskBytesPerSec >> 20} MB/s)";
    }
}

public sealed class CrashLoopAnalyzer : IAnalyzer
{
    public IEnumerable<Finding> Analyze(AnalysisContext ctx)
    {
        var crashes = ctx.Events.Where(e => e.Kind == HealthKind.AppCrash && e.TimeUtc >= ctx.NowUtc.AddHours(-24) && e.AppName is not null);
        foreach (var g in crashes.GroupBy(e => e.AppName!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3))
        {
            var app = ctx.FriendlyName(g.Key);
            var module = g.Select(e => e.Data.GetValueOrDefault("ModuleName") ?? e.Data.GetValueOrDefault("3")).FirstOrDefault(m => !string.IsNullOrEmpty(m));
            yield return new Finding("crash_loop", Severity.Warning, $"{app} crashed {g.Count()} times today",
                module is null ? "Repeated crashes in a short time." : $"Repeated crashes, each in the same component ({module}).",
                g.Min(e => e.TimeUtc), g.Max(e => e.TimeUtc), g.Count(), g.Key, g.Select(e => e.Id).ToList());
        }
    }
}

public sealed class StartupRegressionAnalyzer : IAnalyzer
{
    public IEnumerable<Finding> Analyze(AnalysisContext ctx)
    {
        var boots = ctx.Events
            .Where(e => e.Kind == HealthKind.SlowBoot && e.Data.TryGetValue("BootTime", out _))
            .Select(e => (e, ms: double.Parse(e.Data["BootTime"], CultureInfo.InvariantCulture)))
            .OrderBy(b => b.e.TimeUtc).ToList();
        if (boots.Count < 4) yield break;
        var latest = boots[^1];
        var prior = boots.SkipLast(1).Select(b => b.ms).Order().ToList();
        var median = prior[prior.Count / 2];
        if (latest.ms > median * 1.5 && latest.ms - median > 20_000)
            yield return new Finding("startup_regression", Severity.Notice, "Startup has become slower",
                $"The last startup took {latest.ms / 1000:0} seconds; it usually takes about {median / 1000:0}.",
                latest.e.TimeUtc, latest.e.TimeUtc, 1, null, [latest.e.Id]);
    }
}

public sealed class ThermalTrendAnalyzer : IAnalyzer
{
    public IEnumerable<Finding> Analyze(AnalysisContext ctx)
    {
        var throttles = ctx.Events.Where(e => e.Kind == HealthKind.ThermalThrottle).ToList();
        var lastDay = throttles.Count(e => e.TimeUtc >= ctx.NowUtc.AddHours(-24));
        var weekAvg = throttles.Count(e => e.TimeUtc >= ctx.NowUtc.AddDays(-7)) / 7.0;
        if (lastDay >= 10 || (lastDay >= 3 && lastDay > weekAvg * 2))
            yield return new Finding("thermal_trend", lastDay >= 10 ? Severity.Warning : Severity.Notice,
                "Your PC has been running hot",
                $"The processor slowed itself down because of heat {lastDay} times in the last 24 hours (about {weekAvg:0.#} per day this week).",
                throttles.Min(e => e.TimeUtc), throttles.Max(e => e.TimeUtc), lastDay, null,
                throttles.Where(e => e.TimeUtc >= ctx.NowUtc.AddHours(-24)).Select(e => e.Id).ToList());
    }
}

public sealed class MemoryPressureAnalyzer : IAnalyzer
{
    public IEnumerable<Finding> Analyze(AnalysisContext ctx)
    {
        var snaps = ctx.RecentSnapshots;
        if (snaps.Count < 30) yield break;
        var high = snaps.Where(s => s.MemoryUsedPercent >= 90).ToList();
        if (high.Count < snaps.Count * 0.2) yield break;
        var hogs = high.SelectMany(s => s.Top)
            .Where(p => !RuleValidator.IsOwnProcess(p.Name)) // ResMan's own AI is never the user's memory problem
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (name: g.Key, avg: (long)g.Average(p => p.WorkingSetBytes)))
            .OrderByDescending(x => x.avg).Take(3)
            .Select(x => $"{ctx.FriendlyName(x.name)} ({AnalyzerSuite.Mb(x.avg)})");
        yield return new Finding("memory_pressure", Severity.Warning, "Your PC is often low on memory",
            $"Memory was over 90% full {100.0 * high.Count / snaps.Count:0}% of the time in the last day. Biggest users then: {string.Join(", ", hogs)}.",
            high.Min(s => s.TimeUtc), high.Max(s => s.TimeUtc), high.Count, null, []);
    }
}

public sealed class RecurringHogAnalyzer : IAnalyzer
{
    public IEnumerable<Finding> Analyze(AnalysisContext ctx)
    {
        var snaps = ctx.RecentSnapshots;
        if (snaps.Count < 30) yield break;
        var byName = snaps.SelectMany(s => s.Top).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var g in byName)
        {
            if (g.Key.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase)) continue;
            var cpuHeavy = g.Count(p => p.CpuPercent >= 25);
            var diskHeavy = g.Count(p => p.DiskBytesPerSec >= 10L << 20);
            var share = Math.Max(cpuHeavy, diskHeavy) / (double)snaps.Count;
            if (share < 0.3) continue;
            var what = cpuHeavy >= diskHeavy
                ? $"used over 25% CPU (average {g.Where(p => p.CpuPercent >= 25).Average(p => p.CpuPercent):0}%)"
                : $"kept the disk busy (average {(long)g.Where(p => p.DiskBytesPerSec >= 10L << 20).Average(p => p.DiskBytesPerSec) >> 20} MB/s)";
            yield return new Finding("recurring_hog", Severity.Notice, $"{ctx.FriendlyName(g.Key)} is often busy in the background",
                $"It {what} in {share * 100:0}% of samples over the last day.",
                snaps[0].TimeUtc, snaps[^1].TimeUtc, Math.Max(cpuHeavy, diskHeavy), g.Key, []);
        }
    }
}
