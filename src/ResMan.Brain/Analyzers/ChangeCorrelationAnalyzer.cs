using ResMan.Core;

namespace ResMan.Brain.Analyzers;

/// <summary>
/// Links recurring problems to something that changed shortly before they started, e.g.
/// "Graphics driver crashes started after Tuesday: a driver was installed (NVIDIA ...)".
/// A change only counts if the problem became clearly more frequent after it.
/// </summary>
public sealed class ChangeCorrelationAnalyzer : IAnalyzer
{
    private static readonly string[] ProblemKinds =
    [
        HealthKind.AppCrash, HealthKind.AppHang, HealthKind.BlueScreen, HealthKind.UnexpectedShutdown,
        HealthKind.DisplayDriverReset, HealthKind.DiskError,
    ];

    private static readonly TimeSpan Lookback = TimeSpan.FromDays(14);
    private static readonly TimeSpan MaxLead = TimeSpan.FromHours(72);

    public IEnumerable<Finding> Analyze(AnalysisContext ctx)
    {
        var changes = (ctx.Changes ?? []).Where(c => !ChangeKind.IsRoutine(c)).ToList();
        if (changes.Count == 0) yield break;

        var problems = ctx.Events
            .Where(e => ProblemKinds.Contains(e.Kind))
            .GroupBy(e => (e.Kind, App: AppKey(e)));

        foreach (var g in problems)
        {
            var all = g.OrderBy(e => e.TimeUtc).ToList();
            var recent = all.Where(e => e.TimeUtc >= ctx.NowUtc - Lookback).ToList();
            if (recent.Count < 2) continue;

            var first = recent[0].TimeUtc;
            var best = changes
                .Where(c => c.TimeUtc <= first && c.TimeUtc >= first - MaxLead)
                .Select(c => (change: c, score: Score(c, g.Key.Kind, g.Key.App, ctx.FriendlyName)))
                .Where(x => x.score > 0)
                .Select(x =>
                {
                    var before = all.Count(e => e.TimeUtc < x.change.TimeUtc && e.TimeUtc >= x.change.TimeUtc - Lookback);
                    var after = all.Count(e => e.TimeUtc >= x.change.TimeUtc);
                    var daysAfter = Math.Max(1, (ctx.NowUtc - x.change.TimeUtc).TotalDays);
                    var rateAfter = after / daysAfter;
                    var rateBefore = before / Lookback.TotalDays;
                    return (x.change, x.score, before, after, ok: after >= 2 && rateAfter >= 2 * rateBefore);
                })
                .Where(x => x.ok)
                .OrderByDescending(x => x.score).ThenByDescending(x => x.change.TimeUtc)
                .FirstOrDefault();
            if (best.change is null) continue;

            var problem = ProblemPhrase(g.Key.Kind, g.Key.App, ctx.FriendlyName);
            var day = best.change.TimeUtc.ToLocalTime().ToString("dddd");
            yield return new Finding("change_correlation", Severity.Warning,
                $"{problem} started after a change on {day}",
                $"{ChangeKind.Describe(best.change)} on {best.change.TimeUtc.ToLocalTime():ddd MMM d}. " +
                $"Since then: {best.after} time{(best.after == 1 ? "" : "s")}; in the two weeks before: {best.before}.",
                recent[0].TimeUtc, recent[^1].TimeUtc, best.after, g.Key.App,
                all.Where(e => e.TimeUtc >= best.change.TimeUtc).Select(e => e.Id).ToList(), best.change);
        }
    }

    private static string? AppKey(HealthEvent e) =>
        e.Kind is HealthKind.AppCrash or HealthKind.AppHang && e.AppName is not null
            ? RuleValidator.NormalizeProcessName(e.AppName).ToLowerInvariant()
            : null;

    /// <summary>How plausible it is that this change caused this kind of problem. Zero means unrelated.</summary>
    internal static int Score(SystemChange c, string kind, string? app, Func<string?, string> friendly)
    {
        var systemLevel = kind is HealthKind.BlueScreen or HealthKind.DisplayDriverReset or HealthKind.UnexpectedShutdown or HealthKind.DiskError;
        var appMatches = app is not null &&
            (c.Name.Contains(app, StringComparison.OrdinalIgnoreCase) || c.Name.Contains(friendly(app), StringComparison.OrdinalIgnoreCase));
        // App problems are only linked to changes that name the same app; system problems to drivers and updates.
        // Anything looser produced confident-sounding but coincidental links in testing.
        return c.Kind switch
        {
            ChangeKind.DriverInstalled => systemLevel ? 3 : 0,
            ChangeKind.WindowsUpdate => systemLevel ? 2 : 0,
            ChangeKind.AppInstalled or ChangeKind.AppRemoved => appMatches ? 4 : 0,
            _ => 0,
        };
    }

    internal static string ProblemPhrase(string kind, string? app, Func<string?, string> friendly) => kind switch
    {
        HealthKind.AppCrash => $"{friendly(app)} crashes",
        HealthKind.AppHang => $"{friendly(app)} freezes",
        HealthKind.BlueScreen => "Blue-screen crashes",
        HealthKind.UnexpectedShutdown => "Unexpected shutdowns",
        HealthKind.DisplayDriverReset => "Graphics driver crashes",
        HealthKind.DiskError => "Drive errors",
        _ => "Problems",
    };
}
