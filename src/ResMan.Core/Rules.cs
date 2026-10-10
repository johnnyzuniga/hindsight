using System.Text.Json.Serialization;

namespace ResMan.Core;

public static class RuleActions
{
    public const string SetPriority = "set_priority";
    public const string EfficiencyMode = "efficiency_mode";
    public const string CpuCap = "cpu_cap";
    public const string MemoryCapMb = "memory_cap_mb";
    public static readonly string[] All = [SetPriority, EfficiencyMode, CpuCap, MemoryCapMb];
}

public static class Profiles
{
    public const string Any = "any";
    public const string Gaming = "gaming";
    public const string Focus = "focus";
    public const string Battery = "battery";
    public const string Idle = "idle";
    public static readonly string[] All = [Any, Gaming, Focus, Battery, Idle];
}

/// <summary>A rule as proposed by the user or the model. Never trusted until validated.</summary>
public sealed record RuleProposal(
    [property: JsonPropertyName("process")] string Process,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record StoredRule(long Id, RuleProposal Rule, bool Enabled, DateTime CreatedUtc);
