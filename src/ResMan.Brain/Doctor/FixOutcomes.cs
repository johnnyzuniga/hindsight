using ResMan.Core;

namespace ResMan.Brain.Doctor;

public sealed record FixOutcome(
    FixAttempt Fix,
    double BeforePerWeek,
    double AfterPerWeek,
    double DaysSince,
    string Verdict,
    string Summary);

/// <summary>Compares how often a problem happened before and after a fix was applied ("freezes: 6/week -> 0").</summary>
public static class FixOutcomes
{
    private const double WindowDays = 28;
    private const double MinDaysToJudge = 3;

    public static FixOutcome Evaluate(FixAttempt fix, IReadOnlyList<HealthEvent> events, DateTime nowUtc, DateTime historyStartUtc)
    {
        bool Matches(HealthEvent e) =>
            e.Kind == fix.ProblemKind &&
            (fix.App is null || string.Equals(RuleValidator.NormalizeProcessName(e.AppName ?? ""), fix.App, StringComparison.OrdinalIgnoreCase));

        var beforeStart = fix.AppliedUtc.AddDays(-WindowDays) < historyStartUtc ? historyStartUtc : fix.AppliedUtc.AddDays(-WindowDays);
        var beforeDays = Math.Max(1, (fix.AppliedUtc - beforeStart).TotalDays);
        var afterDays = Math.Min(WindowDays, (nowUtc - fix.AppliedUtc).TotalDays);

        var before = events.Count(e => Matches(e) && e.TimeUtc >= beforeStart && e.TimeUtc < fix.AppliedUtc);
        var after = events.Count(e => Matches(e) && e.TimeUtc >= fix.AppliedUtc && e.TimeUtc < fix.AppliedUtc.AddDays(WindowDays));
        var beforeWeek = before / beforeDays * 7;
        var afterWeek = afterDays > 0 ? after / Math.Max(afterDays, 1) * 7 : 0;

        var what = Noun(fix.ProblemKind);
        string verdict, summary;
        if (afterDays < MinDaysToJudge)
        {
            verdict = "too_early";
            summary = $"{Cap(what)}: {beforeWeek:0.#}/week before. Too early to tell; check back in a few days.";
        }
        else
        {
            verdict = after == 0 && before > 0 ? "fixed"
                : afterWeek <= beforeWeek * 0.5 ? "better"
                : afterWeek >= beforeWeek * 1.5 && after > 1 ? "worse"
                : "no_change";
            var label = verdict switch { "fixed" => "looks fixed", "better" => "better", "worse" => "worse", _ => "no clear change" };
            summary = $"{Cap(what)}: {beforeWeek:0.#}/week before -> {afterWeek:0.#}/week in the {afterDays:0} days since ({label}).";
        }
        return new FixOutcome(fix, Math.Round(beforeWeek, 1), Math.Round(afterWeek, 1), Math.Round(Math.Max(0, (nowUtc - fix.AppliedUtc).TotalDays), 1), verdict, summary);
    }

    public static string Noun(string kind) => kind switch
    {
        HealthKind.AppHang => "freezes",
        HealthKind.AppCrash => "crashes",
        HealthKind.BlueScreen => "blue screens",
        HealthKind.UnexpectedShutdown => "unexpected shutdowns",
        HealthKind.DisplayDriverReset => "graphics driver resets",
        HealthKind.ThermalThrottle => "heat slow-downs",
        HealthKind.DiskError => "drive errors",
        HealthKind.LowMemory => "out-of-memory events",
        HealthKind.WifiDrop => "Wi-Fi drops",
        HealthKind.DriveHealthWarning => "drive health warnings",
        _ => "problems",
    };

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
