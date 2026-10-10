using ResMan.Brain.Analyzers;
using ResMan.Brain.Knowledge;
using ResMan.Core;
using ResMan.Data;

namespace ResMan.Brain.Doctor;

public sealed record Analysis(
    DateTime NowUtc,
    IReadOnlyList<HealthEvent> Events,
    IReadOnlyList<SignatureMatch> Matches,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Issue> Issues,
    HealthStatus Status,
    Snapshot? Latest,
    IReadOnlyList<SystemChange> Changes,
    IReadOnlyList<DriveHealth> Drives,
    IReadOnlyList<FixOutcome> Fixes);

/// <summary>
/// Read-only view over the service's database plus the knowledge base. Runs the analyzers and
/// signature matcher (cached briefly) and builds the plain-language reports.
/// </summary>
public sealed class DoctorService(ResManDb db, KnowledgeBase kb)
{
    private readonly TelemetryStore _telemetry = new(db);
    private readonly HealthStore _health = new(db);
    private readonly ChangeStore _changes = new(db);
    private readonly DriveHealthStore _drives = new(db);
    private readonly FixStore _fixes = new(db);
    private readonly ReplayStore _replays = new(db);

    public ReplayStore Replays => _replays;
    private readonly Lock _lock = new();
    private Analysis? _cached;

    public KnowledgeBase Knowledge => kb;
    public TelemetryStore Telemetry => _telemetry;
    public HealthStore Health => _health;
    public ChangeStore Changes => _changes;

    public Analysis Analyze(bool fresh = false)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!fresh && _cached is not null && now - _cached.NowUtc < TimeSpan.FromSeconds(15)) return _cached;
            return _cached = AnalyzeAt(now);
        }
    }

    /// <summary>Analysis as of a given time. Used directly by tests and evals with fixture data.</summary>
    public Analysis AnalyzeAt(DateTime nowUtc)
    {
        var events = _health.Range(nowUtc.AddDays(-60), nowUtc, includeNoise: true, limit: 20_000);
        var snapshots = _telemetry.Range(nowUtc.AddHours(-24), nowUtc);
        var changes = _changes.Range(nowUtc.AddDays(-60), nowUtc);
        var snapCache = snapshots.ToDictionary(s => s.Id);
        Snapshot? GetSnap(long id) => snapCache.TryGetValue(id, out var s) ? s : _telemetry.Get(id);

        var problems = events.Where(e => e.Kind != HealthKind.Noise).ToList();
        var matches = SignatureMatcher.Match(kb.Signatures, events, nowUtc);
        var findings = AnalyzerSuite.Run(new AnalysisContext(nowUtc, problems, snapshots, GetSnap, kb.FriendlyName, changes));
        var issues = Reports.Issues(matches, findings, kb);
        var noise = events.Count(e => e.Kind == HealthKind.Noise && e.TimeUtc >= nowUtc.AddDays(-7));
        var latest = snapshots.Count > 0 ? snapshots[^1] : _telemetry.Latest();

        var historyStart = events.Count > 0 ? events.Min(e => e.TimeUtc) : nowUtc;
        var fixes = _fixes.List().Select(f => FixOutcomes.Evaluate(f, problems, nowUtc, historyStart)).ToList();

        return new Analysis(nowUtc, events, matches, findings, issues, Reports.Status(issues, noise), latest,
            changes, _drives.Latest(), fixes);
    }

    public IReadOnlyList<TimelineEntry> Timeline(int days = 7)
    {
        var a = Analyze();
        var from = a.NowUtc.AddDays(-days);
        return Reports.Timeline(a.Events.Where(e => e.TimeUtc >= from).ToList(), a.Matches, kb);
    }

    /// <summary>Changes in the few days before a moment, newest first. Used to give explanations context.</summary>
    public IReadOnlyList<SystemChange> ChangesBefore(DateTime timeUtc, TimeSpan window) =>
        Analyze().Changes.Where(c => !ChangeKind.IsRoutine(c) && c.TimeUtc <= timeUtc && c.TimeUtc >= timeUtc - window).ToList();
}
