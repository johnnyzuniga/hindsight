using ResMan.Brain.Doctor;
using ResMan.Brain.Knowledge;
using ResMan.Brain.Models;
using ResMan.Core;
using ResMan.Data;

namespace ResMan.Tests;

/// <summary>A throwaway database file plus helpers for building fixture data.</summary>
public sealed class TestDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"resman-test-{Guid.NewGuid():N}.db");
    public ResManDb Db { get; }
    public TelemetryStore Telemetry { get; }
    public HealthStore Health { get; }
    public ChangeStore Changes { get; }
    public FixStore Fixes { get; }

    public TestDb()
    {
        Db = new ResManDb(Path);
        Telemetry = new TelemetryStore(Db);
        Health = new HealthStore(Db);
        Changes = new ChangeStore(Db);
        Fixes = new FixStore(Db);
    }

    public static readonly KnowledgeBase Kb = KnowledgeBase.LoadDefault();

    public DoctorService Doctor() => new(Db, Kb);

    public long AddEvent(string kind, DateTime timeUtc, string? app = null, Dictionary<string, string>? data = null, long? snapshotId = null,
        string provider = "Test", int eventId = 1) =>
        Health.Insert(new HealthEvent(0, timeUtc, "Test", provider, eventId, 2, kind, app, $"{kind} test event",
            data ?? [], snapshotId))!.Value;

    public long AddSnapshot(DateTime timeUtc, double memPct, double cpuPct, params ProcessSample[] top) =>
        Telemetry.Insert(new Snapshot(0, timeUtc, cpuPct, memPct, 1L << 30, false, top));

    public void AddChange(string kind, string name, DateTime timeUtc) =>
        Changes.Insert(new SystemChange(0, timeUtc, kind, name, null, "test", $"{kind}:{name}:{timeUtc.Ticks}"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
            try { File.Delete(f); } catch { }
    }
}

/// <summary>A model that replays scripted replies, so the harness can be tested without real inference.</summary>
public sealed class ScriptedModel(params string[] replies) : IModelProvider
{
    private int _next;
    public List<Core.InferenceRequest> Requests { get; } = [];
    public bool Enabled => true;
    public string Describe => "scripted test model";

    public Task<ModelResult> CompleteAsync(Core.InferenceRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var text = _next < replies.Length ? replies[_next++] : replies[^1];
        return Task.FromResult(new ModelResult(text, null, 5, "test"));
    }
}
