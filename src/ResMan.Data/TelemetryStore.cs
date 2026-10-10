using Microsoft.Data.Sqlite;
using ResMan.Core;

namespace ResMan.Data;

public sealed class TelemetryStore(ResManDb db)
{
    public long Insert(Snapshot s)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO snapshots(time_utc,total_cpu,mem_used_pct,mem_avail,on_battery)
            VALUES($t,$cpu,$mem,$avail,$bat); SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$t", ResManDb.ToMs(s.TimeUtc));
        cmd.Parameters.AddWithValue("$cpu", s.TotalCpuPercent);
        cmd.Parameters.AddWithValue("$mem", s.MemoryUsedPercent);
        cmd.Parameters.AddWithValue("$avail", s.MemoryAvailableBytes);
        cmd.Parameters.AddWithValue("$bat", s.OnBattery ? 1 : 0);
        var id = (long)cmd.ExecuteScalar()!;

        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO process_samples VALUES($s,$pid,$n,$cpu,$ws,$disk,$gpu)";
        var pS = ins.Parameters.Add("$s", SqliteType.Integer);
        var pPid = ins.Parameters.Add("$pid", SqliteType.Integer);
        var pN = ins.Parameters.Add("$n", SqliteType.Text);
        var pCpu = ins.Parameters.Add("$cpu", SqliteType.Real);
        var pWs = ins.Parameters.Add("$ws", SqliteType.Integer);
        var pDisk = ins.Parameters.Add("$disk", SqliteType.Integer);
        var pGpu = ins.Parameters.Add("$gpu", SqliteType.Real);
        foreach (var p in s.Top)
        {
            pS.Value = id; pPid.Value = p.Pid; pN.Value = p.Name; pCpu.Value = p.CpuPercent;
            pWs.Value = p.WorkingSetBytes; pDisk.Value = p.DiskBytesPerSec; pGpu.Value = p.GpuPercent;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    public Snapshot? Get(long id) => Query("WHERE id=$a", ("$a", id)).FirstOrDefault();

    public Snapshot? Latest() => Query("ORDER BY time_utc DESC LIMIT 1").FirstOrDefault();

    /// <summary>The snapshot closest to <paramref name="timeUtc"/>, if one exists within <paramref name="maxDistance"/>.</summary>
    public Snapshot? Nearest(DateTime timeUtc, TimeSpan maxDistance)
    {
        var t = ResManDb.ToMs(timeUtc);
        var s = Query("WHERE time_utc BETWEEN $a AND $b ORDER BY ABS(time_utc-$t) LIMIT 1",
            ("$a", t - (long)maxDistance.TotalMilliseconds), ("$b", t + (long)maxDistance.TotalMilliseconds), ("$t", t)).FirstOrDefault();
        return s;
    }

    public IReadOnlyList<Snapshot> Range(DateTime fromUtc, DateTime toUtc, int limit = 10_000) =>
        Query("WHERE time_utc BETWEEN $a AND $b ORDER BY time_utc LIMIT $l",
            ("$a", ResManDb.ToMs(fromUtc)), ("$b", ResManDb.ToMs(toUtc)), ("$l", limit));

    public long Count()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snapshots";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Rolls raw data older than <paramref name="keepRaw"/> into hourly summaries and deletes it.</summary>
    public int ApplyRetention(DateTime nowUtc, TimeSpan keepRaw)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$cut", ResManDb.ToMs(nowUtc - keepRaw));
        cmd.CommandText = """
            INSERT INTO hourly_summary(hour_utc,avg_cpu,avg_mem,max_mem,samples)
            SELECT (time_utc/3600000)*3600000, AVG(total_cpu), AVG(mem_used_pct), MAX(mem_used_pct), COUNT(*)
            FROM snapshots WHERE time_utc < $cut GROUP BY time_utc/3600000
            ON CONFLICT(hour_utc) DO UPDATE SET
              avg_cpu=(avg_cpu*samples + excluded.avg_cpu*excluded.samples)/(samples+excluded.samples),
              avg_mem=(avg_mem*samples + excluded.avg_mem*excluded.samples)/(samples+excluded.samples),
              max_mem=MAX(max_mem, excluded.max_mem),
              samples=samples+excluded.samples;
            DELETE FROM snapshots WHERE time_utc < $cut;
            """;
        var n = cmd.ExecuteNonQuery();
        tx.Commit();
        return n;
    }

    private List<Snapshot> Query(string where, params (string, object)[] args)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id,time_utc,total_cpu,mem_used_pct,mem_avail,on_battery FROM snapshots {where}";
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var heads = new List<(long id, DateTime t, double cpu, double mem, long avail, bool bat)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                heads.Add((r.GetInt64(0), ResManDb.FromMs(r.GetInt64(1)), r.GetDouble(2), r.GetDouble(3), r.GetInt64(4), r.GetInt64(5) != 0));

        if (heads.Count == 0) return [];

        // One query for all samples in the id span, instead of one per snapshot.
        var wanted = heads.Select(h => h.id).ToHashSet();
        var samples = new Dictionary<long, List<ProcessSample>>();
        using (var pc = c.CreateCommand())
        {
            pc.CommandText = "SELECT snapshot_id,pid,name,cpu,ws,disk,gpu FROM process_samples WHERE snapshot_id BETWEEN $a AND $b";
            pc.Parameters.AddWithValue("$a", wanted.Min());
            pc.Parameters.AddWithValue("$b", wanted.Max());
            using var r = pc.ExecuteReader();
            while (r.Read())
            {
                var sid = r.GetInt64(0);
                if (!wanted.Contains(sid)) continue;
                if (!samples.TryGetValue(sid, out var list)) samples[sid] = list = [];
                list.Add(new ProcessSample(r.GetInt32(1), r.GetString(2), r.GetDouble(3), r.GetInt64(4), r.GetInt64(5), r.GetDouble(6)));
            }
        }
        return heads.Select(h => new Snapshot(h.id, h.t, h.cpu, h.mem, h.avail, h.bat, samples.GetValueOrDefault(h.id) ?? [])).ToList();
    }
}
