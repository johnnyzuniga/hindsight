using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResMan.Core;

namespace ResMan.Data;

public sealed class HealthStore(ResManDb db)
{
    /// <summary>Inserts an event. Returns the new id, or null if the (log, record id) pair is already stored.</summary>
    public long? Insert(HealthEvent e)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO health_events(time_utc,log,provider,event_id,level,kind,app_name,summary,data_json,snapshot_id,record_id)
            VALUES($t,$log,$p,$eid,$lvl,$k,$app,$sum,$data,$snap,$rec);
            SELECT CASE WHEN changes()=0 THEN NULL ELSE last_insert_rowid() END;
            """;
        cmd.Parameters.AddWithValue("$t", ResManDb.ToMs(e.TimeUtc));
        cmd.Parameters.AddWithValue("$log", e.Log);
        cmd.Parameters.AddWithValue("$p", e.Provider);
        cmd.Parameters.AddWithValue("$eid", e.EventId);
        cmd.Parameters.AddWithValue("$lvl", e.Level);
        cmd.Parameters.AddWithValue("$k", e.Kind);
        cmd.Parameters.AddWithValue("$app", (object?)e.AppName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sum", e.Summary);
        cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(e.Data));
        cmd.Parameters.AddWithValue("$snap", (object?)e.SnapshotId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rec", (object?)e.RecordId ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is long id ? id : null;
    }

    public HealthEvent? Get(long id) => Query("WHERE id=$a", ("$a", id)).FirstOrDefault();

    /// <summary>
    /// A crash of the same app recorded within a few seconds. Windows logs one .NET app crash twice
    /// (".NET Runtime" 1026 and "Application Error" 1000), and both must count as one crash.
    /// </summary>
    public HealthEvent? FindSameCrash(string? app, string provider, DateTime timeUtc, TimeSpan window)
    {
        if (string.IsNullOrEmpty(app)) return null;
        return Query("WHERE kind='app_crash' AND provider <> $p AND lower(app_name)=lower($a) AND time_utc BETWEEN $f AND $t ORDER BY id LIMIT 1",
            ("$a", app), ("$p", provider), ("$f", ResManDb.ToMs(timeUtc - window)), ("$t", ResManDb.ToMs(timeUtc + window))).FirstOrDefault();
    }

    /// <summary>Adds details the stored event lacks (e.g. the .NET exception text from the twin event).</summary>
    public void MergeData(long id, IReadOnlyDictionary<string, string> extra)
    {
        if (Get(id) is not { } e) return;
        var data = new Dictionary<string, string>(e.Data);
        var changed = false;
        foreach (var (k, v) in extra)
            if (v.Length > 0 && (!data.TryGetValue(k, out var old) || old.Length == 0)) { data[k] = v; changed = true; }
        if (!changed) return;
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE health_events SET data_json=$d WHERE id=$i";
        cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(data));
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>One-off cleanup for crashes stored twice before de-duplication existed; returns how many were removed.</summary>
    public int RemoveDuplicateCrashes()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM health_events WHERE kind='app_crash' AND provider <> '.NET Runtime' AND EXISTS (
              SELECT 1 FROM health_events d WHERE d.kind='app_crash' AND d.provider='.NET Runtime'
                AND lower(d.app_name)=lower(health_events.app_name) AND abs(d.time_utc - health_events.time_utc) <= 3000);
            """;
        var removed = cmd.ExecuteNonQuery();
        // Their black-box samples go too, or a later event that reuses the id would inherit them.
        cmd.CommandText = "DELETE FROM replay_samples WHERE event_id NOT IN (SELECT id FROM health_events)";
        cmd.ExecuteNonQuery();
        return removed;
    }

    public IReadOnlyList<HealthEvent> Range(DateTime fromUtc, DateTime toUtc, bool includeNoise = false, int limit = 5000) =>
        Query($"WHERE time_utc BETWEEN $a AND $b {(includeNoise ? "" : "AND kind <> 'noise'")} ORDER BY time_utc DESC LIMIT $l",
            ("$a", ResManDb.ToMs(fromUtc)), ("$b", ResManDb.ToMs(toUtc)), ("$l", limit));

    public IReadOnlyList<HealthEvent> After(long lastId, int limit = 200) =>
        Query("WHERE id > $a ORDER BY id LIMIT $l", ("$a", lastId), ("$l", limit));

    public long CountNoise(DateTime fromUtc, DateTime toUtc)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM health_events WHERE kind='noise' AND time_utc BETWEEN $a AND $b";
        cmd.Parameters.AddWithValue("$a", ResManDb.ToMs(fromUtc));
        cmd.Parameters.AddWithValue("$b", ResManDb.ToMs(toUtc));
        return (long)cmd.ExecuteScalar()!;
    }

    public long Count()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM health_events";
        return (long)cmd.ExecuteScalar()!;
    }

    public long MaxId()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT IFNULL(MAX(id),0) FROM health_events";
        return (long)cmd.ExecuteScalar()!;
    }

    public void DeleteOlderThan(DateTime cutoffUtc)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM health_events WHERE time_utc < $c";
        cmd.Parameters.AddWithValue("$c", ResManDb.ToMs(cutoffUtc));
        cmd.ExecuteNonQuery();
    }

    private List<HealthEvent> Query(string where, params (string, object)[] args)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id,time_utc,log,provider,event_id,level,kind,app_name,summary,data_json,snapshot_id,record_id FROM health_events {where}";
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var list = new List<HealthEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(9)) ?? [];
            list.Add(new HealthEvent(
                r.GetInt64(0), ResManDb.FromMs(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5),
                r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), data,
                r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11)));
        }
        return list;
    }
}
