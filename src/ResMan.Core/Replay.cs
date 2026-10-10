namespace ResMan.Core;

/// <summary>One point of the high-resolution "black box" series kept in memory and saved around incidents.</summary>
public sealed record ReplaySample(
    DateTime TimeUtc,
    double CpuPercent,
    double MemoryPercent,
    long DiskBytesPerSec,
    IReadOnlyList<ReplayProcess> Top);

public sealed record ReplayProcess(string Name, double CpuPercent, long WorkingSetMb, long DiskBytesPerSec);

/// <summary>Network state sampled by the service every 10 s. Works fully offline (no internet traffic).</summary>
public sealed record NetworkSample(
    DateTime TimeUtc,
    string Connectivity,
    string? Ssid,
    int? SignalBars,
    bool IsWifi,
    string? AdapterName,
    string? Gateway,
    int? GatewayPingMs,
    bool GatewayReachable);

public static class Connectivity
{
    public const string None = "none";
    public const string LocalOnly = "local_only";
    public const string Constrained = "constrained";
    public const string Internet = "internet";
}
