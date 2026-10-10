using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ResMan.Core;
using ResMan.Data;

namespace ResMan.Service;

/// <summary>
/// The "what changed?" recorder. Installs, uninstalls, Windows updates and driver installs come from
/// the event logs (subscribed, plus a 30-day backfill). Programs and startup items are inventoried from
/// the registry and Startup folders every 30 minutes and diffed. The first scan only records a baseline.
/// </summary>
public sealed partial class ChangeCollector(ChangeStore store, ServiceState state, ILogger<ChangeCollector> log) : BackgroundService
{
    public const string InvPrograms = "programs";
    public const string InvStartup = "startup";
    private static readonly long BackfillMs = (long)TimeSpan.FromDays(30).TotalMilliseconds;
    private readonly List<EventLogWatcher> _watchers = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var channel in EventClassifier.ChangeLogs)
        {
            try
            {
                var xpath = EventClassifier.XPathFor(EventClassifier.ChangeSources, channel, BackfillMs);
                using (var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, xpath)))
                    for (var r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
                        using (r) Store(channel, r);
                var watcher = new EventLogWatcher(new EventLogQuery(channel, PathType.LogName, EventClassifier.XPathFor(EventClassifier.ChangeSources, channel)));
                watcher.EventRecordWritten += (_, e) => { if (e.EventRecord is { } r) using (r) Store(channel, r); };
                watcher.Enabled = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is EventLogNotFoundException or UnauthorizedAccessException or EventLogException)
            {
                state.Warnings["changes:" + channel] = $"Can't read change history from '{channel}': {ex.Message}";
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try { ScanInventory(DateTime.UtcNow); }
            catch (Exception ex) { log.LogWarning("Inventory scan failed: {Message}", ex.Message); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public void ScanInventory(DateTime nowUtc)
    {
        Diff(InvPrograms, InstalledPrograms(), ChangeKind.AppInstalled, ChangeKind.AppRemoved, nowUtc);
        Diff(InvStartup, StartupItems(), ChangeKind.StartupAdded, ChangeKind.StartupRemoved, nowUtc);
    }

    private void Diff(string kind, Dictionary<string, string> current, string added, string removed, DateTime nowUtc)
    {
        if (store.HasBaseline(kind))
        {
            var previous = store.Inventory(kind);
            foreach (var (key, value) in current.Where(c => !previous.ContainsKey(c.Key)))
                Record(new SystemChange(0, nowUtc, added, value, null, "inventory", $"{added}:{key}:{nowUtc:yyyyMMddHH}"));
            foreach (var (key, value) in previous.Where(p => !current.ContainsKey(p.Key)))
                Record(new SystemChange(0, nowUtc, removed, value, null, "inventory", $"{removed}:{key}:{nowUtc:yyyyMMddHH}"));
        }
        else log.LogInformation("Recorded a baseline of {Count} {Kind}", current.Count, kind);
        store.ReplaceInventory(kind, current, nowUtc);
    }

    private void Record(SystemChange c)
    {
        if (store.Insert(c)) log.LogInformation("Change: {Description}", ChangeKind.Describe(c));
    }

    private void Store(string channel, EventRecord r)
    {
        try
        {
            if (Normalize(channel, r) is { } c) Record(c);
        }
        catch (Exception ex) { log.LogDebug("Skipped change event: {Message}", ex.Message); }
    }

    [GeneratedRegex(@"Product:\s*(.+?)\s*--")]
    private static partial Regex MsiProduct();

    public static SystemChange? Normalize(string channel, EventRecord r)
    {
        var kind = EventClassifier.ChangeKindFor(channel, r.ProviderName ?? "", r.Id);
        if (kind is null) return null;
        var data = HealthEventCollector.ParseEventData(r.ToXml());
        var time = (r.TimeCreated ?? DateTime.Now).ToUniversalTime();
        string? name, detail = null, key;

        switch (r.ProviderName)
        {
            case "MsiInstaller":
                string message;
                try { message = r.FormatDescription() ?? ""; } catch { message = ""; }
                var m = MsiProduct().Match(message);
                name = m.Success ? m.Groups[1].Value : data.GetValueOrDefault("0");
                key = $"msi:{r.RecordId}";
                break;
            case "Microsoft-Windows-WindowsUpdateClient":
                name = data.GetValueOrDefault("updateTitle");
                key = $"wu:{data.GetValueOrDefault("updateGuid")}:{r.Id}:{data.GetValueOrDefault("updateRevisionNumber")}";
                break;
            default: // driver events: keep only real third-party driver installs, not every device plug-in
                var driver = data.GetValueOrDefault("DriverName");
                if (data.GetValueOrDefault("DeviceUpdated") is "false" || data.GetValueOrDefault("DriverInbox") is "true"
                    || string.IsNullOrEmpty(driver) || driver == "null") return null;
                var provider = data.GetValueOrDefault("DriverProvider");
                var version = data.GetValueOrDefault("DriverVersion");
                name = $"{(string.IsNullOrEmpty(provider) ? "" : provider + " ")}driver {driver}{(string.IsNullOrEmpty(version) ? "" : " " + version)}".Trim();
                detail = data.GetValueOrDefault("DeviceInstanceId") ?? data.GetValueOrDefault("DeviceInstanceID");
                key = $"drv:{driver}:{version}";
                break;
        }
        return string.IsNullOrWhiteSpace(name) ? null : new SystemChange(0, time, kind, name.Trim(), detail, channel, key);
    }

    /// <summary>Installed programs from the machine-wide uninstall keys (64- and 32-bit) and each loaded user's.</summary>
    public static Dictionary<string, string> InstalledPrograms()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey? root, string path)
        {
            using var k = root?.OpenSubKey(path);
            if (k is null) return;
            foreach (var sub in k.GetSubKeyNames())
            {
                using var app = k.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (app.GetValue("SystemComponent") is 1) continue;
                var version = app.GetValue("DisplayVersion") as string;
                result[$"{name}|{version}"] = string.IsNullOrEmpty(version) ? name : $"{name} {version}";
            }
        }
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        Read(Registry.LocalMachine, uninstall);
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        foreach (var user in UserHives()) using (user) Read(user, uninstall);
        return result;
    }

    /// <summary>Programs set to start with Windows: Run keys (machine and each loaded user) and Startup folders.</summary>
    public static Dictionary<string, string> StartupItems()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey? root, string path, string scope)
        {
            using var k = root?.OpenSubKey(path);
            if (k is null) return;
            foreach (var name in k.GetValueNames().Where(n => n.Length > 0))
                result[$"{scope}|{name}"] = name;
        }
        const string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        Read(Registry.LocalMachine, run, "machine");
        Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "machine32");
        foreach (var user in UserHives()) using (user) Read(user, run, "user:" + user.Name);

        var folders = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) };
        var users = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users");
        if (Directory.Exists(users))
            folders.AddRange(Directory.EnumerateDirectories(users)
                .Select(u => Path.Combine(u, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup")));
        foreach (var f in folders.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(f).Where(x => !x.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    result[$"folder|{file}"] = Path.GetFileNameWithoutExtension(file);
            }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>Loaded real-user hives under HKEY_USERS (the user's own HKCU when not running as the service).</summary>
    private static IEnumerable<RegistryKey> UserHives()
    {
        var isSystem = WindowsIdentity.GetCurrent().IsSystem;
        if (!isSystem) { yield return Registry.CurrentUser.OpenSubKey("")!; yield break; }
        foreach (var sid in Registry.Users.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-") && !s.EndsWith("_Classes")))
            if (Registry.Users.OpenSubKey(sid) is { } k) yield return k;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        foreach (var w in _watchers) { w.Enabled = false; w.Dispose(); }
        return base.StopAsync(ct);
    }
}
