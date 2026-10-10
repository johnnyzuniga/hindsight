using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResMan.Agent;
using ResMan.Brain.Doctor;
using ResMan.Brain.Knowledge;
using ResMan.Brain.Models;
using ResMan.Core;

// ResMan.Agent: runs as the signed-in user. Hosts the Brain, the hang and context detectors, the tray icon,
// and a local API on 127.0.0.1 that any UI (WinUI, React Native, Flutter, or the CLI) can use.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var inference = DoctorHost.ResolveInference(builder.Configuration);
var sidecar = new SidecarModelProvider(inference);
IModelProvider model = sidecar;
var host = new DoctorHost(KnowledgeBase.LoadDefault(), model);
var state = new AgentState();

builder.Services.AddSingleton(host);
builder.Services.AddSingleton(state);
builder.Services.AddSingleton<ServiceClient>();
builder.Services.AddSingleton(sidecar);
builder.Services.AddSingleton<AiTelemetry>();
builder.Services.AddHostedService<HangDetector>();
builder.Services.AddHostedService<ContextDetector>();
builder.Services.AddHostedService<LiveWatcher>();

var app = builder.Build();
var service = app.Services.GetRequiredService<ServiceClient>();
var ai = app.Services.GetRequiredService<AiTelemetry>();
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var tokenBytes = Encoding.UTF8.GetBytes(token);

// Only local, authenticated callers. The Host check blocks DNS-rebinding attacks from web pages.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 400; return; }
    var auth = ctx.Request.Headers.Authorization.ToString();
    var given = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : ctx.Request.Query["token"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), tokenBytes)) { ctx.Response.StatusCode = 401; return; }
    await next();
});
app.UseWebSockets();

var api = app.MapGroup("/api/v1");
IResult NoData() => Results.Json(new { error = "The Hindsight service hasn't collected any data yet. Is it running?" }, statusCode: 503);
IResult? GamingLockout(bool force) => state.Profile == Profiles.Gaming && !force
    ? Results.Json(new { error = "Hindsight's AI is paused while you're gaming so it doesn't slow your game down. Ask again afterwards, or add ?force=true." }, statusCode: 409)
    : null;

api.MapGet("/status", async () =>
{
    var a = host.Doctor?.Analyze();
    var svc = await service.GetAsync(PipeTypes.Status, ResManJson.Default.ServiceStatus);
    using var me = Process.GetCurrentProcess();
    return Results.Ok(new
    {
        health = a?.Status,
        service = svc,
        agent = new
        {
            workingSetMb = me.PrivateMemorySize64 >> 20,
            paused = state.Paused,
            profile = state.Profile,
            foregroundApp = state.ForegroundApp,
            hangsDetected = state.HangsDetected,
        },
        ai = new { enabled = model.Enabled, describe = model.Describe, lastRunUtc = state.LastAiRunUtc },
        footprint = new { totalMb = ((svc?.WorkingSetBytes ?? 0) + me.PrivateMemorySize64) >> 20, measure = "private memory (what Task Manager shows)" },
    });
});

api.MapGet("/issues", () => host.Doctor is { } d ? Results.Ok(d.Analyze().Issues) : NoData());
api.MapGet("/findings", () => host.Doctor is { } d ? Results.Ok(d.Analyze().Findings) : NoData());
api.MapGet("/timeline", (int? days) => host.Doctor is { } d ? Results.Ok(d.Timeline(Math.Clamp(days ?? 7, 1, 30))) : NoData());

// Resource history plus individual incidents in the window: feeds the UI's traces.
api.MapGet("/history", (int? minutes) =>
{
    if (host.Doctor is not { } d) return NoData();
    var span = Math.Clamp(minutes ?? 60, 5, 24 * 60);
    var now = DateTime.UtcNow;
    var from = now.AddMinutes(-span);
    var snaps = d.Telemetry.Range(from, now);
    var incidents = d.Analyze().Events
        .Where(e => e.TimeUtc >= from && e.Kind is not (HealthKind.Noise or HealthKind.WifiConnect or HealthKind.Other))
        .OrderBy(e => e.TimeUtc)
        .Select(e => new { id = e.Id, e.TimeUtc, e.Kind, app = e.AppName is null ? null : host.Knowledge.FriendlyName(e.AppName), title = Reports.Describe(e, host.Knowledge).Title });
    return Results.Ok(new
    {
        fromUtc = from, toUtc = now,
        samples = snaps.Select(s => new
        {
            s.TimeUtc, cpu = Math.Round(s.TotalCpuPercent, 1), memory = Math.Round(s.MemoryUsedPercent, 1),
            diskMbps = Math.Round(s.Top.Where(p => !RuleValidator.IsOwnProcess(p.Name)).Sum(p => p.DiskBytesPerSec) / 1048576.0, 1),
        }),
        incidents,
    });
});

api.MapGet("/events/{id:long}", (long id) =>
{
    if (host.Doctor is not { } d) return NoData();
    var e = d.Health.Get(id);
    if (e is null) return Results.NotFound();
    var snapshot = e.SnapshotId is { } sid ? d.Telemetry.Get(sid) : null;
    var (title, detail) = Reports.Describe(e, host.Knowledge);
    return Results.Ok(new { title, detail, @event = e, snapshot });
});

api.MapPost("/events/{id:long}/explain", async (long id, bool? force, string? requestId, CancellationToken ct) =>
{
    if (GamingLockout(force == true) is { } locked) return locked;
    if (host.Harness is not { } h) return NoData();
    var sw = Stopwatch.StartNew();
    var answer = await h.ExplainEventAsync(id, ct, state.StepSink(requestId));
    if (answer?.UsedModel == true) { state.LastAiRunUtc = DateTime.UtcNow; ai.RecordQuestion(sw.Elapsed.TotalMilliseconds, answer.ModelMilliseconds); }
    return answer is null ? Results.NotFound() : Results.Ok(answer);
});

api.MapPost("/why", async (WhyRequest req, bool? force, string? requestId, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Symptom)) return Results.BadRequest(new { error = "Describe what's happening." });
    if (GamingLockout(force == true) is { } locked) return locked;
    if (host.Harness is not { } h) return NoData();
    var sw = Stopwatch.StartNew();
    var answer = await h.WhyAsync(req.Symptom.Trim(), ct, state.StepSink(requestId));
    if (answer.UsedModel) { state.LastAiRunUtc = DateTime.UtcNow; ai.RecordQuestion(sw.Elapsed.TotalMilliseconds, answer.ModelMilliseconds); }
    return Results.Ok(answer);
});

api.MapPost("/followup", async (FollowUpRequest req, string? requestId, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Question)) return Results.BadRequest(new { error = "Ask a question." });
    if (host.Harness is not { } h) return NoData();
    var sw = Stopwatch.StartNew();
    var answer = await h.FollowUpAsync(req.SessionId, req.Question.Trim(), ct, state.StepSink(requestId));
    if (answer is null) return Results.NotFound(new { error = "That conversation has expired. Ask the original question again." });
    if (answer.UsedModel) { state.LastAiRunUtc = DateTime.UtcNow; ai.RecordQuestion(sw.Elapsed.TotalMilliseconds, answer.ModelMilliseconds); }
    return Results.Ok(answer);
});

api.MapGet("/events/{id:long}/replay", (long id) =>
{
    if (host.Doctor is not { } d) return NoData();
    var e = d.Health.Get(id);
    if (e is null) return Results.NotFound();
    var samples = d.Replays.Get(id, e.TimeUtc);
    var (title, _) = Reports.Describe(e, host.Knowledge);
    return Results.Ok(new
    {
        eventId = id, title, incidentUtc = e.TimeUtc,
        samples = samples.Select(s => new
        {
            tSeconds = Math.Round((s.TimeUtc - e.TimeUtc).TotalSeconds, 1), s.TimeUtc, cpu = s.CpuPercent, memory = s.MemoryPercent,
            diskMbps = Math.Round(s.DiskBytesPerSec / 1048576.0, 1), top = s.Top,
        }),
        available = samples.Count > 1,
    });
});

api.MapPost("/events/{id:long}/replay/narrate", async (long id, bool? force, CancellationToken ct) =>
{
    if (GamingLockout(force == true) is { } locked) return locked;
    if (host.Harness is not { } h) return NoData();
    var sw = Stopwatch.StartNew();
    var n = await h.NarrateReplayAsync(id, ct);
    if (n is null) return Results.NotFound(new { error = "No black-box recording exists for that event." });
    if (n.UsedModel) { state.LastAiRunUtc = DateTime.UtcNow; ai.RecordQuestion(sw.Elapsed.TotalMilliseconds, n.ModelMilliseconds); }
    return Results.Ok(n);
});

api.MapGet("/ai/status", async (CancellationToken ct) => Results.Ok(await ai.SnapshotAsync(ct)));

api.MapPost("/ai/preload", async (PreloadRequest req, CancellationToken ct) =>
{
    if (!sidecar.Enabled) return Results.Json(new { error = sidecar.Describe }, statusCode: 503);
    var sw = Stopwatch.StartNew();
    var r = await sidecar.PreloadAsync(req.Minutes is > 0 ? req.Minutes.Value : 30, ct);
    return r is null ? Results.Json(new { error = "The local model didn't start." }, statusCode: 503)
        : Results.Ok(new { ready = true, tookMs = Math.Round(sw.Elapsed.TotalMilliseconds), keepWarmMinutes = (r.IdleSeconds ?? 0) / 60, r.Backend, r.LoadMilliseconds });
});

api.MapGet("/network", (double? hours) =>
{
    if (host.Doctor is not { } d) return NoData();
    var now = DateTime.UtcNow;
    return Results.Ok(d.Replays.Network(now.AddHours(-Math.Clamp(hours ?? 2, 0.1, 168)), now));
});

api.MapPost("/rules/suggest", async (SuggestRequest req, bool? force, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Request)) return Results.BadRequest(new { error = "Describe the rule you want." });
    if (GamingLockout(force == true) is { } locked) return locked;
    if (host.Harness is not { } h) return NoData();
    var suggestion = await h.ProposeRuleAsync(req.Request.Trim(), ct);
    if (suggestion.UsedModel) state.LastAiRunUtc = DateTime.UtcNow;
    return Results.Ok(suggestion);
});

api.MapGet("/rules", async () =>
    await service.GetAsync(PipeTypes.RulesList, ResManJson.Default.ListStoredRule) is { } rules ? Results.Ok(rules) : Results.Json(new { error = "The Hindsight service isn't running." }, statusCode: 503));

api.MapPost("/rules", async (RuleProposal rule) =>
{
    var v = RuleValidator.Validate(rule);
    if (!v.Ok) return Results.BadRequest(new { error = string.Join(" ", v.Errors) });
    var r = await service.SendAsync(PipeTypes.RulesAdd, rule, ResManJson.Default.RuleProposal);
    return r.Ok ? Results.Ok(new { id = r.Payload?.GetInt64() }) : Results.BadRequest(new { error = r.Error });
});

api.MapDelete("/rules/{id:long}", async (long id) =>
{
    var r = await service.SendAsync(PipeTypes.RulesRemove, id, ResManJson.Default.Int64);
    return r.Ok ? Results.NoContent() : Results.NotFound(new { error = r.Error });
});

api.MapGet("/rules/dry-run", async () =>
    Results.Ok(await service.GetAsync(PipeTypes.DryRunLog, ResManJson.Default.ListDryRunEntry) ?? []));

api.MapGet("/processes/{name}", (string name) =>
{
    var info = host.Knowledge.LookupProcess(name);
    var binary = BinaryInspector.Inspect(name);
    if (info is null && binary is null) return Results.NotFound(new { error = $"Nothing known about '{name}' and it isn't running." });
    return Results.Ok(new { info, binary, isProtected = RuleValidator.IsProtected(name) });
});

api.MapGet("/changes", (int? days) =>
{
    if (host.Doctor is not { } d) return NoData();
    var a = d.Analyze();
    var from = a.NowUtc.AddDays(-Math.Clamp(days ?? 14, 1, 60));
    var inRange = a.Changes.Where(c => c.TimeUtc >= from).ToList();
    return Results.Ok(new
    {
        routineHidden = inRange.Count(ChangeKind.IsRoutine),
        groups = Reports.GroupChanges(inRange).Select(g => new
        {
            g.TimeUtc, g.Kind, description = g.Description, g.Count,
            items = g.Items.Select(c => new { c.Id, c.TimeUtc, c.Name, c.Detail }),
        }),
    });
});

api.MapGet("/drives", () => host.Doctor is { } d ? Results.Ok(d.Analyze().Drives) : NoData());

api.MapGet("/fixes", () => host.Doctor is { } d ? Results.Ok(d.Analyze(fresh: true).Fixes) : NoData());

api.MapPost("/fixes", async (FixRequest req) =>
{
    if (host.Doctor is not { } d) return NoData();
    if (string.IsNullOrWhiteSpace(req.Label)) return Results.BadRequest(new { error = "Describe what you did." });
    var a = d.Analyze(fresh: true);
    var issue = a.Issues.FirstOrDefault(i => i.Id == req.IssueId);
    if (issue is null) return Results.NotFound(new { error = $"No current issue '{req.IssueId}'. See GET /issues for ids." });
    var first = a.Events.FirstOrDefault(e => issue.EventIds.Contains(e.Id));
    if (first is null) return Results.BadRequest(new { error = "That issue has no events to track." });
    var app = first.AppName is null ? null : RuleValidator.NormalizeProcessName(first.AppName).ToLowerInvariant();
    var fix = new FixAttempt(0, issue.Id, first.Kind, app, req.Label.Trim(), DateTime.UtcNow);
    var r = await service.SendAsync(PipeTypes.FixApplied, fix, ResManJson.Default.FixAttempt);
    return r.Ok
        ? Results.Ok(new { id = r.Payload?.GetInt64(), tracking = $"Hindsight will compare how often {FixOutcomes.Noun(first.Kind)} happen before and after this." })
        : Results.Json(new { error = r.Error }, statusCode: 503);
});

api.MapGet("/report", (int? days) => host.Doctor is { } d
    ? Results.Content(HelperReport.Build(d, Math.Clamp(days ?? 30, 1, 60)), "text/html; charset=utf-8")
    : NoData());

api.MapPost("/scan", async () =>
{
    var r = await service.SendAsync(PipeTypes.ScanNow);
    return r.Ok ? Results.Ok(new { scanned = true }) : Results.Json(new { error = r.Error }, statusCode: 503);
});

api.MapPost("/agent/pause", (PauseRequest req) =>
{
    state.Paused = req.Paused;
    return Results.Ok(new { paused = state.Paused });
});

api.Map("/live", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await state.HoldAsync(ws, ctx.RequestAborted);
});

await app.StartAsync();
var url = app.Urls.First();
WriteAgentInfo(url, token);
app.Logger.LogInformation("Hindsight agent listening on {Url}. {Model}", url, model.Describe);

Tray.Start(host, state, () => _ = app.StopAsync());
await app.WaitForShutdownAsync();
try { File.Delete(ResManPaths.AgentInfoPath); } catch { }

// agent.json tells local clients where the API is and holds the bearer token. Only this user can read it.
static void WriteAgentInfo(string url, string token)
{
    Directory.CreateDirectory(ResManPaths.UserDir);
    var path = ResManPaths.AgentInfoPath;
    File.WriteAllText(path, JsonSerializer.Serialize(new { url, token, pid = Environment.ProcessId }));
    var security = new FileSecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
    new FileInfo(path).SetAccessControl(security);
}

record WhyRequest(string Symptom);
record SuggestRequest(string Request);
record PauseRequest(bool Paused);
record FixRequest(string IssueId, string Label);
record FollowUpRequest(string SessionId, string Question);
record PreloadRequest(int? Minutes);
