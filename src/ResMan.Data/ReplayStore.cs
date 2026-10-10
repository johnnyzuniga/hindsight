using System.Text.Json;
using ResMan.Core;

namespace ResMan.Data;

/// <summary>The "black box" series saved around incidents, plus the network history.</summary>
public sealed class ReplayStore(ResManDb db)
{
    public void Save(long eventId, IEnumerable<ReplaySample> samples)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO replay_samples(event_id,time_utc,cpu,mem,disk,top_json) VALUES($e,$t,$c,$m,$d,$j)";
        var pE = cmd.Parameters.AddWithValue("$e", eventId);
        var pT = cmd.Parameters.AddWithValue("$t", 0L);
        var pC = cmd.Parameters.AddWithValue("$c", 0.0);
        var pM = cmd.Parameters.AddWithValue("$m", 0.0);
        var pD = cmd.Parameters.AddWithValue("$d", 0L);
        var pJ = cmd.Parameters.AddWithValue("$j", "");
        foreach (var s in samples)
        {
            pT.Value = ResManDb.ToMs(s.TimeUtc); pC.Value = s.CpuPercent; pM.Value = s.MemoryPercent;
            pD.Value = s.DiskBytesPerSec; pJ.Value = JsonSerializer.Serialize(s.Top);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// The black-box series for an event, limited to the minutes just before it, so samples can
    /// never leak in from an older event that once had the same id.
    /// </summary>
    public IReadOnlyList<ReplaySample> Get(long eventId, DateTime incidentUtc)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time_utc,cpu,mem,disk,top_json FROM replay_samples WHERE event_id=$e AND time_utc BETWEEN $f AND $t ORDER BY time_utc";
        cmd.Parameters.AddWithValue("$e", eventId);
        cmd.Parameters.AddWithValue("$f", ResManDb.ToMs(incidentUtc.AddSeconds(-130)));
        cmd.Parameters.AddWithValue("$t", ResManDb.ToMs(incidentUtc.AddSeconds(10)));
        var list = new List<ReplaySample>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ReplaySample(ResManDb.FromMs(r.GetInt64(0)), r.GetDouble(1), r.GetDouble(2), r.GetInt64(3),
                JsonSerializer.Deserialize<List<ReplayProcess>>(r.GetString(4)) ?? []));
        return list;
    }

    public void InsertNetwork(NetworkSample n)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO network_samples(time_utc,connectivity,ssid,signal_bars,is_wifi,adapter,gateway,gateway_ping_ms,gateway_ok)
            VALUES($t,$c,$s,$b,$w,$a,$g,$p,$ok)
            """;
        cmd.Parameters.AddWithValue("$t", ResManDb.ToMs(n.TimeUtc));
        cmd.Parameters.AddWithValue("$c", n.Connectivity);
        cmd.Parameters.AddWithValue("$s", (object?)n.Ssid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", (object?)n.SignalBars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", n.IsWifi ? 1 : 0);
        cmd.Parameters.AddWithValue("$a", (object?)n.AdapterName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$g", (object?)n.Gateway ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (object?)n.GatewayPingMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ok", n.GatewayReachable ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<NetworkSample> Network(DateTime fromUtc, DateTime toUtc)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM network_samples WHERE time_utc BETWEEN $a AND $b ORDER BY time_utc";
        cmd.Parameters.AddWithValue("$a", ResManDb.ToMs(fromUtc));
        cmd.Parameters.AddWithValue("$b", ResManDb.ToMs(toUtc));
        var list = new List<NetworkSample>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new NetworkSample(ResManDb.FromMs(r.GetInt64(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt32(3), r.GetInt64(4) != 0, r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.GetInt64(8) != 0));
        return list;
    }

    public void DeleteNetworkOlderThan(DateTime cutoffUtc)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM network_samples WHERE time_utc < $c";
        cmd.Parameters.AddWithValue("$c", ResManDb.ToMs(cutoffUtc));
        cmd.ExecuteNonQuery();
    }
}
