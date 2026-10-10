using Microsoft.Data.Sqlite;
using ResMan.Core;

namespace ResMan.Data;

public sealed class DriveHealthStore(ResManDb db)
{
    public void Insert(DriveHealth h)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO drive_health(time_utc,device_id,model,media_type,health_status,wear_pct,read_errors,write_errors,temperature_c,power_on_hours)
            VALUES($t,$d,$m,$mt,$h,$w,$re,$we,$temp,$poh)
            """;
        cmd.Parameters.AddWithValue("$t", ResManDb.ToMs(h.TimeUtc));
        cmd.Parameters.AddWithValue("$d", h.DeviceId);
        cmd.Parameters.AddWithValue("$m", h.Model);
        cmd.Parameters.AddWithValue("$mt", h.MediaType);
        cmd.Parameters.AddWithValue("$h", h.HealthStatus);
        cmd.Parameters.AddWithValue("$w", (object?)h.WearPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$re", (object?)h.ReadErrorsTotal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$we", (object?)h.WriteErrorsTotal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$temp", (object?)h.TemperatureC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$poh", (object?)h.PowerOnHours ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Most recent reading per drive.</summary>
    public IReadOnlyList<DriveHealth> Latest()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT d.* FROM drive_health d
            JOIN (SELECT device_id, MAX(time_utc) t FROM drive_health GROUP BY device_id) m
              ON d.device_id = m.device_id AND d.time_utc = m.t
            ORDER BY d.device_id
            """;
        return Read(cmd);
    }

    public IReadOnlyList<DriveHealth> History(string deviceId, DateTime fromUtc)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM drive_health WHERE device_id=$d AND time_utc >= $f ORDER BY time_utc";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$f", ResManDb.ToMs(fromUtc));
        return Read(cmd);
    }

    private static List<DriveHealth> Read(SqliteCommand cmd)
    {
        var list = new List<DriveHealth>();
        using var r = cmd.ExecuteReader();
        int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        while (r.Read())
            list.Add(new DriveHealth(ResManDb.FromMs(r.GetInt64(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                I(6), L(7), L(8), I(9), L(10)));
        return list;
    }
}
