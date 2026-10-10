using ResMan.Core;

namespace ResMan.Data;

/// <summary>"What changed?" records plus the inventory snapshots they are diffed from.</summary>
public sealed class ChangeStore(ResManDb db)
{
    /// <summary>Inserts a change. Returns false if a change with the same key was already recorded.</summary>
    public bool Insert(SystemChange c)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO changes(time_utc,kind,name,detail,source,key) VALUES($t,$k,$n,$d,$s,$key)";
        cmd.Parameters.AddWithValue("$t", ResManDb.ToMs(c.TimeUtc));
        cmd.Parameters.AddWithValue("$k", c.Kind);
        cmd.Parameters.AddWithValue("$n", c.Name);
        cmd.Parameters.AddWithValue("$d", (object?)c.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", c.Source);
        cmd.Parameters.AddWithValue("$key", c.Key);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<SystemChange> Range(DateTime fromUtc, DateTime toUtc, int limit = 2000)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,time_utc,kind,name,detail,source,key FROM changes WHERE time_utc BETWEEN $a AND $b ORDER BY time_utc DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$a", ResManDb.ToMs(fromUtc));
        cmd.Parameters.AddWithValue("$b", ResManDb.ToMs(toUtc));
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<SystemChange>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SystemChange(r.GetInt64(0), ResManDb.FromMs(r.GetInt64(1)), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6)));
        return list;
    }

    public bool HasBaseline(string kind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM inventory_baseline WHERE kind=$k";
        cmd.Parameters.AddWithValue("$k", kind);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public Dictionary<string, string> Inventory(string kind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key,value FROM inventory WHERE kind=$k";
        cmd.Parameters.AddWithValue("$k", kind);
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    /// <summary>Replaces the stored inventory for a kind and marks the baseline as taken.</summary>
    public void ReplaceInventory(string kind, IReadOnlyDictionary<string, string> items, DateTime nowUtc)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM inventory WHERE kind=$k";
            del.Parameters.AddWithValue("$k", kind);
            del.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO inventory(kind,key,value,first_seen_utc) VALUES($k,$key,$v,$t)";
            var pK = ins.Parameters.AddWithValue("$k", kind);
            var pKey = ins.Parameters.AddWithValue("$key", "");
            var pV = ins.Parameters.AddWithValue("$v", "");
            ins.Parameters.AddWithValue("$t", ResManDb.ToMs(nowUtc));
            foreach (var (key, value) in items) { pKey.Value = key; pV.Value = value; ins.ExecuteNonQuery(); }
        }
        using (var b = conn.CreateCommand())
        {
            b.Transaction = tx;
            b.CommandText = "INSERT OR REPLACE INTO inventory_baseline(kind,time_utc) VALUES($k,$t)";
            b.Parameters.AddWithValue("$k", kind);
            b.Parameters.AddWithValue("$t", ResManDb.ToMs(nowUtc));
            b.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
