using ResMan.Brain.Analyzers;
using ResMan.Brain.Doctor;
using ResMan.Brain.Harness;
using ResMan.Brain.Knowledge;
using ResMan.Brain.Models;
using ResMan.Core;
using ResMan.Service;

namespace ResMan.Tests;

public class SignatureMatcherTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static HealthEvent E(long id, string kind, double hoursAgo, string? app = null, Dictionary<string, string>? data = null) =>
        new(id, Now.AddHours(-hoursAgo), "T", "T", 1, 2, kind, app, "", data ?? [], null);

    [Fact]
    public void Single_crash_matches_generic_signature()
    {
        var m = SignatureMatcher.Match(TestDb.Kb.Signatures, [E(1, HealthKind.AppCrash, 2, "chrome.exe")], Now);
        Assert.Contains(m, x => x.Signature.Id == "app-crash");
    }

    [Fact]
    public void Crash_loop_supersedes_single_crash_for_the_same_app()
    {
        var events = new[] { E(1, HealthKind.AppCrash, 1, "chrome.exe"), E(2, HealthKind.AppCrash, 2, "chrome.exe"), E(3, HealthKind.AppCrash, 3, "chrome.exe") };
        var m = SignatureMatcher.Match(TestDb.Kb.Signatures, events, Now);
        Assert.Contains(m, x => x.Signature.Id == "app-crash-loop" && x.App == "chrome.exe");
        Assert.DoesNotContain(m, x => x.Signature.Id == "app-crash");
    }

    [Fact]
    public void Power_loss_requires_zero_bugcheck_code()
    {
        var lost = SignatureMatcher.Match(TestDb.Kb.Signatures, [E(1, HealthKind.UnexpectedShutdown, 1, data: new() { ["BugcheckCode"] = "0" })], Now);
        var crashed = SignatureMatcher.Match(TestDb.Kb.Signatures, [E(1, HealthKind.UnexpectedShutdown, 1, data: new() { ["BugcheckCode"] = "209" })], Now);
        Assert.Contains(lost, x => x.Signature.Id == "power-loss");
        Assert.DoesNotContain(crashed, x => x.Signature.Id == "power-loss");
    }

    [Fact]
    public void Events_outside_the_window_do_not_match()
    {
        var m = SignatureMatcher.Match(TestDb.Kb.Signatures, [E(1, HealthKind.WifiDrop, 30), E(2, HealthKind.WifiDrop, 31), E(3, HealthKind.WifiDrop, 32),
            E(4, HealthKind.WifiDrop, 33), E(5, HealthKind.WifiDrop, 34)], Now);
        Assert.DoesNotContain(m, x => x.Signature.Id == "wifi-drops");
    }

    [Fact]
    public void Noise_is_flagged_and_kept_out_of_issues()
    {
        var m = SignatureMatcher.Match(TestDb.Kb.Signatures, [E(1, HealthKind.Noise, 1)], Now);
        Assert.Contains(m, x => x.Signature.Noise);
        Assert.Empty(Reports.Issues(m, [], TestDb.Kb));
    }
}

public class AnalyzerTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Freeze_causes_name_memory_pressure_and_a_disk_hog()
    {
        var snap = new Snapshot(1, Now, 40, 96, 100, false,
        [
            new ProcessSample(1, "chrome", 10, 3L << 30, 0, 0),
            new ProcessSample(2, "OneDrive", 5, 200L << 20, 80L << 20, 0),
        ]);
        var causes = FreezeCorrelationAnalyzer.Causes(snap, "chrome", TestDb.Kb.FriendlyName).ToList();
        Assert.Contains(causes, c => c.Contains("memory was nearly full (96%)"));
        Assert.Contains(causes, c => c.Contains("Microsoft OneDrive was busy with the disk"));
    }

    [Fact]
    public void Memory_pressure_never_blames_resmans_own_ai()
    {
        var snaps = Enumerable.Range(0, 40).Select(i => new Snapshot(i, Now.AddMinutes(-i * 10), 20, 95, 100, false,
        [
            new ProcessSample(1, "ResMan.Inference", 30, 4L << 30, 0, 0),
            new ProcessSample(2, "PhotoSync", 5, 2L << 30, 0, 0),
        ])).ToList();
        var f = Assert.Single(new MemoryPressureAnalyzer().Analyze(new AnalysisContext(Now, [], snaps, _ => null, TestDb.Kb.FriendlyName, [])));
        Assert.Contains("PhotoSync", f.Evidence);
        Assert.DoesNotContain("ResMan", f.Evidence);
    }

    [Fact]
    public void Change_correlation_blames_a_driver_installed_just_before_graphics_crashes()
    {
        var driver = new SystemChange(1, Now.AddDays(-4), ChangeKind.DriverInstalled, "NVIDIA driver nvlddmkm 32.0.15", null, "t", "k");
        var events = Enumerable.Range(0, 4)
            .Select(i => new HealthEvent(10 + i, Now.AddDays(-3.5 + i * 0.8), "T", "Display", 4101, 2, HealthKind.DisplayDriverReset, null, "", new Dictionary<string, string>(), null))
            .ToList();
        var ctx = new AnalysisContext(Now, events, [], _ => null, TestDb.Kb.FriendlyName, [driver]);
        var f = Assert.Single(new ChangeCorrelationAnalyzer().Analyze(ctx));
        Assert.Equal("change_correlation", f.Code);
        Assert.StartsWith("Graphics driver crashes started after", f.Title);
        Assert.Same(driver, f.RelatedChange);
    }

    [Fact]
    public void Change_correlation_ignores_routine_updates_and_problems_that_did_not_increase()
    {
        var routine = new SystemChange(1, Now.AddDays(-4), ChangeKind.WindowsUpdate, "Security Intelligence Update for Microsoft Defender", null, "t", "k");
        var events = Enumerable.Range(0, 3)
            .Select(i => new HealthEvent(10 + i, Now.AddDays(-3 + i), "T", "Display", 4101, 2, HealthKind.DisplayDriverReset, null, "", new Dictionary<string, string>(), null))
            .ToList();
        Assert.Empty(new ChangeCorrelationAnalyzer().Analyze(new AnalysisContext(Now, events, [], _ => null, TestDb.Kb.FriendlyName, [routine])));

        // Problem was already just as frequent before the driver change: no blame.
        var driver = new SystemChange(2, Now.AddDays(-3.5), ChangeKind.DriverInstalled, "Some driver", null, "t", "k2");
        var steady = Enumerable.Range(0, 14)
            .Select(i => new HealthEvent(100 + i, Now.AddDays(-13.5 + i), "T", "Display", 4101, 2, HealthKind.DisplayDriverReset, null, "", new Dictionary<string, string>(), null))
            .ToList();
        Assert.Empty(new ChangeCorrelationAnalyzer().Analyze(new AnalysisContext(Now, steady, [], _ => null, TestDb.Kb.FriendlyName, [driver])));
    }

    [Fact]
    public void Crash_loop_needs_three_crashes_in_a_day()
    {
        HealthEvent C(long id, double h) => new(id, Now.AddHours(-h), "T", "T", 1000, 2, HealthKind.AppCrash, "spotify.exe", "", new Dictionary<string, string>(), null);
        var ctx = new AnalysisContext(Now, [C(1, 1), C(2, 2), C(3, 30)], [], _ => null, TestDb.Kb.FriendlyName);
        Assert.Empty(new CrashLoopAnalyzer().Analyze(ctx));
        ctx = ctx with { Events = [C(1, 1), C(2, 2), C(3, 3)] };
        Assert.Single(new CrashLoopAnalyzer().Analyze(ctx));
    }
}

public class FixOutcomeTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Reports_fixed_when_problem_stops()
    {
        var applied = Now.AddDays(-10);
        var events = Enumerable.Range(1, 6)
            .Select(i => new HealthEvent(i, applied.AddDays(-i), "T", "T", 1, 2, HealthKind.AppHang, "chrome.exe", "", new Dictionary<string, string>(), null))
            .ToList();
        var o = FixOutcomes.Evaluate(new FixAttempt(1, "app-hang:chrome", HealthKind.AppHang, "chrome", "Turned on Memory Saver", applied), events, Now, Now.AddDays(-60));
        Assert.Equal("fixed", o.Verdict);
        Assert.Equal(0, o.AfterPerWeek);
        Assert.Contains("Freezes:", o.Summary);
    }

    [Fact]
    public void Too_early_to_tell_right_after_a_fix()
    {
        var o = FixOutcomes.Evaluate(new FixAttempt(1, "x", HealthKind.AppCrash, null, "Updated", Now.AddHours(-5)), [], Now, Now.AddDays(-30));
        Assert.Equal("too_early", o.Verdict);
    }
}

public class ReportTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Groups_installer_bursts_and_hides_routine_updates()
    {
        var changes = Enumerable.Range(0, 5)
            .Select(i => new SystemChange(i, Now.AddMinutes(-i), ChangeKind.AppInstalled, $"Microsoft .NET Part {i}", null, "t", $"k{i}"))
            .Append(new SystemChange(9, Now.AddHours(-1), ChangeKind.WindowsUpdate, "Security Intelligence Update for Microsoft Defender", null, "t", "k9"))
            .ToList();
        var g = Assert.Single(Reports.GroupChanges(changes));
        Assert.Equal(5, g.Count);
        Assert.Contains("and 4 related items were installed", g.Description);
    }

    [Fact]
    public void Timeline_collapses_bursts()
    {
        var events = Enumerable.Range(0, 5)
            .Select(i => new HealthEvent(i, Now.AddMinutes(-i * 10), "T", "T", 37, 2, HealthKind.ThermalThrottle, null, "", new Dictionary<string, string>(), null))
            .ToList();
        var entry = Assert.Single(Reports.Timeline(events, [], TestDb.Kb));
        Assert.Equal(5, entry.Count);
    }

    [Fact]
    public void Helper_report_is_html_and_escapes_untrusted_text()
    {
        using var t = new TestDb();
        t.AddEvent(HealthKind.AppCrash, DateTime.UtcNow.AddHours(-1), app: "<script>alert(1)</script>.exe");
        t.AddChange(ChangeKind.AppInstalled, "Evil <b>App</b>", DateTime.UtcNow.AddHours(-2));
        var html = HelperReport.Build(t.Doctor());
        Assert.StartsWith("<!doctype html>", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("What changed recently", html);
    }
}

public class DataTests
{
    [Fact]
    public void Health_events_are_deduplicated_by_record_id()
    {
        using var t = new TestDb();
        var e = new HealthEvent(0, DateTime.UtcNow, "System", "disk", 7, 2, HealthKind.DiskError, null, "x", new Dictionary<string, string>(), null, 42);
        Assert.NotNull(t.Health.Insert(e));
        Assert.Null(t.Health.Insert(e));
        Assert.Equal(1, t.Health.Count());
    }

    [Fact]
    public void Nearest_snapshot_respects_the_distance_limit()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        var id = t.AddSnapshot(now.AddSeconds(-5), 50, 10, new ProcessSample(1, "chrome", 5, 1 << 20, 0, 0));
        Assert.Equal(id, t.Telemetry.Nearest(now, TimeSpan.FromSeconds(30))!.Id);
        Assert.Null(t.Telemetry.Nearest(now.AddMinutes(5), TimeSpan.FromSeconds(30)));
        Assert.Equal("chrome", t.Telemetry.Get(id)!.Top.Single().Name);
    }

    [Fact]
    public void Inventory_baseline_and_change_dedupe()
    {
        using var t = new TestDb();
        Assert.False(t.Changes.HasBaseline("programs"));
        t.Changes.ReplaceInventory("programs", new Dictionary<string, string> { ["a|1"] = "A 1" }, DateTime.UtcNow);
        Assert.True(t.Changes.HasBaseline("programs"));
        Assert.Equal("A 1", t.Changes.Inventory("programs")["a|1"]);

        var c = new SystemChange(0, DateTime.UtcNow, ChangeKind.AppInstalled, "A", null, "t", "same-key");
        Assert.True(t.Changes.Insert(c));
        Assert.False(t.Changes.Insert(c));
    }

    [Fact]
    public void Retention_rolls_old_snapshots_into_hourly_summary()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        t.AddSnapshot(now.AddDays(-10), 50, 10);
        t.AddSnapshot(now.AddMinutes(-1), 60, 20);
        t.Telemetry.ApplyRetention(now, TimeSpan.FromDays(7));
        Assert.Equal(1, t.Telemetry.Count());
    }
}

public class DriveHealthTests
{
    private static DriveHealth D(string status, long? readErr = 0, int? wear = 5) =>
        new(DateTime.UtcNow, "0", "Test SSD", "SSD", status, wear, readErr, 0, 40, 100);

    [Fact]
    public void Flags_status_drop_rising_errors_and_heavy_wear_once()
    {
        Assert.Null(DriveHealthCollector.Assess(null, D("Healthy")));
        Assert.NotNull(DriveHealthCollector.Assess(D("Healthy"), D("Warning")));
        Assert.Null(DriveHealthCollector.Assess(D("Warning"), D("Warning")));
        Assert.Contains("Read errors went up", DriveHealthCollector.Assess(D("Healthy", 0), D("Healthy", 3)));
        Assert.Contains("worn out", DriveHealthCollector.Assess(D("Healthy", wear: 80), D("Healthy", wear: 91)));
        Assert.Null(DriveHealthCollector.Assess(D("Healthy", wear: 91), D("Healthy", wear: 92)));
    }
}

public class HarnessTests
{
    private const string Answer = """{"action":"answer","summary":"SketchPad froze while memory was full.","likely_cause":"PhotoSync used most of the memory.","confidence":"medium","evidence":["memory 96% at 3:12 PM","PhotoSync 7 GB"],"steps":["Close PhotoSync"],"needs_human":false}""";

    [Fact]
    public async Task Without_ai_says_nothing_is_on_record_instead_of_guessing()
    {
        using var t = new TestDb();
        t.AddEvent(HealthKind.AppCrash, DateTime.UtcNow.AddHours(-1), "chrome.exe");
        var a = await new DoctorHarness(t.Doctor(), new NullModelProvider()).WhyAsync("my pc randomly turned off");
        Assert.Contains("hasn't recorded any unexpected shutdowns", a.Summary);
    }

    [Fact]
    public async Task Model_must_investigate_with_a_tool_before_answering()
    {
        using var t = new TestDb();
        for (var i = 0; i < 3; i++) t.AddEvent(HealthKind.AppHang, DateTime.UtcNow.AddHours(-i - 1), "sketchpad.exe");
        var model = new ScriptedModel("""{"action":"tool","tool":"get_health_events","input":"48"}""",
            """{"action":"tool","tool":"inspect_binary","input":"PhotoSync"}""", Answer);
        var a = await new DoctorHarness(t.Doctor(), model).WhyAsync("my drawing app keeps freezing");

        Assert.Equal(Grammars.ToolOnly, model.Requests[0].Gbnf);          // step 1 can only be a tool call
        Assert.Equal(Grammars.ToolOnly, model.Requests[1].Gbnf);          // ...and so can step 2
        Assert.Equal(Grammars.ToolOrAnswer, model.Requests[2].Gbnf);
        Assert.Equal(["get_health_events(48)", "inspect_binary(PhotoSync)"], a.ToolTrace);
        Assert.Contains("[event", model.Requests[1].User);                // tool output fed back as evidence
        Assert.Contains("not in ResMan's knowledge base", model.Requests[2].User);
        Assert.Equal(["memory 96% at 3:12 PM", "PhotoSync 7 GB"], a.Evidence);
        Assert.Equal(3, a.ModelCalls);
        Assert.NotNull(a.SessionId);
        Assert.DoesNotContain("app-hang", model.Requests[0].User);        // overview is neutral: no signature verdicts
    }

    [Fact]
    public async Task Investigation_steps_stream_in_order_with_plain_labels()
    {
        using var t = new TestDb();
        for (var i = 0; i < 2; i++) t.AddEvent(HealthKind.AppHang, DateTime.UtcNow.AddHours(-i - 1), "sketchpad.exe");
        var model = new ScriptedModel("""{"action":"tool","tool":"get_health_events","input":"24"}""",
            """{"action":"tool","tool":"get_health_events","input":"24"}""",
            """{"action":"tool","tool":"get_resource_history","input":"60"}""", Answer);
        var steps = new List<InvestigationStep>();
        var a = await new DoctorHarness(t.Doctor(), model).WhyAsync("sketchpad froze", onStep: steps.Add);

        Assert.Equal(["thinking", "tool", "thinking", "repeat", "thinking", "tool", "thinking", "answer"], steps.Select(s => s.Kind));
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], steps.Select(s => s.Step));
        Assert.Equal("Event records, last 24 hours", steps[1].Label);
        Assert.Contains("2 problem event", steps[1].Finding);
        Assert.Equal("Memory and CPU history, last 60 min", steps[5].Label);
        Assert.Equal(a.LikelyCause, steps[^1].Finding);
        Assert.True(steps.Zip(steps.Skip(1)).All(p => p.First.ElapsedMs <= p.Second.ElapsedMs));
    }

    [Fact]
    public void Resource_history_leads_with_who_grew_even_over_long_windows()
    {
        // Regression: over 300 min the output was truncated before its summary, and a steady
        // multi-process app (a small helper plus the main process) looked like it had grown.
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        static long Mb(long mb) => mb << 20;
        for (var i = 0; i < 300; i++)
        {
            var top = new List<ProcessSample> { new(1, "claude", 1, Mb(150), 0, 0), new(2, "claude", 1, Mb(450), 0, 0) };
            if (i >= 280) top.Add(new(3, "PhotoSync", 2, Mb(500 + (i - 280) * 100), 0, 0));
            t.AddSnapshot(now.AddMinutes(-300 + i), i >= 280 ? 92 : 60, 10, [.. top]);
        }
        var output = new HarnessTools(t.Doctor()).Run("get_resource_history", "300");
        var lines = output.Split('\n');
        var grew = lines.First(l => l.StartsWith("Grew or appeared", StringComparison.Ordinal));
        Assert.Contains("PhotoSync", grew);
        Assert.DoesNotContain("claude", grew, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", lines.First(l => l.StartsWith("Large but steady", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PhotoSync", HarnessTools.Gist("get_resource_history", output));
    }

    [Fact]
    public void A_crash_logged_twice_by_windows_counts_once_and_keeps_the_exception()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        var first = t.AddEvent(HealthKind.AppCrash, now, "SketchPad.exe", provider: "Application Error", eventId: 1000);
        Assert.Equal(first, t.Health.FindSameCrash("sketchpad.exe", ".NET Runtime", now.AddSeconds(1), TimeSpan.FromSeconds(3))!.Id);
        Assert.Null(t.Health.FindSameCrash("SketchPad.exe", "Application Error", now.AddSeconds(1), TimeSpan.FromSeconds(3))); // same provider: a new crash
        Assert.Null(t.Health.FindSameCrash("SketchPad.exe", ".NET Runtime", now.AddSeconds(6), TimeSpan.FromSeconds(3)));      // seconds later: a new crash
        t.Health.MergeData(first, new Dictionary<string, string> { ["Exception"] = "System.OutOfMemoryException" });
        Assert.Equal("System.OutOfMemoryException", t.Health.Get(first)!.Data["Exception"]);

        // Doubles stored before de-duplication existed are cleaned up once.
        t.AddEvent(HealthKind.AppCrash, now.AddMinutes(-5), "SketchPad.exe", provider: ".NET Runtime", eventId: 1026);
        t.AddEvent(HealthKind.AppCrash, now.AddMinutes(-5).AddSeconds(1), "SketchPad.exe", provider: "Application Error", eventId: 1000);
        Assert.Equal(1, t.Health.RemoveDuplicateCrashes());
        Assert.Equal(0, t.Health.RemoveDuplicateCrashes());
    }

    [Fact]
    public async Task Inspecting_a_hardware_fault_always_means_a_person_should_look()
    {
        using var t = new TestDb();
        var drive = t.AddEvent(HealthKind.DriveHealthWarning, DateTime.UtcNow.AddHours(-2));
        var model = new ScriptedModel($$"""{"action":"tool","tool":"get_event_details","input":"{{drive}}"}""",
            """{"action":"tool","tool":"get_findings","input":""}""", Answer); // Answer says needs_human: false
        var a = await new DoctorHarness(t.Doctor(), model).WhyAsync("files open slowly");
        Assert.True(a.NeedsHuman);
    }

    [Fact]
    public async Task A_failing_step_listener_does_not_break_the_investigation()
    {
        using var t = new TestDb();
        var model = new ScriptedModel("""{"action":"tool","tool":"get_findings","input":""}""",
            """{"action":"tool","tool":"get_health_events","input":"24"}""", Answer);
        var a = await new DoctorHarness(t.Doctor(), model).WhyAsync("it's slow", onStep: _ => throw new InvalidOperationException());
        Assert.True(a.UsedModel);
        Assert.Equal("PhotoSync used most of the memory.", a.LikelyCause);
    }

    [Fact]
    public async Task Repeated_tool_calls_are_not_rerun_and_the_last_step_forces_an_answer()
    {
        using var t = new TestDb();
        var model = new ScriptedModel("""{"action":"tool","tool":"get_findings","input":""}""");
        var replies = Enumerable.Repeat("""{"action":"tool","tool":"get_findings","input":""}""", 4).Append(Answer).ToArray();
        model = new ScriptedModel(replies);
        var a = await new DoctorHarness(t.Doctor(), model).WhyAsync("it's slow");
        Assert.Single(a.ToolTrace);
        Assert.Equal(Grammars.AnswerOnly, model.Requests[^1].Gbnf);
        Assert.True(a.UsedModel);
    }

    [Fact]
    public async Task Follow_up_reuses_the_session_evidence()
    {
        using var t = new TestDb();
        t.AddEvent(HealthKind.AppHang, DateTime.UtcNow.AddHours(-1), "sketchpad.exe");
        var model = new ScriptedModel("""{"action":"tool","tool":"get_health_events","input":"24"}""", Answer,
            """{"action":"answer","summary":"PhotoSync isn't signed and isn't needed by Windows, so it's safe to remove.","likely_cause":"-","confidence":"medium","evidence":[],"steps":[],"needs_human":false}""");
        var h = new DoctorHarness(t.Doctor(), model);
        var first = await h.WhyAsync("sketchpad froze");
        var follow = await h.FollowUpAsync(first.SessionId!, "is PhotoSync safe to remove?");
        Assert.NotNull(follow);
        Assert.Contains("safe to remove", follow!.Summary);
        Assert.Contains("EARLIER EVIDENCE", model.Requests[^1].User);
        Assert.Contains("[get_health_events", model.Requests[^1].User);
        Assert.Null(await h.FollowUpAsync("nope", "?"));
    }

    [Fact]
    public async Task Rule_proposals_targeting_protected_processes_are_rejected()
    {
        using var t = new TestDb();
        var model = new ScriptedModel("""{"process":"lsass.exe","action":"set_priority","value":"idle","profile":"gaming","reason":"x"}""");
        var s = await new DoctorHarness(t.Doctor(), model).ProposeRuleAsync("make lsass quiet");
        Assert.False(s.Valid);
        Assert.Contains(s.Errors, e => e.Contains("protected"));
    }

    [Fact]
    public async Task Without_ai_the_doctor_still_answers_from_signatures()
    {
        using var t = new TestDb();
        t.AddEvent(HealthKind.BlueScreen, DateTime.UtcNow.AddHours(-2));
        var a = await new DoctorHarness(t.Doctor(), new NullModelProvider()).WhyAsync("I got a blue screen");
        Assert.False(a.UsedModel);
        Assert.Contains("blue screen", a.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tools_return_evidence_for_unknown_binaries_and_network()
    {
        using var t = new TestDb();
        var tools = new HarnessTools(t.Doctor());
        Assert.Contains("not in ResMan's knowledge base", tools.Run("inspect_binary", "TotallyUnknownThing"));
        Assert.Contains("No network samples", tools.Run("get_network_history", "2"));
        Assert.Contains("There is no event", tools.Run("get_event_details", "999"));
    }

    [Fact]
    public void Grammars_restrict_tools_and_actions()
    {
        Assert.Contains("inspect_binary", Grammars.ToolOnly);
        Assert.DoesNotContain("answer ::=", Grammars.ToolOnly);
        Assert.DoesNotContain("run_command", Grammars.ToolOrAnswer);
        Assert.Contains("evidence", Grammars.AnswerOnly);
        Assert.Contains("memory_cap_mb", Grammars.Rule);
    }
}
