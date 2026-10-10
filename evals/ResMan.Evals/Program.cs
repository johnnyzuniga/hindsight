using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResMan.Brain.Doctor;
using ResMan.Brain.Harness;
using ResMan.Brain.Knowledge;
using ResMan.Brain.Models;
using ResMan.Core;
using ResMan.Data;

// ResMan A/B/C evaluation on staged cases (evals/cases.json).
//   C (run here):  the local 4B model investigating through ResMan's harness and evidence tools.
//   B (exported):  a big model given the same captured evidence (all tool outputs, pre-gathered) -> evals/out/B/<case>.txt
//   A (exported):  a big model given only Windows Event Viewer-style logs -> evals/out/A/<case>.txt
// No cloud model is called from here. Paste A/B prompts into any model, save its JSON reply to
// evals/in/A|B/<case>.json, then run with --score to compare all three with the same scorer.
//
//   dotnet run --project evals/ResMan.Evals -- [--only <id>] [--export] [--score] [--no-model]

var argv = args.ToList();
string? Arg(string k) { var i = argv.IndexOf(k); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
var only = Arg("--only");
var exportOnly = argv.Contains("--export");
var scoreOnly = argv.Contains("--score");
var noModel = argv.Contains("--no-model");

var repo = Directory.GetParent(ResManPaths.FindUp("evals")!)!.FullName;
var evalDir = Path.Combine(repo, "evals");
var kb = KnowledgeBase.Load(Path.Combine(repo, "kb"));
var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(evalDir, "cases.json")))!.AsArray()
    .Select(c => c!).Where(c => only is null || c["id"]!.ToString() == only).ToList();

IModelProvider model = noModel || exportOnly || scoreOnly ? new NullModelProvider() : new SidecarModelProvider(new SidecarOptions
{
    ModelPath = Arg("--model") ?? Directory.EnumerateFiles(Path.Combine(repo, "models"), "*.gguf").FirstOrDefault(f => !f.Contains("embed", StringComparison.OrdinalIgnoreCase)),
    InferencePath = new[] { "Release", "Debug" }
        .Select(c => Path.Combine(repo, "src", "ResMan.Inference", "bin", c, "net10.0-windows10.0.19041.0", "ResMan.Inference.exe")).FirstOrDefault(File.Exists),
    IdleSeconds = 120,
});
Console.WriteLine($"ResMan A/B/C eval: {cases.Count} cases. Local model: {model.Describe}\n");

var rows = new List<Row>();
foreach (var c in cases)
{
    var id = c["id"]!.ToString();
    var complaint = c["complaint"]!.ToString();
    var dbPath = Path.Combine(Path.GetTempPath(), $"resman-eval-{Guid.NewGuid():N}.db");
    try
    {
        var db = new ResManDb(dbPath);
        var windowsLog = Fixture.Seed(db, c, DateTime.UtcNow);
        var doctor = new DoctorService(db, kb);
        var harness = new DoctorHarness(doctor, model);

        // Exports for A and B (always refreshed, so they match the current evidence format).
        Write(Path.Combine(evalDir, "out", "A", id + ".txt"), Exports.LogsOnly(complaint, windowsLog));
        Write(Path.Combine(evalDir, "out", "B", id + ".txt"), Exports.WithEvidence(complaint, doctor, harness));

        Score? cScore = null;
        double cMs = 0;
        string? cAnswer = null;
        if (!exportOnly && !scoreOnly && model.Enabled)
        {
            var sw = Stopwatch.StartNew();
            var a = await harness.WhyAsync(complaint);
            cMs = sw.Elapsed.TotalMilliseconds;
            cAnswer = JsonSerializer.Serialize(new { a.Summary, likely_cause = a.LikelyCause, a.Confidence, a.Evidence, a.Steps, needs_human = a.NeedsHuman, tools = a.ToolTrace }, new JsonSerializerOptions { WriteIndented = true });
            Write(Path.Combine(evalDir, "out", "C", id + ".json"), cAnswer);
            cScore = Scorer.Score(c["expect"]!, a.Summary, a.LikelyCause, a.Evidence, a.Steps, a.NeedsHuman, a.Confidence)
                     with { Note = a.UsedModel ? $"tools: {string.Join(" -> ", a.ToolTrace)}" : $"no model: {a.Note}" };
        }
        else if (File.Exists(Path.Combine(evalDir, "out", "C", id + ".json")))
            cScore = Scorer.FromFile(c["expect"]!, Path.Combine(evalDir, "out", "C", id + ".json"));

        var aScore = Scorer.FromFile(c["expect"]!, Path.Combine(evalDir, "in", "A", id + ".json"));
        var bScore = Scorer.FromFile(c["expect"]!, Path.Combine(evalDir, "in", "B", id + ".json"));
        rows.Add(new Row(id, aScore, bScore, cScore, cMs));
        Console.WriteLine($"  {id,-30} A {Mark(aScore)}  B {Mark(bScore)}  C {Mark(cScore)}{(cMs > 0 ? $" {cMs / 1000,5:0.0}s" : "")}  {cScore?.Note}{(cScore?.Missing is { Length: > 0 } m ? $"  missing: {m}" : "")}");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) try { File.Delete(f); } catch { }
    }
}

string Summary(Func<Row, Score?> pick, string label)
{
    var judged = rows.Select(pick).Where(s => s is not null).ToList();
    return judged.Count == 0 ? $"{label}: not run (no answers in evals/in/{label[0]})" : $"{label}: {judged.Count(s => s!.Pass)}/{judged.Count} passed";
}
var lat = rows.Where(r => r.CMs > 0).Select(r => r.CMs).Order().ToList();
var report = new StringBuilder()
    .AppendLine($"# ResMan A/B/C eval ({DateTime.Now:yyyy-MM-dd HH:mm})").AppendLine()
    .AppendLine($"- {Summary(r => r.A, "A (big model, logs only)")}")
    .AppendLine($"- {Summary(r => r.B, "B (big model, ResMan evidence)")}")
    .AppendLine($"- {Summary(r => r.C, "C (local 4B, ResMan harness)")}")
    .AppendLine(lat.Count > 0 ? $"- C latency: median {lat[lat.Count / 2] / 1000:0.0}s, max {lat[^1] / 1000:0.0}s (local GPU, nothing sent online)" : "")
    .AppendLine().AppendLine("| Case | A | B | C | C notes |").AppendLine("|---|---|---|---|---|");
foreach (var r in rows) report.AppendLine($"| {r.Id} | {Mark(r.A)} | {Mark(r.B)} | {Mark(r.C)} | {r.C?.Note} {(r.C?.Missing is { Length: > 0 } m ? $"(missing: {m})" : "")} |");
Write(Path.Combine(evalDir, "results.md"), report.ToString());
Console.WriteLine("\n" + report);
return 0;

static string Mark(Score? s) => s is null ? " - " : s.Pass ? "ok " : "BAD";
static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

record Row(string Id, Score? A, Score? B, Score? C, double CMs);

record Score(bool Pass, string Missing, string? Note = null);

static class Scorer
{
    public static Score Score(JsonNode expect, string summary, string cause, IEnumerable<string> evidence, IEnumerable<string> steps, bool needsHuman, string confidence)
    {
        var text = $"{summary} {cause} {string.Join(" ", evidence)} {string.Join(" ", steps)}".ToLowerInvariant();
        var missing = new List<string>();
        foreach (var group in expect["all"]?.AsArray() ?? [])
        {
            var words = group!.AsArray().Select(w => w!.ToString().ToLowerInvariant()).ToList();
            if (!words.Any(text.Contains)) missing.Add(string.Join("/", words));
        }
        foreach (var f in expect["forbid"]?.AsArray() ?? [])
            if (text.Contains(f!.ToString().ToLowerInvariant())) missing.Add($"said '{f}'");
        if (expect["needsHuman"] is { } nh && nh.GetValue<bool>() != needsHuman) missing.Add("needs_human");
        if (expect["confidenceLow"] is not null && confidence != "low") missing.Add($"confidence {confidence}, expected low");
        return new Score(missing.Count == 0, string.Join("; ", missing));
    }

    /// <summary>Scores an answer saved as JSON (summary, likely_cause, confidence, evidence[], steps[], needs_human).</summary>
    public static Score? FromFile(JsonNode expect, string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var raw = File.ReadAllText(path);
            var start = raw.IndexOf('{');
            var j = JsonNode.Parse(raw[start..(raw.LastIndexOf('}') + 1)])!;
            string S(params string[] keys) => keys.Select(k => j[k]?.ToString()).FirstOrDefault(v => v is not null) ?? "";
            IEnumerable<string> L(params string[] keys) => keys.Select(k => j[k]).FirstOrDefault(v => v is JsonArray)?.AsArray().Select(x => x!.ToString()) ?? [];
            return Score(expect, S("summary", "Summary"), S("likely_cause", "likelyCause", "LikelyCause"), L("evidence", "Evidence"), L("steps", "Steps"),
                j["needs_human"]?.GetValue<bool>() ?? j["NeedsHuman"]?.GetValue<bool>() ?? false, S("confidence", "Confidence").ToLowerInvariant());
        }
        catch (Exception ex) { return new Score(false, $"unreadable answer: {ex.Message}"); }
    }
}

static class Exports
{
    private const string AnswerSpec = """
        Reply with JSON only:
        {"summary": "...", "likely_cause": "...", "confidence": "low|medium|high", "evidence": ["..."], "steps": ["..."], "needs_human": true|false}
        """;

    /// <summary>Setup A: what a user could paste from Event Viewer into a chatbot.</summary>
    public static string LogsOnly(string complaint, IReadOnlyList<string> windowsLog) => $"""
        You are an expert Windows support technician helping a non-technical person.
        Their complaint: "{complaint}"
        Below are the Windows Event Viewer entries from their PC (System and Application logs, last 7 days). There is nothing else.

        {(windowsLog.Count == 0 ? "(no entries)" : string.Join("\n", windowsLog))}

        Diagnose the most likely cause in plain language.
        {AnswerSpec}
        """;

    /// <summary>Setup B: the same evidence ResMan's tools provide, gathered up front.</summary>
    public static string WithEvidence(string complaint, DoctorService doctor, DoctorHarness harness)
    {
        var tools = new HarnessTools(doctor);
        var a = doctor.Analyze(fresh: true);
        var sb = new StringBuilder();
        sb.AppendLine(Prompts.Doctor).AppendLine();
        sb.AppendLine($"COMPLAINT: \"{complaint}\"").AppendLine();
        sb.AppendLine(harness.Overview(a)).AppendLine();
        sb.AppendLine("EVIDENCE (output of ResMan's tools, gathered for you):");
        void Add(string tool, string input) => sb.AppendLine($"[{tool}(\"{input}\")]").AppendLine(tools.Run(tool, input)).AppendLine();
        Add("get_health_events", "168");
        foreach (var e in a.Events.Where(e => e.Kind is not (HealthKind.Noise or HealthKind.WifiConnect)).Take(5)) Add("get_event_details", e.Id.ToString());
        Add("get_resource_history", "60");
        Add("get_changes", "14");
        Add("get_network_history", "2");
        Add("get_findings", "");
        sb.AppendLine(AnswerSpec);
        return sb.ToString();
    }
}

/// <summary>Builds a staged PC history from a case: resource series, events (with snapshots/replays), changes, network.</summary>
static class Fixture
{
    public static List<string> Seed(ResManDb db, JsonNode c, DateTime now)
    {
        var telemetry = new TelemetryStore(db);
        var health = new HealthStore(db);
        var changes = new ChangeStore(db);
        var replays = new ReplayStore(db);
        var windowsLog = new List<string>();
        var series = BuildSeries(c["series"], now);
        var snapIds = series.Select(s => (s.TimeUtc, id: telemetry.Insert(s), s)).ToList();

        foreach (var ch in c["changes"]?.AsArray() ?? [])
        {
            var t = now.AddMinutes(-ch!["minutesAgo"]!.GetValue<double>());
            var sc = new SystemChange(0, t, ch["kind"]!.ToString(), ch["name"]!.ToString(), null, "eval", Guid.NewGuid().ToString());
            changes.Insert(sc);
            if (sc.Kind is ChangeKind.AppInstalled) windowsLog.Add($"{Local(t)}  Application  MsiInstaller  11707  Information  Product: {sc.Name} -- Installation completed successfully.");
            if (sc.Kind is ChangeKind.DriverInstalled) windowsLog.Add($"{Local(t)}  System  Microsoft-Windows-UserPnp  20001  Information  Driver Management concluded the process to install driver {sc.Name} with status 0.");
        }

        foreach (var n in c["network"]?.AsArray() ?? [])
            replays.InsertNetwork(new NetworkSample(now.AddMinutes(-n!["minutesAgo"]!.GetValue<double>()), n["connectivity"]!.ToString(),
                n["ssid"]?.ToString(), n["bars"]?.GetValue<int>(), true, "Intel(R) Wi-Fi 6 AX201", "192.168.1.1",
                n["pingMs"]?.GetValue<int>(), n["router"]!.GetValue<bool>()));

        foreach (var e in c["events"]?.AsArray() ?? [])
        {
            var count = e!["count"]?.GetValue<int>() ?? 1;
            for (var k = 0; k < count; k++)
            {
                var t = now.AddMinutes(-e["minutesAgo"]!.GetValue<double>() + k);
                var kind = e["kind"]!.ToString();
                var app = e["app"]?.ToString();
                var data = e["data"]?.Deserialize<Dictionary<string, string>>() ?? [];
                long? snapId = null;
                if (e["snapshot"] is { } s)
                {
                    var top = s["top"]!.AsArray().Select((p, i) => new ProcessSample(2000 + i, p!["name"]!.ToString(), p["cpu"]?.GetValue<double>() ?? 0,
                        (long)(p["mb"]?.GetValue<double>() ?? 100) << 20, (long)((p["diskMbps"]?.GetValue<double>() ?? 0) * (1 << 20)), 0)).ToList();
                    snapId = telemetry.Insert(new Snapshot(0, t, s["cpu"]!.GetValue<double>(), s["mem"]!.GetValue<double>(), 1L << 30, false, top));
                }
                else if (e["snapshotAt"] is not null && snapIds.Count > 0)
                    snapId = snapIds.MinBy(x => Math.Abs((x.TimeUtc - t).TotalSeconds)).id;

                var summary = e["summary"]?.ToString() ?? $"{kind} (staged)";
                var id = health.Insert(new HealthEvent(0, t, "Eval", e["provider"]?.ToString() ?? "Eval", e["eventId"]?.GetValue<int>() ?? 1, 2, kind, app, summary, data, snapId))!.Value;
                if (e["replay"] is not null && series.Count > 0) replays.Save(id, Replay(series, t));
                if (WindowsText(kind, app, data, snapId is { } sid ? telemetry.Get(sid) : null) is { } line) windowsLog.Add($"{Local(t)}  {line}");
            }
        }
        windowsLog.Sort(StringComparer.Ordinal);
        windowsLog.Reverse();
        return windowsLog;
    }

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>A resource history sampled every 30 s: steady apps, an optional growing hog, optional periodic CPU spikes.</summary>
    private static List<Snapshot> BuildSeries(JsonNode? s, DateTime now)
    {
        var list = new List<Snapshot>();
        if (s is null) return list;
        var minutes = s["minutes"]!.GetValue<double>();
        double baseMem = s["baseMem"]!.GetValue<double>(), baseCpu = s["baseCpu"]!.GetValue<double>();
        var steady = s["steady"]?.AsArray().Select(p => (name: p!["name"]!.ToString(), mb: p["mb"]!.GetValue<double>())).ToList() ?? [];
        var grow = s["grow"];
        var spikes = s["spikes"];
        for (var m = minutes; m >= 0; m -= 0.5)
        {
            var t = now.AddMinutes(-m);
            var mem = baseMem;
            var cpu = baseCpu;
            var top = steady.Select((p, i) => new ProcessSample(100 + i, p.name, 2, (long)p.mb << 20, 0, 0)).ToList();
            if (grow is not null && m <= grow["fromMinutesAgo"]!.GetValue<double>())
            {
                var from = grow["fromMinutesAgo"]!.GetValue<double>();
                var f = Math.Clamp((from - m) / Math.Max(1, from - 1), 0, 1);
                top.Add(new ProcessSample(900, grow["name"]!.ToString(), 6, (long)(200 + f * (grow["toMb"]!.GetValue<double>() - 200)) << 20, 0, 0));
                mem = baseMem + f * (grow["memTo"]!.GetValue<double>() - baseMem);
                cpu = baseCpu + 6;
            }
            if (spikes is not null)
            {
                var every = spikes["everyMinutes"]!.GetValue<double>();
                var spiking = Math.Abs(m % every) < 0.01;
                top.Add(new ProcessSample(901, spikes["name"]!.ToString(), spiking ? spikes["cpu"]!.GetValue<double>() : 0.5,
                    (long)spikes["mb"]!.GetValue<double>() << 20, spiking ? 40L << 20 : 0, 0));
                if (spiking) cpu = 96;
            }
            list.Add(new Snapshot(0, t, cpu, Math.Round(mem), 1L << 30, false, top));
        }
        return list;
    }

    private static List<ReplaySample> Replay(List<Snapshot> series, DateTime incident)
    {
        var samples = new List<ReplaySample>();
        for (var dt = -60; dt <= 6; dt += 2)
        {
            var t = incident.AddSeconds(dt);
            var s = series.MinBy(x => Math.Abs((x.TimeUtc - t).TotalSeconds))!;
            samples.Add(new ReplaySample(t, s.TotalCpuPercent, s.MemoryUsedPercent, 0,
                s.Top.OrderByDescending(p => p.WorkingSetBytes).Take(5).Select(p => new ReplayProcess(p.Name, p.CpuPercent, p.WorkingSetBytes >> 20, p.DiskBytesPerSec)).ToList()));
        }
        return samples;
    }

    /// <summary>What Windows itself would have logged (for setup A). ResMan-only signals have no Windows log line.</summary>
    private static string? WindowsText(string kind, string? app, Dictionary<string, string> d, Snapshot? snap) => kind switch
    {
        HealthKind.AppHang => $"Application  Application Hang  1002  Error  The program {app} version 1.0.0.0 stopped interacting with Windows and was closed.",
        HealthKind.AppCrash => d.TryGetValue("Exception", out var ex)
            ? $"Application  .NET Runtime  1026  Error  Application: {app} Description: The process was terminated due to an unhandled exception. Exception Info: {ex}"
            : $"Application  Application Error  1000  Error  Faulting application name: {app}, version: 6.4.1.0, Exception code: 0xc0000005, Fault offset: 0x00000000001a2b3c",
        HealthKind.BlueScreen => $"System  Microsoft-Windows-WER-SystemErrorReporting  1001  Error  The computer has rebooted from a bugcheck. The bugcheck was: {d.GetValueOrDefault("BugcheckCode", "0x0")} ({d.GetValueOrDefault("param1", "")}).",
        HealthKind.UnexpectedShutdown => "System  Microsoft-Windows-Kernel-Power  41  Critical  The system has rebooted without cleanly shutting down first. BugcheckCode 0.",
        HealthKind.DisplayDriverReset => "System  Display  4101  Warning  Display driver nvlddmkm stopped responding and has successfully recovered.",
        HealthKind.ThermalThrottle => "System  Microsoft-Windows-Kernel-Processor-Power  37  Warning  The speed of processor 0 in group 0 is being limited by system firmware. The processor has been in this reduced performance state for 71 seconds since the last report.",
        HealthKind.DiskError => @"System  disk  7  Error  The device, \Device\Harddisk0\DR0, has a bad block.",
        HealthKind.LowMemory => $"System  Microsoft-Windows-Resource-Exhaustion-Detector  2004  Warning  Windows successfully diagnosed a low virtual memory condition. The following programs consumed the most virtual memory: " +
                                string.Join(", ", snap?.Top.OrderByDescending(p => p.WorkingSetBytes).Take(3).Select(p => $"{p.Name}.exe ({p.WorkingSetBytes} bytes)") ?? []),
        HealthKind.WifiDrop => $"Microsoft-Windows-WLAN-AutoConfig/Operational  WLAN-AutoConfig  8003  Information  WLAN AutoConfig service has successfully disconnected from a wireless network. Network: {d.GetValueOrDefault("SSID", "HomeNet")} Reason: {d.GetValueOrDefault("Reason", "The network is disconnected by the driver.")}",
        HealthKind.SlowBoot => $"Microsoft-Windows-Diagnostics-Performance/Operational  Diagnostics-Performance  100  Warning  Windows has started up: Boot Duration: {d.GetValueOrDefault("BootTime", "0")}ms",
        HealthKind.Noise => "System  Microsoft-Windows-DistributedCOM  10016  Warning  The application-specific permission settings do not grant Local Activation permission for the COM Server application with CLSID {2593F8B9-4EAF-457C-B68A-50F6B8EA6B54} to the user NT AUTHORITY\\LOCAL SERVICE.",
        _ => null, // drive health, network monitor: ResMan-only evidence
    };
}
