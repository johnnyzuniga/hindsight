using ResMan.Brain.Analyzers;
using ResMan.Core;

namespace ResMan.Brain.Doctor;

public sealed record FreezeExplanation(long? EventId, string App, double Seconds, string Headline, string Explanation, IReadOnlyList<string> Causes, string? RecentChange);

/// <summary>
/// The instant "what just happened" note shown the moment a freeze ends. Pure code, no model,
/// so it's ready in milliseconds. The local model can expand on it later via the explain endpoint.
/// </summary>
public static class FreezeExplainer
{
    public static FreezeExplanation Explain(DoctorService doctor, long? eventId, string appExe, double seconds, Snapshot? snapshot)
    {
        var kb = doctor.Knowledge;
        var name = RuleValidator.NormalizeProcessName(appExe);
        var app = kb.FriendlyName(name);
        var causes = snapshot is null ? [] : FreezeCorrelationAnalyzer.Causes(snapshot, name, kb.FriendlyName).ToList();

        var a = doctor.Analyze(fresh: true);
        var timesThisWeek = a.Events.Count(e => e.Kind == HealthKind.AppHang && e.TimeUtc >= a.NowUtc.AddDays(-7)
            && string.Equals(RuleValidator.NormalizeProcessName(e.AppName ?? ""), name, StringComparison.OrdinalIgnoreCase));
        var change = doctor.ChangesBefore(DateTime.UtcNow, TimeSpan.FromDays(3)).FirstOrDefault();

        var headline = $"{app} froze for about {seconds:0} seconds";
        var explanation = causes.Count switch
        {
            0 when snapshot is null => "ResMan couldn't capture what your PC was doing at that moment.",
            0 => $"Your PC wasn't short on memory, processor or disk at that moment, so the cause is probably inside {app} itself.",
            _ => $"At that moment, {Join(causes)}.",
        };
        if (timesThisWeek >= 2) explanation += $" This is the {Ordinal(timesThisWeek)} time this week.";
        var changeText = change is null ? null : $"Recently: {ChangeKind.Describe(change)} ({change.TimeUtc.ToLocalTime():ddd h:mm tt}).";
        return new FreezeExplanation(eventId, app, Math.Round(seconds, 1), headline, explanation, causes, changeText);
    }

    private static string Join(List<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.SkipLast(1)) + " and " + items[^1];

    private static string Ordinal(int n) => n switch
    {
        2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth",
        _ => n % 10 == 1 && n != 11 ? $"{n}st" : n % 10 == 2 && n != 12 ? $"{n}nd" : n % 10 == 3 && n != 13 ? $"{n}rd" : $"{n}th",
    };
}
