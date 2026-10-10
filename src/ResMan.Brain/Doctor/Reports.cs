using System.Globalization;
using ResMan.Brain.Knowledge;
using ResMan.Core;

namespace ResMan.Brain.Doctor;

public sealed record HealthStatus(string Level, string Headline, string Detail, int IssueCount, long HiddenWarnings);

public sealed record Issue(
    string Id,
    string Title,
    string Explanation,
    string? Evidence,
    string Severity,
    IReadOnlyList<Fix> Fixes,
    bool NeedsHuman,
    IReadOnlyList<long> EventIds,
    DateTime LastSeenUtc);

public sealed record ChangeGroup(DateTime TimeUtc, string Kind, string Description, int Count, IReadOnlyList<SystemChange> Items);

public sealed record TimelineEntry(
    IReadOnlyList<long> EventIds,
    DateTime TimeUtc,
    DateTime FirstUtc,
    string Kind,
    string Title,
    string? Detail,
    string Severity,
    string? SignatureId,
    int Count);

/// <summary>Turns events, signature matches and findings into plain-language status, issues and timeline.</summary>
public static class Reports
{
    // Findings that aren't tied to one signature borrow fixes from a related one.
    private static readonly Dictionary<string, string> FindingFixSource = new()
    {
        ["thermal_trend"] = "thermal-throttle-frequent",
        ["memory_pressure"] = "low-memory",
        ["startup_regression"] = "slow-boot",
        ["crash_loop"] = "app-crash-loop",
        ["freeze_correlation"] = "app-hang-repeated",
    };

    public static List<Issue> Issues(IReadOnlyList<SignatureMatch> matches, IReadOnlyList<Finding> findings, KnowledgeBase kb)
    {
        var issues = new List<Issue>();
        var usedFindings = new HashSet<Finding>();
        foreach (var m in matches.Where(m => !m.Signature.Noise && m.Signature.SeverityLevel >= Severity.Notice))
        {
            var related = findings.Where(f =>
                f.EventIds.Intersect(m.Events.Select(e => e.Id)).Any()
                || (m.App is not null && string.Equals(f.Subject, RuleValidator.NormalizeProcessName(m.App), StringComparison.OrdinalIgnoreCase))).ToList();
            usedFindings.UnionWith(related);
            var app = m.App ?? m.Events.Select(e => e.AppName).FirstOrDefault(a => a is not null);
            var title = m.Signature.Title.Replace("{app}", kb.FriendlyName(app));
            var evidence = related.Count > 0 ? string.Join(" ", related.Select(f => f.Evidence)) : null;
            // A suspected cause from "what changed?" comes first: undoing the change is often the real fix.
            var changeFixes = related.Where(f => f.RelatedChange is not null).SelectMany(f => ChangeFixes(f.RelatedChange!));
            var severity = related.Any(f => f.Code == "change_correlation") && m.Signature.SeverityLevel < Severity.Warning
                ? Severity.Warning : m.Signature.SeverityLevel;
            issues.Add(new Issue(m.Signature.Id + (m.App is null ? "" : ":" + RuleValidator.NormalizeProcessName(m.App)),
                title, m.Signature.Explanation, evidence, severity.ToString().ToLowerInvariant(),
                changeFixes.Concat(m.Signature.Fixes).DistinctBy(f => f.Label).ToList(), m.Signature.NeedsHuman,
                m.Events.Select(e => e.Id).ToList(), m.LastSeenUtc));
        }

        foreach (var f in findings.Where(f => !usedFindings.Contains(f) && f.Severity >= Severity.Notice))
        {
            var sig = FindingFixSource.TryGetValue(f.Code, out var sid) ? kb.GetSignature(sid) : null;
            IReadOnlyList<Fix> fixes = f.RelatedChange is { } rc ? ChangeFixes(rc).ToList()
                : sig?.Fixes ?? (f.Code == "recurring_hog" && f.Subject is not null
                ? [new Fix($"Close {kb.FriendlyName(f.Subject)} when you're not using it"), new Fix("Turn off apps that start automatically", "ms-settings:startupapps")]
                : []);
            issues.Add(new Issue(f.Code + (f.Subject is null ? "" : ":" + f.Subject), f.Title, f.Evidence, null,
                f.Severity.ToString().ToLowerInvariant(), fixes, false, f.EventIds, f.LastSeenUtc));
        }

        return issues
            .OrderByDescending(i => Enum.Parse<Severity>(i.Severity, true))
            .ThenByDescending(i => i.LastSeenUtc).ToList();
    }

    /// <summary>
    /// Collapses bursts of the same kind of change (one installer often registers 30 components)
    /// into a single readable entry.
    /// </summary>
    public static List<ChangeGroup> GroupChanges(IEnumerable<SystemChange> changes, TimeSpan? gap = null)
    {
        var window = gap ?? TimeSpan.FromMinutes(15);
        var groups = new List<List<SystemChange>>();
        foreach (var c in changes.Where(c => !ChangeKind.IsRoutine(c)).OrderByDescending(c => c.TimeUtc))
        {
            var last = groups.LastOrDefault();
            if (last is not null && last[0].Kind == c.Kind && last[^1].TimeUtc - c.TimeUtc <= window) last.Add(c);
            else groups.Add([c]);
        }
        return groups.Select(g =>
        {
            if (g.Count == 1) return new ChangeGroup(g[0].TimeUtc, g[0].Kind, ChangeKind.Describe(g[0]), 1, g);
            var lead = g.MinBy(c => c.Name.Length)!;
            var verb = g[0].Kind switch
            {
                ChangeKind.AppInstalled => "were installed", ChangeKind.AppRemoved => "were uninstalled",
                ChangeKind.WindowsUpdate => "were installed by Windows Update", ChangeKind.UpdateFailed => "failed to install",
                ChangeKind.DriverInstalled => "were installed (drivers)", ChangeKind.StartupAdded => "now start with Windows",
                ChangeKind.StartupRemoved => "no longer start with Windows", _ => "changed",
            };
            return new ChangeGroup(g[0].TimeUtc, g[0].Kind, $"{lead.Name} and {g.Count - 1} related item{(g.Count == 2 ? "" : "s")} {verb}", g.Count, g);
        }).ToList();
    }

    /// <summary>How to undo or update past a change that's suspected of causing a problem.</summary>
    public static IEnumerable<Fix> ChangeFixes(SystemChange c) => c.Kind switch
    {
        ChangeKind.DriverInstalled =>
        [
            new Fix("Roll back the driver: open Device Manager, right-click the device, Properties > Driver > Roll Back Driver"),
            new Fix("Check for a newer driver", "ms-settings:windowsupdate-optionalupdates"),
        ],
        ChangeKind.WindowsUpdate =>
        [
            new Fix("Check for a newer update that fixes it", "ms-settings:windowsupdate"),
            new Fix("Uninstall the recent update if problems continue", "ms-settings:windowsupdate-history"),
        ],
        ChangeKind.AppInstalled => [new Fix($"Uninstall {c.Name} if you don't need it", "ms-settings:appsfeatures")],
        ChangeKind.StartupAdded => [new Fix($"Stop {c.Name} from starting automatically", "ms-settings:startupapps")],
        _ => [],
    };

    public static HealthStatus Status(IReadOnlyList<Issue> issues, long hiddenWarnings)
    {
        var worst = issues.Count == 0 ? Severity.Info : issues.Max(i => Enum.Parse<Severity>(i.Severity, true));
        var attention = issues.Count(i => Enum.Parse<Severity>(i.Severity, true) >= Severity.Warning);
        // Lead with the actual problem ("SketchPad keeps crashing"), not a vague verdict about the PC.
        var top = issues.Where(i => Enum.Parse<Severity>(i.Severity, true) == worst).MaxBy(i => i.LastSeenUtc);
        var others = attention - 1;
        var more = others > 0 ? $" {Plural(others, "other thing")} need{(others == 1 ? "s" : "")} attention too." : "";
        return worst switch
        {
            Severity.Critical => new("problem", top!.Title, $"{top.Explanation}{more}".Trim(), issues.Count, hiddenWarnings),
            Severity.Warning => new("attention", top!.Title, $"{top.Explanation}{more}".Trim(), issues.Count, hiddenWarnings),
            _ => new("good", "Your PC is healthy",
                issues.Count == 0 ? "Nothing needs your attention." : $"{Plural(issues.Count, "minor note")}, nothing urgent.", issues.Count, hiddenWarnings),
        };
    }

    public static List<TimelineEntry> Timeline(IReadOnlyList<HealthEvent> events, IReadOnlyList<SignatureMatch> matches, KnowledgeBase kb)
    {
        var entries = new List<TimelineEntry>();
        TimelineEntry? current = null;
        foreach (var e in events.Where(e => e.Kind != HealthKind.Noise).OrderByDescending(e => e.TimeUtc))
        {
            var sig = SignatureMatcher.ForEvent(matches, e.Id)?.Signature;
            var severity = (sig?.SeverityLevel ?? Severity.Info).ToString().ToLowerInvariant();
            var (title, detail) = Describe(e, kb);
            // Collapse bursts of the same thing (e.g. 12 heat slow-downs in an hour) into one entry.
            if (current is not null && current.Kind == e.Kind && current.Title == title && current.FirstUtc - e.TimeUtc < TimeSpan.FromHours(2))
            {
                current = current with { EventIds = [.. current.EventIds, e.Id], FirstUtc = e.TimeUtc, Count = current.Count + 1 };
                entries[^1] = current;
                continue;
            }
            current = new TimelineEntry([e.Id], e.TimeUtc, e.TimeUtc, e.Kind, title, detail, severity, sig?.Id, 1);
            entries.Add(current);
        }
        return entries;
    }

    public static (string Title, string? Detail) Describe(HealthEvent e, KnowledgeBase kb)
    {
        var app = kb.FriendlyName(e.AppName);
        return e.Kind switch
        {
            HealthKind.AppHang => e.Data.TryGetValue("DurationSeconds", out var d)
                ? ($"{app} froze", $"Stopped responding for about {double.Parse(d, CultureInfo.InvariantCulture):0} seconds.")
                : ($"{app} froze", "Windows recorded that it stopped responding."),
            HealthKind.AppCrash => ($"{app} closed unexpectedly", null),
            HealthKind.BlueScreen => ("Your PC crashed with a blue screen", null),
            HealthKind.UnexpectedShutdown => e.Data.GetValueOrDefault("BugcheckCode") is "0" or null
                ? ("Your PC turned off unexpectedly", "It wasn't shut down normally (for example, power loss or holding the power button).")
                : ("Your PC restarted after a crash", null),
            HealthKind.DisplayDriverReset => ("Your graphics driver restarted", "The screen may have flickered or gone black for a moment."),
            HealthKind.SlowBoot => e.Data.TryGetValue("BootTime", out var bt) && double.TryParse(bt, CultureInfo.InvariantCulture, out var ms)
                ? ($"Startup took {ms / 1000:0} seconds", null)
                : ("Startup was measured", null),
            HealthKind.ThermalThrottle => ("Your PC slowed down because of heat", null),
            HealthKind.DiskError => ("Your drive reported an error", null),
            HealthKind.LowMemory => ("Your PC ran out of memory", null),
            HealthKind.HardwareError => ("Your hardware reported an error", null),
            HealthKind.DriveHealthWarning => ($"Drive health changed: {e.Data.GetValueOrDefault("Model") ?? "a drive"}", e.Summary),
            HealthKind.NetworkChange => (e.Summary, e.Data.GetValueOrDefault("RouterReachableNow") is "False" ? "Your router wasn't answering." : null),
            HealthKind.WifiConnect => ($"Wi-Fi connected{(e.Data.GetValueOrDefault("SSID") is { Length: > 0 } ssid ? $" to '{ssid}'" : "")}", null),
            HealthKind.WifiDrop => ("Wi-Fi disconnected", e.Data.GetValueOrDefault("Reason") is { Length: > 0 } reason ? reason : null),
            _ => (e.Summary, null),
        };
    }

    private static string Plural(int n, string word) => $"{(n == 1 ? "One" : n.ToString())} {word}{(n == 1 ? "" : "s")}";
}
