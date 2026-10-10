using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResMan.Core;

// resman: a terminal stand-in for the ResMan UI. Talks to the agent's local API.

if (args.Length == 0 || args[0] is "help" or "-h" or "--help") return Help();

// Hidden helpers used by `simulate`.
if (args[0] == "_hang") { SimulatedHang(); return 0; }
if (args[0] == "_crash") { Thread.Sleep(500); throw new InvalidOperationException("Hindsight simulated crash (this is expected)."); }

if (args[0] == "simulate") return Simulate(args.ElementAtOrDefault(1));

var api = Api.Connect();
if (api is null)
{
    Err("The Hindsight agent isn't running. Start it with: dotnet run --project src/ResMan.Agent");
    return 1;
}

try
{
    return args[0] switch
    {
        "status" => await Status(api),
        "issues" => await Issues(api),
        "timeline" => await Timeline(api, int.TryParse(args.ElementAtOrDefault(1), out var d) ? d : 7),
        "findings" => await Findings(api),
        "followup" => await FollowUp(api, string.Join(' ', args.Skip(1))),
        "replay" => await Replay(api, long.Parse(args[1]), args.Contains("--narrate")),
        "ai" => await Ai(api),
        "preload" => await Preload(api, int.TryParse(args.ElementAtOrDefault(1), out var pm) ? pm : 30),
        "demo" => await Demo(api, args.Contains("--no-preload")),
        "why" => await Why(api, string.Join(' ', args.Skip(1).Where(a => a != "--force")), args.Contains("--force")),
        "explain-event" => await Explain(api, long.Parse(args[1]), args.Contains("--force")),
        "ask" => await Ask(api, string.Join(' ', args.Skip(1).Where(a => a is not ("--apply" or "--force"))), args.Contains("--apply"), args.Contains("--force")),
        "rules" when args.ElementAtOrDefault(1) == "rm" => await RemoveRule(api, long.Parse(args[2])),
        "rules" => await Rules(api),
        "dry-run" => await DryRun(api),
        "process" => await ProcessInfo(api, args[1]),
        "changes" => await Changes(api, int.TryParse(args.ElementAtOrDefault(1), out var cd) ? cd : 14),
        "drives" => await Drives(api),
        "report" => await Report(api, args.ElementAtOrDefault(1)),
        "fixed" when args.Length >= 3 => await Fixed(api, args[1], string.Join(' ', args.Skip(2))),
        "fixes" => await Fixes(api),
        "scan" => await Scan(api),
        "watch" => await Watch(api),
        _ => Help(),
    };
}
catch (HttpRequestException ex)
{
    Err($"Couldn't reach the agent: {ex.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine("""
        resman: ResMan, the black box recorder for PC freezes and crashes

          status                 Health, service and agent status, and Hindsight's own footprint
          issues                 Problems that need attention, with likely causes and fixes
          timeline [days]        Plain-language history of what happened (default 7 days)
          changes [days]         What changed: installs, updates, drivers, startup items (default 14)
          drives                 Drive health (status, wear, errors, temperature)
          why "<symptom>"        Ask what's wrong, e.g. resman why "my pc froze"
          explain-event <id>     Explain one event from the timeline
          fixed <issue-id> "<what you did>"   Record a fix so Hindsight can tell whether it worked
          fixes                  Fixes tried, and how often the problem happened before and after
          report [file.html]     Save a shareable report for whoever fixes this PC, and open it
          findings               Patterns found by Hindsight's built-in analyzers
          scan                   Check for changes and drive health right now
          process <name>         What a program is and whether it's safe to limit
          watch                  Live events as they happen (Ctrl+C to stop)
          replay <event-id> [--narrate]   The black-box minute before a freeze/crash, narrated by the local AI
          followup "<question>"  Ask more about the last diagnosis (e.g. "is it safe to remove?")
          ai                     Local AI footprint: state, RAM/VRAM, load and inference times
          preload [minutes]      Load the model now and keep it warm (for demos)
          demo [--no-preload]    Run the scripted demo: memory hog + freezing app + crashing app
          simulate freeze|crash  Create a test freeze or crash

        Not part of v1 (stubs): ask "<request>" [--apply], rules, rules rm <id>, dry-run

        AI questions are paused while you're gaming; add --force to ask anyway.
        """);
    return 0;
}

static async Task<int> Status(Api api)
{
    var s = await api.Get("status");
    var h = s["health"];
    if (h is null) Warn("No data yet: the Hindsight service hasn't created its database. Is it running?");
    else
    {
        var level = h["level"]!.GetValue<string>();
        Color(level switch { "good" => ConsoleColor.Green, "attention" => ConsoleColor.Yellow, _ => ConsoleColor.Red },
            $"\n  {h["headline"]}");
        Console.WriteLine($"  {h["detail"]}");
        if (h["hiddenWarnings"]!.GetValue<long>() > 0) Dim($"  {h["hiddenWarnings"]} harmless system warnings hidden this week");
    }

    Console.WriteLine();
    var svc = s["service"];
    if (svc is null) Warn("  Service:  not running");
    else
    {
        Console.WriteLine($"  Service:  running{(svc["elevated"]!.GetValue<bool>() ? " (admin)" : " (not admin: some logs unavailable)")}, {svc["workingSetBytes"]!.GetValue<long>() >> 20} MB, " +
                          $"{svc["snapshotsStored"]} snapshots, {svc["healthEventsStored"]} events");
        foreach (var w in svc["warnings"]!.AsArray()) Dim($"            {w}");
    }
    var agent = s["agent"]!;
    Console.WriteLine($"  Agent:    {agent["workingSetMb"]} MB, profile '{agent["profile"]}'{(agent["paused"]!.GetValue<bool>() ? ", PAUSED" : "")}, foreground: {agent["foregroundApp"] ?? "-"}");
    var ai = s["ai"]!;
    Console.WriteLine($"  AI:       {ai["describe"]}{(ai["lastRunUtc"] is { } t ? $", last ran {Ago(t.GetValue<DateTime>())}" : "")}");
    Console.WriteLine($"  Hindsight is using {s["footprint"]!["totalMb"]} MB in total (the AI isn't counted while it's unloaded)\n");
    return 0;
}

static async Task<int> Issues(Api api)
{
    var issues = (await api.Get("issues")).AsArray();
    if (issues.Count == 0) { Color(ConsoleColor.Green, "No issues. Your PC looks healthy."); return 0; }
    foreach (var i in issues)
    {
        var sev = i!["severity"]!.GetValue<string>();
        Color(SevColor(sev), $"\n  [{sev}] {i["title"]}");
        Console.WriteLine($"  {i["explanation"]}");
        if (i["evidence"] is { } ev) Console.WriteLine($"  What Hindsight saw: {ev}");
        foreach (var f in i["fixes"]!.AsArray())
            Console.WriteLine($"    -> {f!["label"]}{(f["uri"] is { } u ? $"  ({u})" : "")}");
        if (i["needsHuman"]!.GetValue<bool>()) Color(ConsoleColor.Magenta, "    This may be a hardware problem worth getting checked.");
        Dim($"  id: {i["id"]}  last seen {Ago(i["lastSeenUtc"]!.GetValue<DateTime>())}, events: {string.Join(",", i["eventIds"]!.AsArray().Take(8))}");
    }
    Console.WriteLine();
    return 0;
}

static async Task<int> Timeline(Api api, int days)
{
    var entries = (await api.Get($"timeline?days={days}")).AsArray();
    if (entries.Count == 0) { Console.WriteLine($"Nothing happened in the last {days} days."); return 0; }
    foreach (var e in entries)
    {
        var t = e!["timeUtc"]!.GetValue<DateTime>().ToLocalTime();
        var count = e["count"]!.GetValue<int>();
        Console.Write($"  {t:ddd MMM d h:mm tt}  ");
        Color(SevColor(e["severity"]!.GetValue<string>()), $"{e["title"]}{(count > 1 ? $" (x{count})" : "")}", newline: false);
        Dim($"  #{e["eventIds"]![0]}");
        if (e["detail"] is { } d) Dim($"                       {d}");
    }
    return 0;
}

static async Task<int> Findings(Api api)
{
    var f = (await api.Get("findings")).AsArray();
    if (f.Count == 0) { Console.WriteLine("No patterns found this week."); return 0; }
    foreach (var x in f) Console.WriteLine($"  [{x!["severity"]}] {x["title"]}\n      {x["evidence"]}");
    return 0;
}

static async Task<int> Why(Api api, string symptom, bool force)
{
    if (string.IsNullOrWhiteSpace(symptom)) { Err("Usage: resman why \"my pc froze\""); return 1; }
    Dim("Thinking (the local model may take a moment to load)...");
    var r = await api.Post($"why{(force ? "?force=true" : "")}", new { symptom });
    return PrintAnswer(r);
}

static async Task<int> Explain(Api api, long id, bool force)
{
    Dim("Thinking...");
    return PrintAnswer(await api.Post($"events/{id}/explain{(force ? "?force=true" : "")}", new { }));
}

static int PrintAnswer(JsonNode a)
{
    if (a["sessionId"]?.ToString() is { Length: > 0 } sid) File.WriteAllText(SessionFile(), sid);
    Console.WriteLine();
    Color(ConsoleColor.White, "  " + a["summary"]);
    Console.WriteLine($"  Likely cause: {a["likelyCause"]}  (confidence: {a["confidence"]})");
    if (a["evidence"]?.AsArray() is { Count: > 0 } evidence)
    {
        Console.WriteLine("\n  Evidence:");
        foreach (var e in evidence) Console.WriteLine($"    * {e}");
    }
    var steps = a["steps"]!.AsArray();
    if (steps.Count > 0)
    {
        Console.WriteLine("\n  What you can do:");
        var n = 1;
        foreach (var s in steps) Console.WriteLine($"    {n++}. {s}");
    }
    var actions = a["actions"]!.AsArray().Where(x => x?["uri"] is not null).ToList();
    if (actions.Count > 0)
    {
        Console.WriteLine("\n  Shortcuts:");
        foreach (var x in actions) Console.WriteLine($"    -> {x!["label"]}  ({x["uri"]})");
    }
    if (a["needsHuman"]!.GetValue<bool>()) Color(ConsoleColor.Magenta, "\n  This may be a hardware problem; consider having it checked.");
    var trace = a["toolTrace"]!.AsArray();
    if (trace.Count > 0) Dim($"\n  Investigation: {string.Join(" -> ", trace)}");
    Dim($"  {(a["usedModel"]!.GetValue<bool>() ? $"Local AI on this PC, {a["modelCalls"]} model call(s), {a["modelMilliseconds"]!.GetValue<double>() / 1000:0.0}s, nothing sent online" : "Built-in checks (no AI)")}" +
        $"{(a["note"] is { } note ? $". {note}" : "")}");
    if (a["sessionId"] is not null) Dim("  Ask a follow-up: resman followup \"is it safe to remove?\"\n");
    return 0;
}

static string SessionFile() => Path.Combine(ResManPaths.UserDir, "cli-session.txt");

static async Task<int> FollowUp(Api api, string question)
{
    if (string.IsNullOrWhiteSpace(question)) { Err("Usage: resman followup \"is it safe to remove?\""); return 1; }
    if (!File.Exists(SessionFile())) { Err("Ask something first with: resman why \"...\""); return 1; }
    Dim("Thinking...");
    return PrintAnswer(await api.Post("followup", new { sessionId = File.ReadAllText(SessionFile()).Trim(), question }));
}

static async Task<int> Replay(Api api, long id, bool narrate)
{
    var r = await api.Get($"events/{id}/replay");
    if (!r["available"]!.GetValue<bool>()) { Warn("No black-box recording for that event (only live incidents are recorded)."); return 1; }
    Console.WriteLine($"\n  Black box: {r["title"]}  (the minute before, every 2 s)\n");
    Console.WriteLine("    time    CPU                   memory                busiest");
    foreach (var s in r["samples"]!.AsArray())
    {
        double cpu = s!["cpu"]!.GetValue<double>(), mem = s["memory"]!.GetValue<double>();
        var top = s["top"]!.AsArray().FirstOrDefault();
        Console.Write($"  {s["tSeconds"]!.GetValue<double>(),5:+0;-0;0}s  ");
        Color(cpu >= 85 ? ConsoleColor.Red : ConsoleColor.Gray, $"{Bar(cpu)} {cpu,3:0}%  ", newline: false);
        Color(mem >= 90 ? ConsoleColor.Red : ConsoleColor.Gray, $"{Bar(mem)} {mem,3:0}%  ", newline: false);
        Dim(top is null ? "" : $"{top["name"]} {top["workingSetMb"]} MB");
    }
    if (!narrate) { Dim("\n  Add --narrate to have the local AI tell the story.\n"); return 0; }
    Dim("\n  Narrating...");
    var n = await api.Post($"events/{id}/replay/narrate", new { });
    Color(ConsoleColor.White, $"\n  {n["narration"]}");
    foreach (var m in n["keyMoments"]!.AsArray()) Console.WriteLine($"    {m!["t"],6}  {m["what"]}");
    Console.WriteLine($"  Likely cause: {n["likelyCause"]}");
    Dim($"  {(n["usedModel"]!.GetValue<bool>() ? $"Local AI, {n["modelMilliseconds"]!.GetValue<double>() / 1000:0.0}s" : "No AI")}\n");
    return 0;

    static string Bar(double pct) => new string('#', (int)Math.Round(pct / 5)).PadRight(20, '.');
}

static async Task<int> Ai(Api api)
{
    var a = await api.Get("ai/status");
    var t = a["timings"]!;
    var sc = a["sidecar"]!;
    var rm = a["resman"]!;
    Console.WriteLine($"\n  Model:     {a["model"]} ({a["modelFileMb"]} MB file), context {a["contextSize"]} tokens");
    Color(a["state"]!.ToString() == "unloaded" ? ConsoleColor.Gray : ConsoleColor.Green, $"  State:     {a["state"]}{(a["backend"] is { } b ? $" on {b}" : "")}");
    Console.WriteLine($"  While loaded: RAM {sc["privateMb"]?.ToString() ?? "-"} MB, VRAM {sc["vramMb"]?.ToString() ?? "-"} MB");
    Console.WriteLine($"  Timings:   load {Ms(t["loadMs"])}, last inference {Ms(t["lastInferenceMs"])}, {t["tokensPerSecond"]?.GetValue<double>().ToString("0") ?? "-"} tokens/s, last question {Ms(t["lastQuestionMs"])}");
    Console.WriteLine($"  Unloads after {a["idleUnloadSeconds"]} s idle");
    Console.WriteLine($"  Hindsight without AI: {rm["totalWithoutAiMb"]} MB (agent {rm["agentMb"]} + service {rm["serviceMb"]}); right now: {rm["totalNowMb"]} MB");
    Dim($"  Network used by AI: {a["networkUsedByAi"]}\n");
    return 0;

    static string Ms(JsonNode? n) => n is null ? "-" : $"{n.GetValue<double>() / 1000:0.0}s";
}

static async Task<int> Preload(Api api, int minutes)
{
    Dim("Loading the local model...");
    var r = await api.Post("ai/preload", new { minutes });
    Color(ConsoleColor.Green, $"Ready in {r["tookMs"]!.GetValue<double>() / 1000:0.0}s on {r["backend"]}; staying warm for {r["keepWarmMinutes"]} min.");
    return 0;
}

/// <summary>
/// The deterministic demo: a background app eats memory, then a drawing app freezes and crashes repeatedly.
/// Neither app is in ResMan's knowledge base, so the AI has to investigate them.
/// </summary>
static async Task<int> Demo(Api api, bool noPreload)
{
    var demo = ResManPaths.FindUp("demo");
    // Packaged: the demo apps sit next to resman.exe. Development: under demo/<name>/bin.
    string Exe(string name) => new[] { Path.Combine(AppContext.BaseDirectory, name + ".exe") }
        .Concat(new[] { "Release", "Debug" }.Select(c => Path.Combine(demo ?? "", name, "bin", c, "net10.0-windows10.0.19041.0", name + ".exe")))
        .FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException($"Build the demo apps first: dotnet build demo/{name}");
    var sketch = Exe("SketchPad");
    var photos = Exe("PhotoSync");

    Color(ConsoleColor.Cyan, "\n[1/4] PhotoSync starts quietly using memory in the background...");
    using var hog = Process.Start(new ProcessStartInfo(photos, "93 75") { UseShellExecute = false, CreateNoWindow = true })!;
    await Task.Delay(TimeSpan.FromSeconds(20));
    Color(ConsoleColor.Cyan, "[2/4] SketchPad opens a big canvas and freezes (keep its window in front)...");
    Process.Start(sketch, "hang 18")!.WaitForExit();
    await Task.Delay(TimeSpan.FromSeconds(4));
    Color(ConsoleColor.Cyan, "[3/4] SketchPad crashes on launch, three times...");
    for (var i = 0; i < 3; i++)
    {
        using var p = Process.Start(new ProcessStartInfo(sketch, "crash") { UseShellExecute = false, RedirectStandardError = true })!;
        _ = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
    Color(ConsoleColor.Cyan, "[4/4] Waiting for Windows to finish recording the crashes...");
    await Task.Delay(TimeSpan.FromSeconds(8));
    try { hog.Kill(); } catch { }

    // Load the model only after the incident, like real life: the AI isn't running while you work.
    if (!noPreload) { try { await Preload(api, 30); } catch (HttpRequestException ex) { Warn($"Preload skipped: {ex.Message}"); } }

    var timeline = (await api.Get("timeline?days=1")).AsArray().Take(4);
    Console.WriteLine("\n  Recorded:");
    foreach (var e in timeline) Console.WriteLine($"    #{e!["eventIds"]![0]}  {e["title"]}{(e["count"]!.GetValue<int>() > 1 ? $" (x{e["count"]})" : "")}");
    Console.WriteLine("""

      Try:
        resman why "my drawing app keeps freezing and closing"
        resman followup "what is PhotoSync and is it safe to remove?"
        resman replay <freeze event id> --narrate
        resman ai
    """);
    return 0;
}

static async Task<int> Ask(Api api, string request, bool apply, bool force)
{
    if (string.IsNullOrWhiteSpace(request)) { Err("Usage: resman ask \"slow down onedrive while I'm gaming\""); return 1; }
    Dim("Thinking...");
    var s = await api.Post($"rules/suggest{(force ? "?force=true" : "")}", new { request });
    if (s["clarify"] is { } c) { Console.WriteLine($"\n  {c}\n"); return 0; }
    var rule = s["rule"];
    if (rule is null) { Err(string.Join(" ", s["errors"]!.AsArray())); return 1; }
    Console.WriteLine($"\n  Proposed rule: {rule["action"]} = {rule["value"]} for '{rule["process"]}' when profile is '{rule["profile"]}'");
    Console.WriteLine($"  Why: {rule["reason"]}");
    if (!s["valid"]!.GetValue<bool>())
    {
        Color(ConsoleColor.Red, $"  Rejected by Hindsight's safety checks: {string.Join(" ", s["errors"]!.AsArray())}");
        return 1;
    }
    Color(ConsoleColor.Green, "  Passed Hindsight's safety checks.");
    if (!apply) { Dim("  Add --apply to save it. Rules run in dry-run mode for now.\n"); return 0; }
    var added = await api.Post("rules", rule);
    Console.WriteLine($"  Saved as rule #{added["id"]}. See what it would do with: resman dry-run\n");
    return 0;
}

static async Task<int> Rules(Api api)
{
    var rules = (await api.Get("rules")).AsArray();
    if (rules.Count == 0) { Console.WriteLine("No rules yet. Try: resman ask \"keep onedrive quiet while I game\" --apply"); return 0; }
    foreach (var r in rules)
    {
        var x = r!["rule"]!;
        Console.WriteLine($"  #{r["id"]}  {x["action"]}={x["value"]}  {x["process"]}  ({x["profile"]})  {x["reason"]}");
    }
    return 0;
}

static async Task<int> RemoveRule(Api api, long id)
{
    await api.Delete($"rules/{id}");
    Console.WriteLine($"Removed rule #{id}.");
    return 0;
}

static async Task<int> DryRun(Api api)
{
    var log = (await api.Get("rules/dry-run")).AsArray();
    if (log.Count == 0) { Console.WriteLine("The rules engine hasn't matched anything yet."); return 0; }
    foreach (var e in log) Console.WriteLine($"  {e!["timeUtc"]!.GetValue<DateTime>().ToLocalTime():h:mm:ss tt}  {e["description"]}");
    return 0;
}

static async Task<int> ProcessInfo(Api api, string name)
{
    var p = await api.Get($"processes/{Uri.EscapeDataString(name)}");
    if (p["info"] is { } i)
        Console.WriteLine($"\n  {i["title"]} ({i["publisher"]})\n  {i["what"]}\n  {i["notes"]}\n  Safe to limit: {(i["safeToLimit"]!.GetValue<bool>() ? "yes" : "no")}");
    if (p["binary"] is { } b)
        Console.WriteLine($"\n  Running from: {b["path"]}\n  {b["description"]} by {b["company"] ?? "unknown"}; {(b["signer"] is { } sg ? $"signed by {sg}" : "not signed")}; {b["location"]}");
    if (p["isProtected"]!.GetValue<bool>()) Color(ConsoleColor.Yellow, "  Protected: Hindsight will never change this process.");
    Console.WriteLine();
    return 0;
}

static async Task<int> Changes(Api api, int days)
{
    var result = await api.Get($"changes?days={days}");
    var list = result["groups"]!.AsArray();
    if (list.Count == 0) Console.WriteLine($"No changes recorded in the last {days} days.");
    foreach (var c in list)
        Console.WriteLine($"  {c!["timeUtc"]!.GetValue<DateTime>().ToLocalTime():ddd MMM d h:mm tt}  {c["description"]}");
    var hidden = result["routineHidden"]!.GetValue<int>();
    Dim($"\n  Grouped: one installer often adds many components at once.{(hidden > 0 ? $" {hidden} routine antivirus updates hidden." : "")}");
    return 0;
}

static async Task<int> Drives(Api api)
{
    var list = (await api.Get("drives")).AsArray();
    if (list.Count == 0) { Console.WriteLine("No drive readings yet. Try: resman scan"); return 0; }
    foreach (var d in list)
    {
        var status = d!["healthStatus"]!.GetValue<string>();
        Color(status == "Healthy" ? ConsoleColor.Green : status == "Unknown" ? ConsoleColor.Gray : ConsoleColor.Red,
            $"  {d["model"]} ({d["mediaType"]}): {status}", newline: false);
        if (d["wearPercent"] is null && d["readErrorsTotal"] is null)
            Dim("  (wear and error counters need the service to run as admin)");
        else
            Dim($"  wear {d["wearPercent"]?.ToString() ?? "?"}%, read/write errors {d["readErrorsTotal"]?.ToString() ?? "?"}/{d["writeErrorsTotal"]?.ToString() ?? "?"}, " +
                $"{(d["temperatureC"] is { } t ? $"{t} C" : "temp unknown")}, {d["powerOnHours"]?.ToString() ?? "?"} h powered on");
    }
    return 0;
}

static async Task<int> Report(Api api, string? path)
{
    var html = await api.GetText("report");
    path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"PC health report {DateTime.Now:yyyy-MM-dd}.html");
    File.WriteAllText(path, html);
    Console.WriteLine($"Saved: {path}\nOpen it in a browser; use Print > Save as PDF for a PDF.");
    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    return 0;
}

static async Task<int> Fixed(Api api, string issueId, string label)
{
    var r = await api.Post("fixes", new { issueId, label });
    Color(ConsoleColor.Green, $"Recorded fix #{r["id"]}. {r["tracking"]}");
    return 0;
}

static async Task<int> Fixes(Api api)
{
    var list = (await api.Get("fixes")).AsArray();
    if (list.Count == 0) { Console.WriteLine("No fixes recorded yet. After you try one: resman fixed <issue-id> \"what you did\""); return 0; }
    foreach (var f in list)
    {
        var verdict = f!["verdict"]!.GetValue<string>();
        Console.WriteLine($"  {f["fix"]!["appliedUtc"]!.GetValue<DateTime>().ToLocalTime():ddd MMM d}  {f["fix"]!["label"]}");
        Color(verdict switch { "fixed" or "better" => ConsoleColor.Green, "worse" => ConsoleColor.Red, _ => ConsoleColor.Gray }, $"      {f["summary"]}");
    }
    return 0;
}

static async Task<int> Scan(Api api)
{
    Dim("Checking for changes and drive health...");
    await api.Post("scan", new { });
    Console.WriteLine("Done. See: resman changes, resman drives");
    return 0;
}

static async Task<int> Watch(Api api)
{
    using var ws = new ClientWebSocket();
    var uri = new Uri(api.BaseUrl.Replace("http://", "ws://") + "/api/v1/live?token=" + api.Token);
    await ws.ConnectAsync(uri, CancellationToken.None);
    Console.WriteLine("Watching for live events (Ctrl+C to stop)...");
    var buf = new byte[16 * 1024];
    while (ws.State == WebSocketState.Open)
    {
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        if (r.MessageType == WebSocketMessageType.Close) break;
        var msg = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))!;
        if (msg["type"]?.ToString() == "freeze_explained")
        {
            var x = msg["data"]!;
            Color(ConsoleColor.Yellow, $"  {DateTime.Now:h:mm:ss tt}  {x["headline"]}");
            Console.WriteLine($"              {x["explanation"]}{(x["recentChange"] is { } rc ? " " + rc : "")}");
        }
        else Console.WriteLine($"  {DateTime.Now:h:mm:ss tt}  {msg["type"]}: {msg["data"]!.ToJsonString()}");
    }
    return 0;
}

static int Simulate(string? what)
{
    var self = Environment.ProcessPath!;
    switch (what)
    {
        case "freeze":
            Console.WriteLine("Opening a test window that will freeze for 20 seconds. Keep it in front so Hindsight sees it.");
            Process.Start(self, "_hang")?.WaitForExit();
            Console.WriteLine("Done. Check `resman timeline` in a few seconds.");
            return 0;
        case "crash":
            Console.WriteLine("Starting a test program that will crash on purpose...");
            using (var p = Process.Start(new ProcessStartInfo(self, "_crash") { UseShellExecute = false, RedirectStandardError = true })!)
            {
                p.StandardError.ReadToEnd();
                p.WaitForExit();
            }
            Console.WriteLine("It crashed as expected. Windows records it within a few seconds; then check `resman timeline`.");
            return 0;
        default:
            Err("Usage: resman simulate freeze|crash");
            return 1;
    }
}

static void SimulatedHang()
{
    Application.EnableVisualStyles();
    var form = new Form { Text = "Hindsight test: this window will freeze", Width = 520, Height = 180, StartPosition = FormStartPosition.CenterScreen, TopMost = true };
    form.Controls.Add(new Label { Text = "This window will stop responding for 20 seconds.\nClick on it or try to move it so Windows notices.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
    form.Shown += async (_, _) =>
    {
        form.Activate();
        await Task.Delay(1500);
        Thread.Sleep(20_000); // block the UI thread on purpose
        form.Close();
    };
    Application.Run(form);
}

static string Ago(DateTime utc)
{
    var d = DateTime.UtcNow - utc;
    return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{d.TotalMinutes:0} min ago" : d.TotalDays < 1 ? $"{d.TotalHours:0} h ago" : $"{d.TotalDays:0} days ago";
}

static ConsoleColor SevColor(string s) => s switch { "critical" => ConsoleColor.Red, "warning" => ConsoleColor.Yellow, "notice" => ConsoleColor.Cyan, _ => ConsoleColor.Gray };

static void Color(ConsoleColor c, string text, bool newline = true)
{
    var old = Console.ForegroundColor;
    Console.ForegroundColor = c;
    if (newline) Console.WriteLine(text); else Console.Write(text);
    Console.ForegroundColor = old;
}
static void Dim(string text) => Color(ConsoleColor.DarkGray, text);
static void Warn(string text) => Color(ConsoleColor.Yellow, text);
static void Err(string text) => Color(ConsoleColor.Red, text);

sealed class Api
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }
    public string Token { get; }

    private Api(string url, string token)
    {
        BaseUrl = url;
        Token = token;
        _http = new HttpClient { BaseAddress = new Uri(url + "/api/v1/"), Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static Api? Connect()
    {
        try
        {
            var info = JsonNode.Parse(File.ReadAllText(ResManPaths.AgentInfoPath))!;
            var pid = info["pid"]!.GetValue<int>();
            try { using var _ = Process.GetProcessById(pid); } catch { return null; }
            return new Api(info["url"]!.GetValue<string>(), info["token"]!.GetValue<string>());
        }
        catch { return null; }
    }

    public async Task<JsonNode> Get(string path) => await Read(await _http.GetAsync(path));

    public async Task<string> GetText(string path)
    {
        var r = await _http.GetAsync(path);
        var text = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new HttpRequestException(JsonNode.Parse(text)?["error"]?.ToString() ?? $"{(int)r.StatusCode}");
        return text;
    }
    public async Task<JsonNode> Post(string path, object body) => await Read(await _http.PostAsJsonAsync(path, body));
    public async Task Delete(string path) => await Read(await _http.DeleteAsync(path), allowEmpty: true);

    private static async Task<JsonNode> Read(HttpResponseMessage r, bool allowEmpty = false)
    {
        var text = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode)
        {
            var msg = text.Length > 0 && JsonNode.Parse(text)?["error"] is { } e ? e.ToString() : $"{(int)r.StatusCode} {r.ReasonPhrase}";
            throw new HttpRequestException(msg);
        }
        return text.Length == 0 && allowEmpty ? new JsonObject() : JsonNode.Parse(text)!;
    }
}
