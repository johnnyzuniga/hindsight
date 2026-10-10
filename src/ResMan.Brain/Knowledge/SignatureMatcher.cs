using System.Globalization;
using ResMan.Core;

namespace ResMan.Brain.Knowledge;

public sealed record SignatureMatch(Signature Signature, IReadOnlyList<HealthEvent> Events, string? App)
{
    public DateTime LastSeenUtc => Events.Max(e => e.TimeUtc);
}

/// <summary>Matches problem signatures against health events. Pure code; no model involved.</summary>
public static class SignatureMatcher
{
    public static IReadOnlyList<SignatureMatch> Match(IEnumerable<Signature> signatures, IReadOnlyList<HealthEvent> events, DateTime nowUtc)
    {
        var sigs = signatures.ToList();
        var matches = new List<SignatureMatch>();
        foreach (var sig in sigs)
        {
            var inWindow = events.Where(e => e.TimeUtc >= nowUtc.AddHours(-sig.WindowHours) && Applies(sig, e)).ToList();
            if (sig.SameApp)
            {
                foreach (var g in inWindow.Where(e => e.AppName is not null).GroupBy(e => e.AppName!, StringComparer.OrdinalIgnoreCase))
                    if (g.Count() >= sig.MinCount) matches.Add(new(sig, g.ToList(), g.Key));
            }
            else if (inWindow.Count >= sig.MinCount)
            {
                matches.Add(new(sig, inWindow, null));
            }
        }
        return ApplySupersedes(matches);
    }

    /// <summary>
    /// A broader signature ("an app froze") gives up the events claimed by a more specific one
    /// ("chrome keeps freezing"); it survives only if enough unclaimed events remain.
    /// </summary>
    private static List<SignatureMatch> ApplySupersedes(List<SignatureMatch> matches)
    {
        var result = new List<SignatureMatch>();
        foreach (var m in matches)
        {
            var claimed = matches
                .Where(o => o.Signature.Supersedes?.Contains(m.Signature.Id) == true)
                .SelectMany(o => o.Events.Select(e => e.Id))
                .ToHashSet();
            if (claimed.Count == 0) { result.Add(m); continue; }
            var remaining = m.Events.Where(e => !claimed.Contains(e.Id)).ToList();
            if (remaining.Count >= m.Signature.MinCount) result.Add(m with { Events = remaining });
        }
        return result
            .OrderByDescending(m => m.Signature.SeverityLevel)
            .ThenByDescending(m => m.LastSeenUtc)
            .ToList();
    }

    public static bool Applies(Signature sig, HealthEvent e)
    {
        if (!sig.Kinds.Contains(e.Kind)) return false;
        if (sig.Providers is { Length: > 0 } && !sig.Providers.Contains(e.Provider, StringComparer.OrdinalIgnoreCase)) return false;
        if (sig.EventIds is { Length: > 0 } && !sig.EventIds.Contains(e.EventId)) return false;
        if (sig.DataEquals is { } eq)
            foreach (var (k, v) in eq)
                if (!e.Data.TryGetValue(k, out var actual) || !string.Equals(actual, v, StringComparison.OrdinalIgnoreCase)) return false;
        if (sig.DataGreaterThan is { } gt)
            foreach (var (k, v) in gt)
                if (!e.Data.TryGetValue(k, out var actual)
                    || !double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= v) return false;
        return true;
    }

    /// <summary>The most severe matched signature that covers a specific event.</summary>
    public static SignatureMatch? ForEvent(IReadOnlyList<SignatureMatch> matches, long eventId) =>
        matches.Where(m => m.Events.Any(e => e.Id == eventId)).MaxBy(m => m.Signature.SeverityLevel);
}
