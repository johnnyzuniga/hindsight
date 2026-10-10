using System.Text.RegularExpressions;

namespace ResMan.Core;

public sealed record ValidationResult(bool Ok, IReadOnlyList<string> Errors)
{
    public static readonly ValidationResult Valid = new(true, []);
}

/// <summary>
/// The single gate every rule passes through, in the Brain and again in the Service.
/// The model can propose anything; only rules that pass here can ever act.
/// </summary>
public static partial class RuleValidator
{
    /// <summary>Processes that must never be throttled: OS-critical, security, anti-cheat, and ResMan itself.</summary>
    public static readonly IReadOnlySet<string> DenyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "secure system", "memory compression",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
        "dwm", "explorer", "sihost", "fontdrvhost", "audiodg", "ctfmon", "taskhostw",
        "msmpeng", "mpdefendercoreservice", "nissrv", "securityhealthservice", "smartscreen",
        "easyanticheat", "easyanticheat_eos", "beservice", "vgc", "vgtray", "faceit", "faceitservice",
        "resman", "resman.service", "resman.agent", "resman.inference", "resman.cli",
    };

    public static readonly string[] Priorities = ["idle", "below_normal", "normal", "above_normal"];

    [GeneratedRegex(@"^[A-Za-z0-9_.\- ]{1,64}$")]
    private static partial Regex ProcessNamePattern();

    public static string NormalizeProcessName(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    /// <summary>ResMan's own processes (and the dev CLI). Their hiccups aren't the user's PC problems.</summary>
    public static bool IsOwnProcess(string? processName) =>
        processName is not null && NormalizeProcessName(processName).StartsWith("resman", StringComparison.OrdinalIgnoreCase);

    public static bool IsProtected(string processName) => DenyList.Contains(NormalizeProcessName(processName));

    public static ValidationResult Validate(RuleProposal? rule)
    {
        if (rule is null) return new(false, ["Rule is empty."]);
        var errors = new List<string>();
        var proc = rule.Process ?? "";

        if (!ProcessNamePattern().IsMatch(proc))
            errors.Add($"'{proc}' is not a valid process name.");
        else if (IsProtected(proc))
            errors.Add($"'{proc}' is a protected Windows or security process and can't be changed.");

        if (!Profiles.All.Contains(rule.Profile))
            errors.Add($"Unknown profile '{rule.Profile}'.");

        var value = rule.Value?.Trim().ToLowerInvariant() ?? "";
        switch (rule.Action)
        {
            case RuleActions.SetPriority:
                if (!Priorities.Contains(value))
                    errors.Add($"Priority must be one of: {string.Join(", ", Priorities)}.");
                else if (value == "above_normal")
                    errors.Add("Raising priority above normal isn't allowed; lower other apps instead.");
                break;
            case RuleActions.EfficiencyMode:
                if (value is not ("on" or "off")) errors.Add("Efficiency mode value must be 'on' or 'off'.");
                break;
            case RuleActions.CpuCap:
                if (!int.TryParse(value.TrimEnd('%'), out var pct) || pct < 5 || pct > 95)
                    errors.Add("CPU cap must be a percentage between 5 and 95.");
                break;
            case RuleActions.MemoryCapMb:
                if (!int.TryParse(value, out var mb) || mb < 256 || mb > 65536)
                    errors.Add("Memory cap must be between 256 and 65536 MB.");
                break;
            default:
                errors.Add($"Unknown action '{rule.Action}'.");
                break;
        }

        return errors.Count == 0 ? ValidationResult.Valid : new(false, errors);
    }
}
