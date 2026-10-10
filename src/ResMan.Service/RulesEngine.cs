using System.Diagnostics;
using ResMan.Core;
using ResMan.Data;

namespace ResMan.Service;

/// <summary>
/// Dry-run rules engine. Every 2 s it re-validates each enabled rule, finds matching processes for the
/// active profile, and records what it *would* do. Real enforcement is a later phase.
/// </summary>
public sealed class RulesEngine(RuleStore rules, ServiceState state, ILogger<RulesEngine> log) : BackgroundService
{
    private readonly HashSet<(long rule, int pid, string profile)> _reported = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        List<StoredRule> cached = [];
        var loadedAt = DateTime.MinValue;
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                if (DateTime.UtcNow - loadedAt > TimeSpan.FromSeconds(10)) { cached = rules.List(); loadedAt = DateTime.UtcNow; }
                Evaluate(cached, state.ActiveProfile);
            }
            catch (Exception ex) { log.LogError(ex, "Rules evaluation failed"); }
        }
    }

    public void Invalidate() => _reported.Clear();

    private void Evaluate(List<StoredRule> list, string profile)
    {
        foreach (var r in list.Where(r => r.Enabled && (r.Rule.Profile == Profiles.Any || r.Rule.Profile == profile)))
        {
            // Defense in depth: never trust what's stored.
            if (!RuleValidator.Validate(r.Rule).Ok) continue;
            foreach (var p in Process.GetProcessesByName(RuleValidator.NormalizeProcessName(r.Rule.Process)))
            {
                using (p)
                {
                    if (!_reported.Add((r.Id, p.Id, profile))) continue;
                    var desc = Describe(r.Rule, p.ProcessName, p.Id);
                    state.AddDryRun(new DryRunEntry(DateTime.UtcNow, r.Id, p.ProcessName, p.Id, desc));
                    log.LogInformation("[dry-run] {Description}", desc);
                }
            }
        }
    }

    internal static string Describe(RuleProposal r, string name, int pid) => r.Action switch
    {
        RuleActions.SetPriority => $"Would set {name} (pid {pid}) to {r.Value.Replace('_', ' ')} priority",
        RuleActions.EfficiencyMode => $"Would turn efficiency mode {r.Value} for {name} (pid {pid})",
        RuleActions.CpuCap => $"Would cap {name} (pid {pid}) at {r.Value.TrimEnd('%')}% CPU",
        RuleActions.MemoryCapMb => $"Would cap {name} (pid {pid}) at {r.Value} MB of memory",
        _ => $"Unknown action for {name}",
    };
}
