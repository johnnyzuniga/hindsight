using Microsoft.Data.Sqlite;

namespace ResMan.Data;

/// <summary>Opens and migrates the ResMan SQLite database. The service is the only writer.</summary>
public sealed class ResManDb
{
    private readonly string _connectionString;
    public string Path { get; }
    public bool ReadOnly { get; }

    public ResManDb(string path, bool readOnly = false)
    {
        Path = path;
        ReadOnly = readOnly;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();

        if (!readOnly)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            using var c = Open();
            Migrate(c);
        }
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ReadOnly
            ? "PRAGMA busy_timeout=3000;"
            : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE snapshots(
            id INTEGER PRIMARY KEY,
            time_utc INTEGER NOT NULL,
            total_cpu REAL NOT NULL,
            mem_used_pct REAL NOT NULL,
            mem_avail INTEGER NOT NULL,
            on_battery INTEGER NOT NULL);
        CREATE INDEX ix_snapshots_time ON snapshots(time_utc);
        CREATE TABLE process_samples(
            snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
            pid INTEGER NOT NULL,
            name TEXT NOT NULL,
            cpu REAL NOT NULL,
            ws INTEGER NOT NULL,
            disk INTEGER NOT NULL,
            gpu REAL NOT NULL);
        CREATE INDEX ix_samples_snapshot ON process_samples(snapshot_id);
        CREATE TABLE health_events(
            id INTEGER PRIMARY KEY,
            time_utc INTEGER NOT NULL,
            log TEXT NOT NULL,
            provider TEXT NOT NULL,
            event_id INTEGER NOT NULL,
            level INTEGER NOT NULL,
            kind TEXT NOT NULL,
            app_name TEXT,
            summary TEXT NOT NULL,
            data_json TEXT NOT NULL,
            snapshot_id INTEGER,
            record_id INTEGER,
            UNIQUE(log, record_id));
        CREATE INDEX ix_events_time ON health_events(time_utc);
        CREATE INDEX ix_events_kind ON health_events(kind);
        CREATE TABLE rules(
            id INTEGER PRIMARY KEY,
            process TEXT NOT NULL,
            action TEXT NOT NULL,
            value TEXT NOT NULL,
            profile TEXT NOT NULL,
            reason TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            created_utc INTEGER NOT NULL);
        CREATE TABLE rule_history(
            id INTEGER PRIMARY KEY,
            rule_id INTEGER NOT NULL,
            change TEXT NOT NULL,
            time_utc INTEGER NOT NULL,
            rule_json TEXT NOT NULL);
        CREATE TABLE hourly_summary(
            hour_utc INTEGER PRIMARY KEY,
            avg_cpu REAL NOT NULL,
            avg_mem REAL NOT NULL,
            max_mem REAL NOT NULL,
            samples INTEGER NOT NULL);
        """,
        """
        CREATE TABLE changes(
            id INTEGER PRIMARY KEY,
            time_utc INTEGER NOT NULL,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            detail TEXT,
            source TEXT NOT NULL,
            key TEXT NOT NULL UNIQUE);
        CREATE INDEX ix_changes_time ON changes(time_utc);
        CREATE TABLE inventory(
            kind TEXT NOT NULL,
            key TEXT NOT NULL,
            value TEXT NOT NULL,
            first_seen_utc INTEGER NOT NULL,
            PRIMARY KEY(kind, key));
        CREATE TABLE inventory_baseline(kind TEXT PRIMARY KEY, time_utc INTEGER NOT NULL);
        CREATE TABLE drive_health(
            id INTEGER PRIMARY KEY,
            time_utc INTEGER NOT NULL,
            device_id TEXT NOT NULL,
            model TEXT NOT NULL,
            media_type TEXT NOT NULL,
            health_status TEXT NOT NULL,
            wear_pct INTEGER,
            read_errors INTEGER,
            write_errors INTEGER,
            temperature_c INTEGER,
            power_on_hours INTEGER);
        CREATE INDEX ix_drive_health ON drive_health(device_id, time_utc);
        CREATE TABLE fix_attempts(
            id INTEGER PRIMARY KEY,
            issue_key TEXT NOT NULL,
            problem_kind TEXT NOT NULL,
            app TEXT,
            label TEXT NOT NULL,
            applied_utc INTEGER NOT NULL);
        """,
        """
        CREATE TABLE replay_samples(
            event_id INTEGER NOT NULL,
            time_utc INTEGER NOT NULL,
            cpu REAL NOT NULL,
            mem REAL NOT NULL,
            disk INTEGER NOT NULL,
            top_json TEXT NOT NULL);
        CREATE INDEX ix_replay_event ON replay_samples(event_id, time_utc);
        CREATE TABLE network_samples(
            time_utc INTEGER NOT NULL,
            connectivity TEXT NOT NULL,
            ssid TEXT,
            signal_bars INTEGER,
            is_wifi INTEGER NOT NULL,
            adapter TEXT,
            gateway TEXT,
            gateway_ping_ms INTEGER,
            gateway_ok INTEGER NOT NULL);
        CREATE INDEX ix_network_time ON network_samples(time_utc);
        """,
    ];

    private static void Migrate(SqliteConnection c)
    {
        using var get = c.CreateCommand();
        get.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(get.ExecuteScalar());
        for (var i = version; i < Migrations.Length; i++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[i] + $"\nPRAGMA user_version={i + 1};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    internal static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    internal static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
