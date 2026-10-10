using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using ResMan.Core;
using ResMan.Data;

namespace ResMan.Service;

/// <summary>
/// Subscribes to the Windows event logs for health-relevant events (no polling) and backfills the
/// last 7 days on start. Duplicates are ignored via the (log, record id) unique key.
/// </summary>
public sealed class HealthEventCollector(
    HealthStore store, TelemetryStore telemetry, ReplayBuffer buffer, ServiceState state, ILogger<HealthEventCollector> log)
    : BackgroundService
{
    private readonly List<EventLogWatcher> _watchers = [];
    private static readonly long BackfillMs = (long)TimeSpan.FromDays(7).TotalMilliseconds;

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        var removed = store.RemoveDuplicateCrashes();
        if (removed > 0) log.LogInformation("Merged {Count} crashes that Windows had logged twice", removed);
        foreach (var channel in EventClassifier.Logs)
        {
            try
            {
                Backfill(channel);
                var watcher = new EventLogWatcher(new EventLogQuery(channel, PathType.LogName, EventClassifier.XPathFor(channel)));
                watcher.EventRecordWritten += (_, e) =>
                {
                    if (e.EventRecord is { } r) using (r) Store(channel, r);
                    else if (e.EventException is { } ex) log.LogWarning("Event watcher error on {Log}: {Message}", channel, ex.Message);
                };
                watcher.Enabled = true;
                _watchers.Add(watcher);
                log.LogInformation("Watching {Log}", channel);
            }
            catch (Exception ex) when (ex is EventLogNotFoundException or UnauthorizedAccessException or EventLogException)
            {
                var msg = ex is UnauthorizedAccessException
                    ? $"Can't read '{channel}' without admin rights; run elevated or as the service."
                    : $"Log '{channel}' unavailable: {ex.Message}";
                state.Warnings[channel] = msg;
                log.LogWarning("{Message}", msg);
            }
        }
        return Task.CompletedTask;
    }

    private void Backfill(string channel)
    {
        var query = new EventLogQuery(channel, PathType.LogName, EventClassifier.XPathFor(channel, BackfillMs));
        using var reader = new EventLogReader(query);
        var n = 0;
        for (var r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
            using (r)
                if (Store(channel, r) is not null) n++;
        if (n > 0) log.LogInformation("Backfilled {Count} events from {Log}", n, channel);
    }

    private long? Store(string channel, EventRecord r)
    {
        try
        {
            var e = Normalize(channel, r);
            if (RuleValidator.IsOwnProcess(e.AppName)) return null; // ResMan doesn't report its own crashes as your PC's problems
            if (e.Kind == HealthKind.AppCrash && store.FindSameCrash(e.AppName, e.Provider, e.TimeUtc, TimeSpan.FromSeconds(3)) is { } twin)
            {
                // The same crash, logged a second time by another provider: keep one, with both providers' details.
                store.MergeData(twin.Id, e.Data);
                return null;
            }
            if (e.Kind is HealthKind.AppHang or HealthKind.AppCrash or HealthKind.LowMemory)
                e = e with { SnapshotId = telemetry.Nearest(e.TimeUtc, TimeSpan.FromSeconds(30))?.Id };
            var id = store.Insert(e);
            // Live incidents get the black-box minute before them saved for replay.
            if (id is { } eventId && e.Kind is HealthKind.AppCrash or HealthKind.AppHang or HealthKind.LowMemory or HealthKind.DisplayDriverReset or HealthKind.WifiDrop
                && buffer.Covers(e.TimeUtc))
                buffer.SaveAround(eventId, e.TimeUtc);
            return id;
        }
        catch (Exception ex)
        {
            log.LogWarning("Couldn't store event {Id} from {Log}: {Message}", r.Id, channel, ex.Message);
            return null;
        }
    }

    public static HealthEvent Normalize(string channel, EventRecord r)
    {
        var provider = r.ProviderName ?? "";
        var data = ParseEventData(r.ToXml());
        var kind = EventClassifier.Classify(channel, provider, r.Id);
        string? app = kind is HealthKind.AppHang or HealthKind.AppCrash
            ? data.GetValueOrDefault("AppName") ?? data.GetValueOrDefault("0")
            : null;
        if (provider == ".NET Runtime" && data.GetValueOrDefault("0") is { } text)
        {
            // One free-text blob: "Application: X.exe ... Exception Info: System.Foo: message ..."
            var lines = text.Split('\n', StringSplitOptions.TrimEntries);
            app = lines.FirstOrDefault(l => l.StartsWith("Application:"))?["Application:".Length..].Trim() ?? app;
            if (lines.FirstOrDefault(l => l.StartsWith("Exception Info:")) is { } ex) data["Exception"] = ex["Exception Info:".Length..].Trim();
            data["0"] = "";
        }

        string summary;
        try { summary = r.FormatDescription() ?? ""; }
        catch { summary = ""; }
        summary = summary.Split('\n', 2)[0].Trim();
        if (summary.Length == 0) summary = $"{provider} event {r.Id}";
        if (summary.Length > 300) summary = summary[..300];

        return new HealthEvent(0, (r.TimeCreated ?? DateTime.Now).ToUniversalTime(), channel, provider, r.Id,
            r.Level ?? 0, kind, app, summary, data, null, r.RecordId);
    }

    /// <summary>Reads EventData/UserData values, by name when present, otherwise by position.</summary>
    internal static Dictionary<string, string> ParseEventData(string xml)
    {
        var result = new Dictionary<string, string>();
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var i = 0;
        foreach (var d in doc.Descendants(ns + "Data"))
        {
            var key = (string?)d.Attribute("Name") ?? (i).ToString();
            var value = d.Value.Trim();
            if (value.Length > 800) value = value[..800];
            result.TryAdd(key, value);
            result.TryAdd(i.ToString(), value);
            i++;
        }
        return result;
    }

    public override Task StopAsync(CancellationToken ct)
    {
        foreach (var w in _watchers) { w.Enabled = false; w.Dispose(); }
        return base.StopAsync(ct);
    }
}
