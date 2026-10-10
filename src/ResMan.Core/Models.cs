namespace ResMan.Core;

/// <summary>One process's resource use at a point in time.</summary>
public sealed record ProcessSample(
    int Pid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    long DiskBytesPerSec,
    double GpuPercent);

/// <summary>Whole-system resource snapshot plus the top processes.</summary>
public sealed record Snapshot(
    long Id,
    DateTime TimeUtc,
    double TotalCpuPercent,
    double MemoryUsedPercent,
    long MemoryAvailableBytes,
    bool OnBattery,
    IReadOnlyList<ProcessSample> Top);

/// <summary>A normalized Windows health event (crash, hang, blue screen, ...).</summary>
public sealed record HealthEvent(
    long Id,
    DateTime TimeUtc,
    string Log,
    string Provider,
    int EventId,
    int Level,
    string Kind,
    string? AppName,
    string Summary,
    IReadOnlyDictionary<string, string> Data,
    long? SnapshotId,
    long? RecordId = null);

public static class HealthKind
{
    public const string AppHang = "app_hang";
    public const string AppCrash = "app_crash";
    public const string BlueScreen = "blue_screen";
    public const string UnexpectedShutdown = "unexpected_shutdown";
    public const string DisplayDriverReset = "display_driver_reset";
    public const string SlowBoot = "boot_performance";
    public const string ThermalThrottle = "thermal_throttle";
    public const string DiskError = "disk_error";
    public const string LowMemory = "low_memory";
    public const string WifiDrop = "wifi_drop";
    public const string HardwareError = "hardware_error";
    public const string DriveHealthWarning = "drive_health";
    public const string NetworkChange = "network_change";
    public const string WifiConnect = "wifi_connect";
    public const string Noise = "noise";
    public const string Other = "other";
}

public enum Severity { Info = 0, Notice = 1, Warning = 2, Critical = 3 }

/// <summary>A deterministic conclusion produced by an analyzer.</summary>
public sealed record Finding(
    string Code,
    Severity Severity,
    string Title,
    string Evidence,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    int Count,
    string? Subject,
    IReadOnlyList<long> EventIds,
    SystemChange? RelatedChange = null);
