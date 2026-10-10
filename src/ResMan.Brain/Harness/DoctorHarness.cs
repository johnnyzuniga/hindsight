using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ResMan.Brain.Doctor;
using ResMan.Brain.Knowledge;
using ResMan.Brain.Models;
using ResMan.Core;

namespace ResMan.Brain.Harness;

public sealed record DoctorAnswer(
    string Summary,
    string LikelyCause,
    string Confidence,
    IReadOnlyList<string> Steps,
    bool NeedsHuman,
    IReadOnlyList<Fix> Actions,
    IReadOnlyList<string> ToolTrace,
    bool UsedModel,
    double ModelMilliseconds,
    string? Note = null)
{
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? SessionId { get; init; }
    public int ModelCalls { get; init; }
}

public sealed record RuleSuggestion(
    RuleProposal? Rule,
    bool Valid,
    IReadOnlyList<string> Errors,
    string? Clarify,
    bool UsedModel,
    double ModelMilliseconds);

/// <summary>
/// One live step of an investigation, for UIs that show the agent working.
/// Kind is "thinking" (a model call started), "tool" (evidence gathered), "repeat" (a duplicate call skipped) or "answer".
/// </summary>
public sealed record InvestigationStep(int Step, string Kind, string? Tool, string? Input, string? Label, string? Finding, double Ms, double ElapsedMs);

public sealed record KeyMoment(string T, string What);

public sealed record ReplayNarration(string Narration, IReadOnlyList<KeyMoment> KeyMoments, string LikelyCause, bool UsedModel, double ModelMilliseconds, string? Note = null);

/// <summary>
/// The agent loop around the local model. The model investigates: it starts from a neutral overview,
/// must call at least one evidence tool, can call up to four more, and then answers with cited evidence.
/// Every reply is grammar-constrained. Analyzers and signatures are tools the model consults, not the answer.
/// </summary>
public sealed class DoctorHarness(DoctorService doctor, IModelProvider model)
{
    public const int MaxSteps = 5;
    private static readonly string[] HardwareKinds = [HealthKind.DriveHealthWarning, HealthKind.HardwareError, HealthKind.DiskError];
    private const int MaxEvidenceChars = 6000;
    private KnowledgeBase Kb => doctor.Knowledge;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    private sealed record Session(string Symptom, string Evidence, DoctorAnswer Answer, DateTime CreatedUtc, List<(string Q, string A)> Turns);

    public async Task<DoctorAnswer> WhyAsync(string symptom, CancellationToken ct = default, Action<InvestigationStep>? onStep = null)
    {
        var a = doctor.Analyze();
        var related = RelatedIssues(a, SymptomKinds(symptom), symptom);
        var actions = related.SelectMany(i => i.Fixes).DistinctBy(f => f.Label).Take(4).ToList();
        if (!model.Enabled) return NoModelAnswer(a, symptom, related, actions);

        var (answer, evidence) = await InvestigateAsync(Prompts.Doctor, $"COMPLAINT: \"{symptom}\"", Overview(a), actions, ct, onStep: onStep);
        return Remember(symptom, evidence, answer);
    }

    /// <summary>Follow-up questions ("is it safe to remove?") reuse the evidence already gathered.</summary>
    public async Task<DoctorAnswer?> FollowUpAsync(string sessionId, string question, CancellationToken ct = default, Action<InvestigationStep>? onStep = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        if (!model.Enabled) return s.Answer with { Summary = "AI is off, so follow-up questions aren't available.", Note = model.Describe };

        var history = new StringBuilder()
            .AppendLine($"ORIGINAL COMPLAINT: \"{s.Symptom}\"")
            .AppendLine($"YOUR DIAGNOSIS: {s.Answer.Summary} Likely cause: {s.Answer.LikelyCause}");
        foreach (var (q, ans) in s.Turns.TakeLast(3)) history.AppendLine($"EARLIER QUESTION: {q}\nYOUR ANSWER: {ans}");
        var (answer, evidence) = await InvestigateAsync(Prompts.Doctor + "\n" + Prompts.FollowUp,
            $"{history}FOLLOW-UP QUESTION: \"{question}\"", "EARLIER EVIDENCE:\n" + s.Evidence, s.Answer.Actions.ToList(), ct, requireTool: false, maxSteps: 3, onStep: onStep);
        s.Turns.Add((question, answer.Summary));
        _sessions[sessionId] = s with { Evidence = Cap(evidence) };
        return answer with { SessionId = sessionId };
    }

    private async Task<(DoctorAnswer Answer, string Evidence)> InvestigateAsync(
        string system, string task, string context, List<Fix> actions, CancellationToken ct, bool requireTool = true, int maxSteps = MaxSteps,
        Action<InvestigationStep>? onStep = null)
    {
        var tools = new HarnessTools(doctor);
        var results = new StringBuilder();
        var trace = new List<string>();
        double totalMs = 0;
        var calls = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Emit(InvestigationStep s)
        {
            // A UI listener must never break the investigation.
            try { onStep?.Invoke(s); } catch { }
        }
        for (var step = 0; step < maxSteps; step++)
        {
            Emit(new InvestigationStep(step + 1, "thinking", null, null, null, null, 0, clock.Elapsed.TotalMilliseconds));
            // The first two steps must gather evidence: one look is rarely enough to connect signals.
            var first = step < 2 && requireTool;
            var last = step == maxSteps - 1;
            // Static parts first, growing evidence last: the sidecar reuses the cached prefix between steps.
            var user = new StringBuilder()
                .AppendLine(task).AppendLine()
                .AppendLine(context).AppendLine()
                .AppendLine(Prompts.ToolGuide)
                .AppendLine(results.Length > 0 ? $"EVIDENCE GATHERED SO FAR:\n{Cap(results.ToString())}" : "No evidence gathered yet.")
                .AppendLine(last ? Prompts.AnswerNow : first ? Prompts.Investigate : Prompts.ContinueOrAnswer);

            var r = await model.CompleteAsync(new InferenceRequest(system, user.ToString(),
                last ? Grammars.AnswerOnly : first ? Grammars.ToolOnly : Grammars.ToolOrAnswer, 450, 0f), ct);
            totalMs += r.Milliseconds;
            calls++;
            if (!r.Ok)
                return (new DoctorAnswer("The local AI couldn't finish the investigation.", "Not sure.", "low", [], false, actions, trace, false, totalMs, r.Error)
                { ModelCalls = calls }, results.ToString());

            using var doc = JsonDocument.Parse(r.Text!);
            var root = doc.RootElement;
            if (root.GetProperty("action").GetString() == "tool")
            {
                var name = root.GetProperty("tool").GetString()!;
                var input = root.GetProperty("input").GetString() ?? "";
                if (trace.Contains($"{name}({input})"))
                {
                    results.AppendLine($"[{name}(\"{input}\")] (already called; use the result above, or try a different tool such as get_event_details for another event)");
                    Emit(new InvestigationStep(step + 1, "repeat", name, input, HarnessTools.Label(name, input), "Already checked; looking elsewhere.", r.Milliseconds, clock.Elapsed.TotalMilliseconds));
                    continue;
                }
                trace.Add($"{name}({input})");
                var toolClock = System.Diagnostics.Stopwatch.StartNew();
                var output = tools.Run(name, input);
                results.AppendLine($"[{name}(\"{input}\")]").AppendLine(output).AppendLine();
                Emit(new InvestigationStep(step + 1, "tool", name, input, HarnessTools.Label(name, input), HarnessTools.Gist(name, output),
                    r.Milliseconds + toolClock.Elapsed.TotalMilliseconds, clock.Elapsed.TotalMilliseconds));
                continue;
            }
            var answer = ParseAnswer(root, actions, trace, totalMs) with { ModelCalls = calls };
            // Code, not the model, decides this one: a failing drive or a hardware error it looked at needs a person.
            if (tools.InspectedKinds.Overlaps(HardwareKinds)) answer = answer with { NeedsHuman = true };
            Emit(new InvestigationStep(step + 1, "answer", null, null, null, answer.LikelyCause, r.Milliseconds, clock.Elapsed.TotalMilliseconds));
            return (answer, results.ToString());
        }
        return (new DoctorAnswer("The local AI didn't reach an answer.", "Not sure.", "low", [], false, actions, trace, true, totalMs)
        { ModelCalls = calls }, results.ToString());
    }

    private DoctorAnswer Remember(string symptom, string evidence, DoctorAnswer answer)
    {
        foreach (var old in _sessions.Where(kv => DateTime.UtcNow - kv.Value.CreatedUtc > TimeSpan.FromHours(2)).Select(kv => kv.Key).ToList())
            _sessions.TryRemove(old, out _);
        var id = Guid.NewGuid().ToString("N")[..12];
        _sessions[id] = new Session(symptom, Cap(evidence), answer, DateTime.UtcNow, []);
        return answer with { SessionId = id };
    }

    public async Task<DoctorAnswer?> ExplainEventAsync(long eventId, CancellationToken ct = default, Action<InvestigationStep>? onStep = null)
    {
        var a = doctor.Analyze();
        var e = a.Events.FirstOrDefault(x => x.Id == eventId) ?? doctor.Health.Get(eventId);
        if (e is null) return null;
        var match = SignatureMatcher.ForEvent(a.Matches, e.Id);
        var issue = a.Issues.FirstOrDefault(i => i.EventIds.Contains(e.Id));
        var actions = (issue?.Fixes ?? match?.Signature.Fixes ?? []).ToList();
        var (title, detail) = Reports.Describe(e, Kb);

        if (!model.Enabled)
            return new DoctorAnswer($"{title}. {detail ?? match?.Signature.Explanation ?? ""}".Trim(),
                match?.Signature.LikelyCauses.FirstOrDefault() ?? "Not sure.", "low",
                actions.Select(f => f.Label).Take(4).ToList(), match?.Signature.NeedsHuman ?? false, actions, [], false, 0, model.Describe);

        // Start the investigation from this event's full details; the model can pull more evidence.
        var details = new HarnessTools(doctor).Run("get_event_details", e.Id.ToString());
        var (answer, evidence) = await InvestigateAsync(Prompts.Doctor,
            $"COMPLAINT: \"What happened here, and why?\" about: {title} ({HarnessTools.When(e.TimeUtc)})",
            $"EVENT DETAILS:\n{details}", actions, ct, requireTool: false, maxSteps: 4, onStep: onStep);
        return Remember($"What happened: {title}", details + "\n" + evidence, answer);
    }

    /// <summary>Narrates the black-box minute before an incident.</summary>
    public async Task<ReplayNarration?> NarrateReplayAsync(long eventId, CancellationToken ct = default)
    {
        var e = doctor.Health.Get(eventId);
        if (e is null) return null;
        var samples = doctor.Replays.Get(eventId, e.TimeUtc);
        if (samples.Count < 2) return null;
        var (title, _) = Reports.Describe(e, Kb);
        var summary = new HarnessTools(doctor).SummarizeReplay(samples, e.TimeUtc);
        if (!model.Enabled) return new ReplayNarration($"{title}. In the minute before: {summary}", [], "Not sure.", false, 0, model.Describe);

        var table = new StringBuilder($"INCIDENT at 0s: {title}\nRECORDING (t, CPU %, memory %, disk MB/s, busiest programs):\n");
        var step = Math.Max(1, samples.Count / 30);
        for (var i = 0; i < samples.Count; i += step)
        {
            var s = samples[i];
            table.AppendLine($"{(s.TimeUtc - e.TimeUtc).TotalSeconds:+0;-0;0}s, {s.CpuPercent:0}, {s.MemoryPercent:0}, {s.DiskBytesPerSec >> 20}, " +
                string.Join("; ", s.Top.Where(p => !RuleValidator.IsOwnProcess(p.Name)).Take(3).Select(p => $"{Kb.FriendlyName(p.Name)} {p.CpuPercent:0}%/{p.WorkingSetMb}MB")));
        }
        table.AppendLine($"SUMMARY: {summary}");
        var r = await model.CompleteAsync(new InferenceRequest(Prompts.Narrate, table.ToString(), Grammars.Narration, 400, 0f), ct);
        if (!r.Ok) return new ReplayNarration($"{title}. In the minute before: {summary}", [], "Not sure.", false, r.Milliseconds, r.Error);
        using var doc = JsonDocument.Parse(r.Text!);
        var root = doc.RootElement;
        return new ReplayNarration(root.GetProperty("narration").GetString() ?? "",
            root.GetProperty("key_moments").EnumerateArray().Select(m => new KeyMoment(m.GetProperty("t").GetString() ?? "", m.GetProperty("what").GetString() ?? "")).ToList(),
            root.GetProperty("likely_cause").GetString() ?? "", true, r.Milliseconds);
    }

    public async Task<RuleSuggestion> ProposeRuleAsync(string request, CancellationToken ct = default)
    {
        if (!model.Enabled)
            return new(null, false, [], "AI is off, so plain-English rules aren't available. You can still add rules directly.", false, 0);

        var a = doctor.Analyze();
        var running = a.Latest?.Top.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(20) ?? [];
        var known = Kb.Processes.Where(p => p.SafeToLimit).Select(p => $"{p.Name} ({p.Title})").Take(40);
        var user = $"""
            REQUEST: "{request}"
            RUNNING PROCESSES: {string.Join(", ", running)}
            KNOWN PROGRAMS: {string.Join(", ", known)}
            """;
        var r = await model.CompleteAsync(new InferenceRequest(Prompts.Rule, user, Grammars.Rule, 200, 0.1f), ct);
        if (!r.Ok) return new(null, false, [r.Error!], null, true, r.Milliseconds);

        using var doc = JsonDocument.Parse(r.Text!);
        if (doc.RootElement.TryGetProperty("clarify", out var c))
            return new(null, false, [], c.GetString(), true, r.Milliseconds);

        var rule = doc.RootElement.Deserialize(ResManJson.Default.RuleProposal)!;
        rule = rule with { Process = RuleValidator.NormalizeProcessName(rule.Process).ToLowerInvariant() };
        var v = RuleValidator.Validate(rule);
        return new(rule, v.Ok, v.Errors, null, true, r.Milliseconds);
    }

    // ---- context ----

    /// <summary>
    /// A neutral starting point: what kinds of data exist and when, not what they mean.
    /// The model has to pull the actual evidence with tools.
    /// </summary>
    public string Overview(Analysis a)
    {
        var week = a.Events.Where(e => e.TimeUtc >= a.NowUtc.AddDays(-7) && e.Kind is not (HealthKind.Noise or HealthKind.WifiConnect)).ToList();
        var counts = week.GroupBy(e => e.Kind).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {FixOutcomes.Noun(g.Key)}{(g.Key == HealthKind.NetworkChange ? " (network changes)" : "")}");
        var sb = new StringBuilder("WHAT RESMAN HAS RECORDED (last 7 days):\n");
        sb.AppendLine(week.Count == 0 ? "- No problem events, so there are no event ids to look up (resource, change and network history still exist)."
            : $"- {string.Join(", ", counts)}; most recent {HarnessTools.When(week.Max(e => e.TimeUtc))}.");
        // Raw facts with real ids, so the model can open the right event instead of guessing ids.
        foreach (var t in Reports.Timeline(week, [], Kb).Take(6))
            sb.AppendLine($"  [event {t.EventIds[0]}] {HarnessTools.When(t.TimeUtc)}: {t.Title}{(t.Count > 1 ? $" (x{t.Count})" : "")}");
        sb.AppendLine($"- {a.Events.Count(e => e.Kind == HealthKind.Noise && e.TimeUtc >= a.NowUtc.AddDays(-7))} harmless background warnings (ignored).");
        sb.AppendLine($"- {a.Changes.Count(c => !ChangeKind.IsRoutine(c) && c.TimeUtc >= a.NowUtc.AddDays(-7))} installs/updates/driver changes.");
        if (a.Latest is { } s) sb.AppendLine($"- Resource samples every 10 s; latest {HarnessTools.When(s.TimeUtc)}: memory {s.MemoryUsedPercent:0}%, CPU {s.TotalCpuPercent:0}%.");
        sb.AppendLine($"- Time now: {HarnessTools.When(a.NowUtc)}.");
        return sb.ToString().TrimEnd();
    }

    private static string Cap(string s) => s.Length <= MaxEvidenceChars ? s : s[..(MaxEvidenceChars / 3)] + "\n...\n" + s[^(MaxEvidenceChars * 2 / 3)..];

    private static readonly (string[] Words, string Kind)[] SymptomMap =
    [
        (["froze", "freeze", "freezing", "frozen", "hang", "hangs", "not responding", "stuck", "unresponsive", "lag", "laggy"], HealthKind.AppHang),
        (["crash", "crashes", "crashed", "closes", "closed", "quit"], HealthKind.AppCrash),
        (["blue screen", "bsod", "blue"], HealthKind.BlueScreen),
        (["turned off", "turns off", "shut down", "shuts down", "shutting down", "restart", "restarted", "rebooted", "power"], HealthKind.UnexpectedShutdown),
        (["hot", "heat", "fan", "overheat", "overheating", "warm"], HealthKind.ThermalThrottle),
        (["startup", "boot", "start up", "takes forever to start"], HealthKind.SlowBoot),
        (["wifi", "wi-fi", "internet", "disconnect", "disconnects", "network", "online"], HealthKind.WifiDrop),
        (["wifi", "wi-fi", "internet", "disconnect", "disconnects", "network", "online"], HealthKind.NetworkChange),
        (["black screen", "flicker", "flickers", "screen went", "display", "screen"], HealthKind.DisplayDriverReset),
        (["memory", "ram", "out of memory"], HealthKind.LowMemory),
        (["disk", "drive", "ssd", "hard drive"], HealthKind.DiskError),
        (["disk", "drive", "ssd", "hard drive", "files"], HealthKind.DriveHealthWarning),
    ];

    public static HashSet<string> SymptomKinds(string symptom)
    {
        var s = symptom.ToLowerInvariant();
        return SymptomMap.Where(m => m.Words.Any(w => s.Contains(w))).Select(m => m.Kind).ToHashSet();
    }

    /// <summary>Issues related to the symptom; used only to attach one-click fix shortcuts to the answer.</summary>
    private List<Issue> RelatedIssues(Analysis a, HashSet<string> kinds, string symptom)
    {
        var byKind = a.Issues.Where(i => i.EventIds.Any(id => a.Events.FirstOrDefault(e => e.Id == id)?.Kind is { } k && kinds.Contains(k))).ToList();
        if (byKind.Count > 0 || kinds.Count > 0) return byKind.Take(3).ToList();
        var hitIds = Kb.Search(symptom, 5).Where(h => h.Type == "signature").Select(h => h.Id).ToHashSet();
        return a.Issues.Where(i => hitIds.Any(h => i.Id.StartsWith(h, StringComparison.Ordinal))).Take(3).ToList();
    }

    private static DoctorAnswer ParseAnswer(JsonElement root, List<Fix> actions, List<string> trace, double ms) => new(
        root.GetProperty("summary").GetString() ?? "",
        root.GetProperty("likely_cause").GetString() ?? "",
        root.GetProperty("confidence").GetString() ?? "low",
        root.GetProperty("steps").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList(),
        root.GetProperty("needs_human").GetBoolean(),
        actions, trace, true, ms)
    {
        Evidence = root.GetProperty("evidence").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList(),
    };

    /// <summary>No-AI tier: answers from the deterministic signatures and analyzers.</summary>
    private static DoctorAnswer NoModelAnswer(Analysis a, string symptom, List<Issue> related, List<Fix> actions)
    {
        var kinds = SymptomKinds(symptom);
        if (related.Count == 0 && kinds.Count > 0 && !a.Events.Any(e => kinds.Contains(e.Kind)))
            return new($"ResMan hasn't recorded any {string.Join(" or ", kinds.Select(FixOutcomes.Noun).Distinct())} on this PC yet.",
                "Not sure: there's nothing on record to go on.", "low",
                ["If it happens again, ResMan will capture what your PC was doing at that moment, so check back afterwards."],
                false, [], [], false, 0, "AI is off; answered from ResMan's records.");
        if (related.Count == 0)
            return new("ResMan's built-in checks don't explain this. The local AI can investigate the raw evidence when it's turned on.",
                "Not sure.", "low", [], false, actions, [], false, 0, "AI is off.");
        var top = related[0];
        return new($"{top.Title}. {top.Explanation}", top.Evidence ?? "See the steps below.", top.Evidence is null ? "low" : "medium",
            top.Fixes.Select(f => f.Label).Take(4).ToList(), top.NeedsHuman, actions, [], false, 0, "AI is off; answered from built-in checks.");
    }
}
