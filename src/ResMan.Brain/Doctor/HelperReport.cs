using System.Text;
using ResMan.Core;
using static System.Net.WebUtility;

namespace ResMan.Brain.Doctor;

/// <summary>
/// A self-contained, print-ready HTML summary for whoever fixes the PC (a relative, a friend, a repair shop).
/// Plain language first, technical details at the end. "Print > Save as PDF" in any browser gives a PDF.
/// </summary>
public static class HelperReport
{
    public static string Build(DoctorService doctor, int days = 30)
    {
        var a = doctor.Analyze(fresh: true);
        var kb = doctor.Knowledge;
        var from = a.NowUtc.AddDays(-days);
        var timeline = Reports.Timeline(a.Events.Where(e => e.TimeUtc >= from).ToList(), a.Matches, kb);
        var changes = Reports.GroupChanges(a.Changes.Where(c => c.TimeUtc >= from));
        var technical = a.Events.Where(e => e.Kind != HealthKind.Noise && e.TimeUtc >= from).Take(60).ToList();

        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>PC health report: {{H(Environment.MachineName)}}</title>
            <style>
              :root { --ink:#1d1d1f; --muted:#5f6368; --line:#dadce0; --warn:#a15c00; --crit:#b3261e; --ok:#1e7a34; --bg:#ffffff; --card:#f6f7f9; }
              * { box-sizing: border-box; }
              body { font: 15px/1.55 "Segoe UI", system-ui, sans-serif; color: var(--ink); background: var(--bg); margin: 0 auto; max-width: 860px; padding: 32px 20px 64px; }
              h1 { font-size: 24px; margin: 0 0 4px; } h2 { font-size: 18px; margin: 32px 0 10px; border-bottom: 1px solid var(--line); padding-bottom: 6px; }
              .muted { color: var(--muted); } .small { font-size: 13px; }
              .status { padding: 14px 16px; border-radius: 10px; background: var(--card); border-left: 5px solid var(--ok); margin-top: 16px; }
              .status.attention { border-left-color: var(--warn); } .status.problem { border-left-color: var(--crit); }
              .issue { border: 1px solid var(--line); border-radius: 10px; padding: 12px 16px; margin: 10px 0; break-inside: avoid; }
              .sev { font-size: 12px; font-weight: 600; text-transform: uppercase; letter-spacing: .04em; }
              .sev.critical { color: var(--crit); } .sev.warning { color: var(--warn); } .sev.notice, .sev.info { color: var(--muted); }
              .human { color: var(--crit); font-weight: 600; }
              table { width: 100%; border-collapse: collapse; font-size: 13.5px; } th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
              th { color: var(--muted); font-weight: 600; } ul { margin: 6px 0; padding-left: 20px; }
              @media print { body { padding: 0; } h2 { break-after: avoid; } }
            </style></head><body>
            <h1>PC health report</h1>
            <div class="muted">{{H(Environment.MachineName)}} &middot; generated {{DateTime.Now:dddd, MMMM d, yyyy h:mm tt}} &middot; covers the last {{days}} days &middot; made by ResMan, everything stays on this PC</div>
            <div class="status {{a.Status.Level}}"><strong>{{H(a.Status.Headline)}}</strong><br>{{H(a.Status.Detail)}}
            {{(a.Status.HiddenWarnings > 0 ? $"<div class=\"small muted\">{a.Status.HiddenWarnings} harmless Windows warnings were left out of this report.</div>" : "")}}</div>
            """);

        sb.Append("<h2>Main problems and likely causes</h2>");
        if (a.Issues.Count == 0) sb.Append("<p>No problems were recorded.</p>");
        foreach (var i in a.Issues)
        {
            sb.Append($"""<div class="issue"><div class="sev {i.Severity}">{H(i.Severity)}</div><strong>{H(i.Title)}</strong><p>{H(i.Explanation)}</p>""");
            if (i.Evidence is not null) sb.Append($"<p><strong>What Hindsight saw:</strong> {H(i.Evidence)}</p>");
            if (i.Fixes.Count > 0)
                sb.Append("<strong>Suggested fixes</strong><ul>" + string.Concat(i.Fixes.Select(f => $"<li>{H(f.Label)}</li>")) + "</ul>");
            if (i.NeedsHuman) sb.Append("""<p class="human">This may be a hardware problem worth having checked.</p>""");
            sb.Append($"""<div class="small muted">Last seen {i.LastSeenUtc.ToLocalTime():ddd MMM d h:mm tt} &middot; {i.EventIds.Count} event(s)</div></div>""");
        }

        sb.Append("<h2>What changed recently</h2>");
        if (changes.Count == 0) sb.Append("<p>No installs, updates, drivers or startup changes were recorded.</p>");
        else sb.Append("<table><tr><th>When</th><th>Change</th></tr>" + string.Concat(changes.Take(40).Select(c =>
            $"<tr><td>{c.TimeUtc.ToLocalTime():ddd MMM d h:mm tt}</td><td>{H(c.Description)}" +
            (c.Count > 1 ? $"<div class=\"small muted\">{H(string.Join(", ", c.Items.Take(12).Select(i => i.Name)))}{(c.Count > 12 ? ", ..." : "")}</div>" : "") +
            "</td></tr>")) + "</table>");

        sb.Append("<h2>Fixes already tried</h2>");
        if (a.Fixes.Count == 0) sb.Append("<p>No fixes have been recorded yet.</p>");
        else sb.Append("<table><tr><th>When</th><th>What was done</th><th>Result</th></tr>" + string.Concat(a.Fixes.Select(f =>
            $"<tr><td>{f.Fix.AppliedUtc.ToLocalTime():ddd MMM d}</td><td>{H(f.Fix.Label)}</td><td>{H(f.Summary)}</td></tr>")) + "</table>");

        sb.Append("<h2>Drive health</h2>");
        if (a.Drives.Count == 0) sb.Append("<p>No drive health readings yet.</p>");
        else sb.Append("<table><tr><th>Drive</th><th>Type</th><th>Status</th><th>Wear</th><th>Read / write errors</th><th>Temperature</th></tr>" +
            string.Concat(a.Drives.Select(d =>
                $"<tr><td>{H(d.Model)}</td><td>{H(d.MediaType)}</td><td>{H(d.HealthStatus)}</td><td>{(d.WearPercent is { } w ? $"{w}%" : "n/a")}</td>" +
                $"<td>{(d.ReadErrorsTotal is null ? "n/a" : $"{d.ReadErrorsTotal} / {d.WriteErrorsTotal}")}</td><td>{(d.TemperatureC is { } t ? $"{t} &deg;C" : "n/a")}</td></tr>")) + "</table>");

        sb.Append("<h2>Timeline</h2>");
        if (timeline.Count == 0) sb.Append("<p>Nothing happened.</p>");
        else sb.Append("<table><tr><th>When</th><th>What happened</th></tr>" + string.Concat(timeline.Take(80).Select(t =>
            $"<tr><td>{t.TimeUtc.ToLocalTime():ddd MMM d h:mm tt}</td><td>{H(t.Title)}{(t.Count > 1 ? $" (&times;{t.Count})" : "")}{(t.Detail is null ? "" : $"<div class=\"small muted\">{H(t.Detail)}</div>")}</td></tr>")) + "</table>");

        sb.Append("<h2>Technical details</h2><p class=\"small muted\">For the person fixing this PC: the underlying Windows events.</p>");
        if (technical.Count > 0)
            sb.Append("<table class=\"small\"><tr><th>Time</th><th>Log / source</th><th>ID</th><th>App</th><th>Windows message</th></tr>" + string.Concat(technical.Select(e =>
                $"<tr><td>{e.TimeUtc.ToLocalTime():MM-dd HH:mm}</td><td>{H(e.Log)} / {H(e.Provider)}</td><td>{e.EventId}</td><td>{H(e.AppName ?? "")}</td><td>{H(e.Summary)}</td></tr>")) + "</table>");

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string H(string? s) => HtmlEncode(s ?? "");
}
