using System.Globalization;
using System.Text;
using ResMan.Brain.Doctor;
using ResMan.Core;

namespace ResMan.Brain.Harness;

/// <summary>
/// Read-only evidence tools the model calls. Each returns compact plain text with times and numbers,
/// capped in length so several results fit in a small model's context.
/// </summary>
public sealed class HarnessTools(DoctorService doctor)
{
    public const int MaxChars = 1400;
    private Brain.Knowledge.KnowledgeBase Kb => doctor.Knowledge;

    /// <summary>Friendly name, or the raw name flagged as unknown so the model knows to inspect_binary instead of guessing.</summary>
    private string Name(string? process) =>
        process is null ? "an app" : Kb.LookupProcess(process) is { } p ? p.Title : $"{RuleValidator.NormalizeProcessName(process)} (unknown program)";

    /// <summary>Kinds of the events the model opened with get_event_details during this investigation.</summary>
    public HashSet<string> InspectedKinds { get; } = [];

    public string Run(string tool, string input)
    {
        try
        {
            var output = tool switch
            {
                "get_health_events" => HealthEvents(input),
                "get_event_details" => EventDetails(input),
                "get_resource_history" => ResourceHistory(input),
                "get_changes" => Changes(input),
                "get_network_history" => NetworkHistory(input),
                "get_findings" => Findings(),
                "search_knowledge" => SearchKnowledge(input),
                "inspect_binary" or "lookup_process" => InspectBinary(input),
                "get_top_processes" => TopProcesses(),
                _ => $"Unknown tool '{tool}'.",
            };
            return output.Length <= MaxChars ? output : output[..MaxChars] + " ...(truncated)";
        }
        catch (Exception ex)
        {
            return $"The tool failed: {ex.Message}";
        }
    }

    /// <summary>What a tool call looks at, in plain words, for the live investigation view.</summary>
    public static string Label(string tool, string input) => tool switch
    {
        "get_health_events" => $"Event records, last {Num(input, 48, 1, 24 * 30):0} hours",
        "get_event_details" => $"Event #{input.Trim()} up close",
        "get_resource_history" => $"Memory and CPU history, last {Num(input, 30, 2, 24 * 60):0} min",
        "get_changes" => "Recent installs, updates and drivers",
        "get_network_history" => "Network history",
        "get_findings" => "Hindsight's built-in checks",
        "search_knowledge" => $"Knowledge base: \"{input.Trim()}\"",
        "inspect_binary" or "lookup_process" => $"The {RuleValidator.NormalizeProcessName(input.Trim())} program file",
        "get_top_processes" => "Programs running right now",
        _ => tool,
    };

    /// <summary>The most telling line of a tool's output, shortened for display.</summary>
    public static string Gist(string tool, string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return "";
        var pick = lines[0];
        if (tool == "get_resource_history")
        {
            var grew = lines.FirstOrDefault(l => l.StartsWith("Grew or appeared", StringComparison.Ordinal));
            var spikes = lines.FirstOrDefault(l => l.StartsWith("CPU spikes", StringComparison.Ordinal));
            pick = grew is not null && !grew.Contains("nothing notable") ? grew
                : spikes is not null && !spikes.EndsWith("none.", StringComparison.Ordinal) ? spikes
                : grew ?? pick;
        }
        else if (tool == "get_event_details" && lines.FirstOrDefault(l => l.StartsWith("The app's own error", StringComparison.Ordinal)) is { } err)
            pick = $"{lines[0]} {err}";
        pick = pick.Replace(" (unknown program)", "", StringComparison.Ordinal);
        return pick.Length <= 200 ? pick : pick[..197].TrimEnd() + "...";
    }

    private static double Num(string input, double fallback, double min, double max) =>
        double.TryParse(new string(input.Where(c => char.IsDigit(c) || c == '.').ToArray()), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, min, max) : fallback;

    internal static string When(DateTime utc) => utc.ToLocalTime().ToString("ddd h:mm:ss tt", CultureInfo.InvariantCulture);

    private string HealthEvents(string input)
    {
        var hours = Num(input, 48, 1, 24 * 30);
        var a = doctor.Analyze();
        var events = a.Events.Where(e => e.Kind is not (HealthKind.Noise or HealthKind.WifiConnect) && e.TimeUtc >= a.NowUtc.AddHours(-hours)).ToList();
        var noise = a.Events.Count(e => e.Kind == HealthKind.Noise && e.TimeUtc >= a.NowUtc.AddHours(-hours));
        if (events.Count == 0) return $"No problems were recorded in the last {hours:0} hours ({noise} harmless background warnings ignored).";
        var lines = Reports.Timeline(events, a.Matches, Kb).Take(14).Select(t =>
            $"- [event {t.EventIds[0]}] {When(t.TimeUtc)}: {t.Title}{(t.Count > 1 ? $" (x{t.Count} since {When(t.FirstUtc)})" : "")}{(t.Detail is null ? "" : $". {t.Detail}")}");
        return $"{events.Count} problem event(s) in the last {hours:0} h ({noise} harmless warnings ignored):\n" + string.Join("\n", lines);
    }

    private string EventDetails(string input)
    {
        var id = (long)Num(input, -1, -1, long.MaxValue);
        var e = doctor.Health.Get(id);
        if (e is null) return $"There is no event {input}. Use get_health_events to find event ids.";
        InspectedKinds.Add(e.Kind);
        var (title, detail) = Reports.Describe(e, Kb);
        var sb = new StringBuilder();
        sb.AppendLine($"Event {e.Id} at {When(e.TimeUtc)}: {title}. {detail}");
        sb.AppendLine($"Windows' own text: \"{Trim(e.Summary, 160)}\"");
        var data = e.Data.Where(kv => !int.TryParse(kv.Key, out _) && kv.Key != "Exception" && kv.Value.Length is > 0 and < 80).Take(8);
        if (data.Any()) sb.AppendLine("Details: " + string.Join(", ", data.Select(kv => $"{kv.Key}={kv.Value}")));
        if (e.Data.GetValueOrDefault("Exception") is { Length: > 0 } exception) sb.AppendLine($"The app's own error: {Trim(exception, 200)}");
        if (e.SnapshotId is { } sid && doctor.Telemetry.Get(sid) is { } snap)
            sb.AppendLine($"At that moment: {DescribeSnapshot(snap)}");
        var replay = doctor.Replays.Get(e.Id, e.TimeUtc);
        if (replay.Count > 1)
            sb.AppendLine($"The minute before ({replay.Count} samples): {SummarizeReplay(replay, e.TimeUtc)}");
        // Only changes that could plausibly relate: same app, or drivers/Windows updates for system-level problems.
        var before = doctor.Analyze().Changes.Where(c => !ChangeKind.IsRoutine(c) && c.TimeUtc <= e.TimeUtc && c.TimeUtc >= e.TimeUtc.AddDays(-3)).ToList();
        var app = e.AppName is null ? null : RuleValidator.NormalizeProcessName(e.AppName);
        var relevant = before.Where(c => (app is not null && c.Name.Contains(app, StringComparison.OrdinalIgnoreCase))
            || (app is null && c.Kind is ChangeKind.DriverInstalled or ChangeKind.WindowsUpdate)).Take(3).ToList();
        sb.AppendLine(relevant.Count > 0
            ? "Possibly related changes in the 3 days before: " + string.Join("; ", relevant.Select(c => $"{ChangeKind.Describe(c)} ({When(c.TimeUtc)})"))
            : $"No changes in the 3 days before mention {(app is null ? "a driver or Windows update" : $"'{app}'")}" +
              (before.Count > 0 ? $" ({Reports.GroupChanges(before).Count} unrelated install/update group(s) happened; see get_changes)." : "."));
        return sb.ToString().TrimEnd();
    }

    private string ResourceHistory(string input)
    {
        var minutes = Num(input, 30, 2, 24 * 60);
        var now = DateTime.UtcNow;
        var snaps = doctor.Telemetry.Range(now.AddMinutes(-minutes), now);
        if (snaps.Count == 0) return "No resource data for that period.";

        // The conclusions come first: the output is capped, and a long window must lose old detail, not the summary.
        // Who changed matters more than who is big: a steady large app isn't what pushed memory up.
        var byName = snaps.SelectMany(s => s.Top.Select(p => (s.TimeUtc, p)))
            .Where(x => !RuleValidator.IsOwnProcess(x.p.Name))
            .GroupBy(x => x.p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // Apps like browsers run many same-named processes: total them per sample, or a small
                // helper process followed by the main one would look like growth.
                var perSample = g.GroupBy(x => x.TimeUtc).Select(t => (time: t.Key, mb: t.Sum(x => x.p.WorkingSetBytes) >> 20)).OrderBy(t => t.time).ToList();
                return (name: g.Key, first: perSample[0].mb, peak: perSample.Max(t => t.mb), seen: perSample.Count, since: perSample[0].time);
            })
            .ToList();
        var grew = byName.Where(x => x.peak - x.first >= 300 || (x.seen < snaps.Count / 2 && x.peak >= 500))
            .OrderByDescending(x => x.peak - x.first).Take(3)
            .Select(x => $"{Name(x.name)} {x.first}->{x.peak} MB{(x.seen < snaps.Count / 2 ? $" (appeared {When(x.since)})" : "")}");
        var steady = byName.Where(x => x.seen >= snaps.Count * 0.8 && x.peak - x.first < 300 && x.peak >= 500)
            .OrderByDescending(x => x.peak).Take(3).Select(x => $"{Name(x.name)} ~{x.peak} MB");
        // Short spikes vanish in averages; list them explicitly with their rhythm.
        var spikes = snaps.SelectMany(s => s.Top.Where(p => p.CpuPercent >= 50 && !RuleValidator.IsOwnProcess(p.Name)).Select(p => (s.TimeUtc, p)))
            .GroupBy(x => x.p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count()).Take(2)
            .Select(g =>
            {
                var times = g.Select(x => x.TimeUtc).Order().ToList();
                var gaps = times.Zip(times.Skip(1), (a, b) => (b - a).TotalMinutes).ToList();
                return $"{Name(g.Key)} jumped to {g.Max(x => x.p.CpuPercent):0}% CPU {g.Count()} times" +
                       (gaps.Count > 0 ? $", about every {gaps.Order().ElementAt(gaps.Count / 2):0.#} min (last {When(times[^1])})" : "");
            });

        var sb = new StringBuilder($"Resource history, last {minutes:0} min ({snaps.Count} samples).\n");
        sb.AppendLine($"Grew or appeared the most: {(grew.Any() ? string.Join(", ", grew) : "nothing notable")}.");
        sb.AppendLine($"CPU spikes (50%+ by one program): {(spikes.Any() ? string.Join("; ", spikes) : "none")}.");
        sb.AppendLine($"Large but steady the whole time (less likely to explain a sudden problem): {(steady.Any() ? string.Join(", ", steady) : "none")}.");
        sb.AppendLine($"Peak memory {snaps.Max(s => s.MemoryUsedPercent):0}% ({When(snaps.MaxBy(s => s.MemoryUsedPercent)!.TimeUtc)}), peak CPU {snaps.Max(s => s.TotalCpuPercent):0}%.");
        sb.AppendLine("Over time, oldest first:");
        var buckets = Math.Min(8, snaps.Count);
        var size = (int)Math.Ceiling(snaps.Count / (double)buckets);
        foreach (var chunk in snaps.Chunk(size))
        {
            var busiest = chunk.SelectMany(s => s.Top).Where(p => !RuleValidator.IsOwnProcess(p.Name)).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, cpu: g.Average(p => p.CpuPercent), mb: g.Max(p => p.WorkingSetBytes) >> 20, disk: g.Max(p => p.DiskBytesPerSec) >> 20))
                .OrderByDescending(x => x.cpu + x.mb / 400.0 + x.disk / 5.0).Take(2)
                .Select(x => $"{Name(x.Key)} {x.cpu:0}% CPU/{x.mb} MB{(x.disk > 0 ? $"/{x.disk} MB/s disk" : "")}");
            sb.AppendLine($"- {When(chunk[0].TimeUtc)}: memory {chunk.Average(s => s.MemoryUsedPercent):0}% (peak {chunk.Max(s => s.MemoryUsedPercent):0}%), " +
                          $"CPU {chunk.Average(s => s.TotalCpuPercent):0}%. Busiest: {string.Join(", ", busiest)}");
        }
        return sb.ToString().TrimEnd();
    }
    private string Changes(string input)
    {
        var days = Num(input, 7, 1, 60);
        var a = doctor.Analyze();
        var groups = Reports.GroupChanges(a.Changes.Where(c => c.TimeUtc >= a.NowUtc.AddDays(-days)));
        return groups.Count == 0 ? $"Nothing was installed, updated or changed in the last {days:0} days."
            : $"Changes in the last {days:0} days (newest first):\n" + string.Join("\n", groups.Take(12).Select(g => $"- {When(g.TimeUtc)}: {g.Description}"));
    }

    private string NetworkHistory(string input)
    {
        var hours = Num(input, 6, 0.25, 24 * 7);
        var now = DateTime.UtcNow;
        var samples = doctor.Replays.Network(now.AddHours(-hours), now);
        var a = doctor.Analyze();
        var netEvents = a.Events.Where(e => e.Kind is HealthKind.NetworkChange or HealthKind.WifiDrop or HealthKind.WifiConnect && e.TimeUtc >= now.AddHours(-hours))
            .OrderBy(e => e.TimeUtc).ToList();
        var sb = new StringBuilder();
        if (samples.Count > 0)
        {
            var last = samples[^1];
            sb.AppendLine($"Right now: {Describe(last)}.");
            sb.AppendLine("Connection over time (oldest first):");
            NetworkSample? prev = null;
            var lines = 0;
            foreach (var s in samples)
            {
                var changed = prev is null || prev.Connectivity != s.Connectivity || prev.Ssid != s.Ssid || prev.GatewayReachable != s.GatewayReachable
                              || Math.Abs((prev.SignalBars ?? 0) - (s.SignalBars ?? 0)) >= 2;
                if (changed && lines++ < 14) sb.AppendLine($"- {When(s.TimeUtc)}: {Describe(s)}");
                prev = s;
            }
            var pings = samples.Where(s => s.GatewayPingMs is not null).Select(s => s.GatewayPingMs!.Value).ToList();
            if (pings.Count > 0) sb.AppendLine($"Router ping: median {pings.Order().ElementAt(pings.Count / 2)} ms, worst {pings.Max()} ms; router didn't answer in {samples.Count(s => !s.GatewayReachable)} of {samples.Count} checks.");
        }
        else sb.AppendLine("No network samples recorded for that period.");
        if (netEvents.Count > 0)
            sb.AppendLine("Network events:\n" + string.Join("\n", netEvents.TakeLast(10).Select(e =>
                $"- [event {e.Id}] {When(e.TimeUtc)}: {e.Summary}{(e.Data.GetValueOrDefault("Reason") is { Length: > 0 } r ? $" (reason: {r})" : "")}")));
        return sb.ToString().TrimEnd();
    }

    private static string Describe(NetworkSample s) =>
        $"{s.Connectivity switch { Connectivity.Internet => "internet OK", Connectivity.None => "disconnected", Connectivity.LocalOnly => "connected to the local network but NO internet", _ => "limited internet" }}" +
        $"{(s.IsWifi ? $", Wi-Fi{(s.Ssid is null ? "" : $" '{s.Ssid}'")}{(s.SignalBars is { } b ? $" signal {b}/5" : "")}" : ", wired")}" +
        $"{(s.Gateway is null ? ", no router" : s.GatewayReachable ? $", router answers in {s.GatewayPingMs} ms" : ", router NOT answering")}";

    private string Findings()
    {
        var a = doctor.Analyze();
        var parts = a.Findings.Take(5).Select(x => $"- Pattern: {x.Title}. {x.Evidence}")
            .Concat(a.Matches.Where(m => !m.Signature.Noise).Take(5).Select(m =>
                $"- Known pattern '{m.Signature.Title.Replace("{app}", Kb.FriendlyName(m.App))}' ({m.Events.Count} event(s), last {When(m.LastSeenUtc)})"))
            .ToList();
        return parts.Count == 0 ? "No automatic pattern matched. Reason from the raw evidence." : "Automatic pattern checks (hints):\n" + string.Join("\n", parts);
    }

    private string SearchKnowledge(string query)
    {
        var hits = Kb.Search(query, 3);
        return hits.Count == 0 ? "Nothing relevant in the knowledge base." : string.Join("\n", hits.Select(h => $"- {h.Title}: {Trim(h.Text, 380)}"));
    }

    private string InspectBinary(string name)
    {
        var info = Kb.LookupProcess(name);
        var bin = BinaryInspector.Inspect(name);
        var parts = new List<string>();
        if (info is not null) parts.Add($"Known program: {info.Title} by {info.Publisher}. {info.What} {info.Notes} Safe to limit: {(info.SafeToLimit ? "yes" : "no")}.");
        else parts.Add($"'{name}' is not in ResMan's knowledge base.");
        if (bin is not null)
        {
            parts.Add($"Windows says: {BinaryInspector.Describe(bin)}");
            parts.Add($"File: {bin.Path}{(bin.Version is null ? "" : $", version {bin.Version}")}{(bin.Product is null ? "" : $", product '{bin.Product}'")}.");
        }
        else parts.Add("It isn't running right now, so Windows can't be asked about its file.");
        var recent = doctor.Analyze().Events.Where(e => e.AppName is not null &&
            RuleValidator.NormalizeProcessName(e.AppName).Equals(RuleValidator.NormalizeProcessName(name), StringComparison.OrdinalIgnoreCase)).ToList();
        if (recent.Count > 0) parts.Add($"Problems recorded for it: {string.Join(", ", recent.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {Doctor.FixOutcomes.Noun(g.Key)}"))}.");
        var installed = doctor.Analyze().Changes.FirstOrDefault(c => c.Name.Contains(RuleValidator.NormalizeProcessName(name), StringComparison.OrdinalIgnoreCase));
        if (installed is not null) parts.Add($"Related change: {ChangeKind.Describe(installed)} ({When(installed.TimeUtc)}).");
        return string.Join("\n", parts);
    }

    private string TopProcesses()
    {
        var s = doctor.Analyze().Latest;
        if (s is null) return "No resource data yet.";
        return $"At {When(s.TimeUtc)}: memory {s.MemoryUsedPercent:0}% full, processor {s.TotalCpuPercent:0}% busy{(s.OnBattery ? ", on battery" : "")}.\n" + string.Join("\n",
            s.Top.Where(p => !RuleValidator.IsOwnProcess(p.Name)).OrderByDescending(p => p.CpuPercent + p.WorkingSetBytes / 1e8).Take(8).Select(p =>
                $"- {Name(p.Name)}: {p.CpuPercent:0}% CPU, {p.WorkingSetBytes >> 20} MB" +
                (p.DiskBytesPerSec >= 1 << 20 ? $", disk {p.DiskBytesPerSec >> 20} MB/s" : "") +
                (p.GpuPercent >= 5 ? $", graphics {p.GpuPercent:0}%" : "")));
    }

    internal string DescribeSnapshot(Snapshot s)
    {
        var others = s.Top.Where(p => !RuleValidator.IsOwnProcess(p.Name)).ToList();
        var top = others.OrderByDescending(p => p.CpuPercent).Take(3).Where(p => p.CpuPercent >= 5).Select(p => $"{Name(p.Name)} {p.CpuPercent:0}% CPU");
        var mem = others.OrderByDescending(p => p.WorkingSetBytes).Take(3).Select(p => $"{Name(p.Name)} {p.WorkingSetBytes >> 20} MB");
        var disk = others.Where(p => p.DiskBytesPerSec >= 10L << 20).OrderByDescending(p => p.DiskBytesPerSec).Take(2).Select(p => $"{Name(p.Name)} {p.DiskBytesPerSec >> 20} MB/s");
        return $"memory {s.MemoryUsedPercent:0}% full, processor {s.TotalCpuPercent:0}% busy{(s.OnBattery ? ", on battery" : "")}. " +
               $"Biggest memory users: {string.Join(", ", mem)}.{(top.Any() ? $" Busiest: {string.Join(", ", top)}." : "")}{(disk.Any() ? $" Heavy disk: {string.Join(", ", disk)}." : "")}";
    }

    /// <summary>Turns a black-box series into a few lines: start state, peaks, and who was busiest.</summary>
    internal string SummarizeReplay(IReadOnlyList<ReplaySample> r, DateTime incidentUtc)
    {
        string T(ReplaySample s) => $"{(s.TimeUtc - incidentUtc).TotalSeconds:+0;-0;0}s";
        var first = r[0];
        var memPeak = r.MaxBy(s => s.MemoryPercent)!;
        var cpuPeak = r.MaxBy(s => s.CpuPercent)!;
        var hogs = r.SelectMany(s => s.Top).Where(p => !RuleValidator.IsOwnProcess(p.Name)).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, cpu: g.Average(p => p.CpuPercent), mb: g.Max(p => p.WorkingSetMb), firstMb: g.First().WorkingSetMb))
            .OrderByDescending(x => x.cpu + x.mb / 300.0).Take(3)
            .Select(x => $"{Name(x.Key)} (avg {x.cpu:0}% CPU, memory {x.firstMb}->{x.mb} MB)");
        return $"at {T(first)} memory {first.MemoryPercent:0}% and CPU {first.CpuPercent:0}%; memory peaked at {memPeak.MemoryPercent:0}% ({T(memPeak)}), " +
               $"CPU peaked at {cpuPeak.CpuPercent:0}% ({T(cpuPeak)}). Busiest programs: {string.Join(", ", hogs)}.";
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
