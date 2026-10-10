namespace ResMan.Core;

/// <summary>Something that changed on the PC: an install, update, driver or startup item.</summary>
public sealed record SystemChange(
    long Id,
    DateTime TimeUtc,
    string Kind,
    string Name,
    string? Detail,
    string Source,
    string Key);

public static class ChangeKind
{
    public const string AppInstalled = "app_installed";
    public const string AppRemoved = "app_removed";
    public const string WindowsUpdate = "windows_update";
    public const string UpdateFailed = "update_failed";
    public const string DriverInstalled = "driver_installed";
    public const string StartupAdded = "startup_added";
    public const string StartupRemoved = "startup_removed";

    /// <summary>Routine background changes (e.g. daily antivirus definitions). Hidden from lists and never blamed.</summary>
    public static bool IsRoutine(SystemChange c) =>
        c.Name.Contains("Security Intelligence Update", StringComparison.OrdinalIgnoreCase)
        || c.Name.Contains("Definition Update", StringComparison.OrdinalIgnoreCase)
        || c.Name.Contains("Malicious Software Removal Tool", StringComparison.OrdinalIgnoreCase);

    public static string Describe(SystemChange c) => c.Kind switch
    {
        AppInstalled => $"{c.Name} was installed",
        AppRemoved => $"{c.Name} was uninstalled",
        WindowsUpdate => $"Windows installed an update: {c.Name}",
        UpdateFailed => $"A Windows update failed to install: {c.Name}",
        DriverInstalled => $"A driver was installed: {c.Name}",
        StartupAdded => $"{c.Name} now starts automatically with Windows",
        StartupRemoved => $"{c.Name} no longer starts automatically",
        _ => c.Name,
    };
}

/// <summary>One reading of a physical drive's own health counters (SMART, via Windows storage APIs).</summary>
public sealed record DriveHealth(
    DateTime TimeUtc,
    string DeviceId,
    string Model,
    string MediaType,
    string HealthStatus,
    int? WearPercent,
    long? ReadErrorsTotal,
    long? WriteErrorsTotal,
    int? TemperatureC,
    long? PowerOnHours);

/// <summary>A fix the user says they applied, so the before/after rate can be tracked.</summary>
public sealed record FixAttempt(
    long Id,
    string IssueKey,
    string ProblemKind,
    string? App,
    string Label,
    DateTime AppliedUtc);
