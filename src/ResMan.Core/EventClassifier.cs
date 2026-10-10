namespace ResMan.Core;

/// <summary>Maps raw Windows events (log, provider, id) to a <see cref="HealthKind"/>.</summary>
public static class EventClassifier
{
    public sealed record Source(string Log, string Provider, int[] EventIds, string Kind);

    /// <summary>Every event source the collector subscribes to.</summary>
    public static readonly Source[] Sources =
    [
        new("Application", "Application Hang", [1002], HealthKind.AppHang),
        new("Application", "Application Error", [1000], HealthKind.AppCrash),
        // .NET apps that crash often log only this (no "Application Error" when Windows Error Reporting is off).
        new("Application", ".NET Runtime", [1026], HealthKind.AppCrash),
        new("System", "Microsoft-Windows-WER-SystemErrorReporting", [1001], HealthKind.BlueScreen),
        new("System", "Microsoft-Windows-Kernel-Power", [41], HealthKind.UnexpectedShutdown),
        new("System", "Display", [4101], HealthKind.DisplayDriverReset),
        new("System", "Microsoft-Windows-Kernel-Processor-Power", [37], HealthKind.ThermalThrottle),
        new("System", "disk", [7, 51, 153], HealthKind.DiskError),
        new("System", "Microsoft-Windows-Ntfs", [55], HealthKind.DiskError),
        new("System", "Microsoft-Windows-Resource-Exhaustion-Detector", [2004], HealthKind.LowMemory),
        new("System", "Microsoft-Windows-WHEA-Logger", [1, 17, 18, 19, 47], HealthKind.HardwareError),
        new("System", "Microsoft-Windows-DistributedCOM", [10016], HealthKind.Noise),
        new("Microsoft-Windows-WLAN-AutoConfig/Operational", "Microsoft-Windows-WLAN-AutoConfig", [8003], HealthKind.WifiDrop),
        new("Microsoft-Windows-WLAN-AutoConfig/Operational", "Microsoft-Windows-WLAN-AutoConfig", [8001], HealthKind.WifiConnect),
        new("Microsoft-Windows-Diagnostics-Performance/Operational", "Microsoft-Windows-Diagnostics-Performance", [100], HealthKind.SlowBoot),
    ];

    public static string Classify(string log, string provider, int eventId)
    {
        foreach (var s in Sources)
            if (s.Log.Equals(log, StringComparison.OrdinalIgnoreCase)
                && s.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)
                && s.EventIds.Contains(eventId))
                return s.Kind;
        return HealthKind.Other;
    }

    /// <summary>Event-log sources that record changes (installs, updates, drivers) rather than problems.</summary>
    public static readonly Source[] ChangeSources =
    [
        new("Application", "MsiInstaller", [11707], ChangeKind.AppInstalled),
        new("Application", "MsiInstaller", [11724], ChangeKind.AppRemoved),
        new("System", "Microsoft-Windows-WindowsUpdateClient", [19], ChangeKind.WindowsUpdate),
        new("System", "Microsoft-Windows-WindowsUpdateClient", [20], ChangeKind.UpdateFailed),
        new("System", "Microsoft-Windows-UserPnp", [20001], ChangeKind.DriverInstalled),
        new("Microsoft-Windows-Kernel-PnP/Configuration", "Microsoft-Windows-Kernel-PnP", [400], ChangeKind.DriverInstalled),
    ];

    public static IEnumerable<string> ChangeLogs => ChangeSources.Select(s => s.Log).Distinct(StringComparer.OrdinalIgnoreCase);

    public static string? ChangeKindFor(string log, string provider, int eventId) =>
        ChangeSources.FirstOrDefault(s => s.Log.Equals(log, StringComparison.OrdinalIgnoreCase)
            && s.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && s.EventIds.Contains(eventId))?.Kind;

    /// <summary>Builds the XPath filter for one log channel, optionally limited to a lookback window.</summary>
    public static string XPathFor(string log, long? lookbackMs = null) => XPathFor(Sources, log, lookbackMs);

    public static string XPathFor(IEnumerable<Source> sources, string log, long? lookbackMs = null)
    {
        var clauses = sources
            .Where(s => s.Log.Equals(log, StringComparison.OrdinalIgnoreCase))
            .Select(s => $"(Provider[@Name='{s.Provider}'] and ({string.Join(" or ", s.EventIds.Select(i => $"EventID={i}"))}))");
        var filter = string.Join(" or ", clauses);
        var time = lookbackMs is { } ms ? $" and TimeCreated[timediff(@SystemTime) <= {ms}]" : "";
        return $"*[System[({filter}){time}]]";
    }

    public static IEnumerable<string> Logs => Sources.Select(s => s.Log).Distinct(StringComparer.OrdinalIgnoreCase);
}
