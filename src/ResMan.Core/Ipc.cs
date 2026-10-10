using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResMan.Core;

/// <summary>Agent to Service messages over the named pipe (one JSON object per line).</summary>
public sealed record PipeRequest(string Type, JsonElement? Payload = null);

public sealed record PipeResponse(bool Ok, string? Error = null, JsonElement? Payload = null);

public static class PipeTypes
{
    public const string Status = "status";
    public const string Hang = "hang";
    public const string Context = "context";
    public const string RulesList = "rules.list";
    public const string RulesAdd = "rules.add";
    public const string RulesRemove = "rules.remove";
    public const string DryRunLog = "rules.dryrun_log";
    public const string FixApplied = "fix.applied";
    public const string ScanNow = "scan.now";
}

public sealed record HangReport(string AppName, int Pid, DateTime StartUtc, double DurationSeconds, bool Recovered);

public sealed record ContextUpdate(string Profile, string? ForegroundApp, bool Fullscreen, double IdleSeconds, bool OnBattery);

public sealed record ServiceStatus(
    DateTime StartedUtc,
    long WorkingSetBytes,
    bool Elevated,
    string ActiveProfile,
    long SnapshotsStored,
    long HealthEventsStored,
    DateTime? LastSnapshotUtc,
    IReadOnlyList<string> Warnings);

public sealed record DryRunEntry(DateTime TimeUtc, long RuleId, string Process, int Pid, string Description);

/// <summary>Sidecar inference protocol (Agent to ResMan.Inference).</summary>
/// <summary>A generation request, or a control command ("stats", "keepalive") when Command is set.</summary>
public sealed record InferenceRequest(string System, string User, string? Gbnf, int MaxTokens = 512, float Temperature = 0.2f,
    string? Command = null, int? IdleSeconds = null);

public sealed record InferenceResponse(string? Text, string? Error, double Milliseconds, int Tokens, string Backend)
{
    public double? LoadMilliseconds { get; init; }
    public string? Model { get; init; }
    public double? TokensPerSecond { get; init; }
    public double? LastInferenceMilliseconds { get; init; }
    public int? IdleSeconds { get; init; }
    public int? Requests { get; init; }
    public int? ContextSize { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(PipeResponse))]
[JsonSerializable(typeof(HangReport))]
[JsonSerializable(typeof(ContextUpdate))]
[JsonSerializable(typeof(ServiceStatus))]
[JsonSerializable(typeof(RuleProposal))]
[JsonSerializable(typeof(StoredRule))]
[JsonSerializable(typeof(List<StoredRule>))]
[JsonSerializable(typeof(List<DryRunEntry>))]
[JsonSerializable(typeof(InferenceRequest))]
[JsonSerializable(typeof(InferenceResponse))]
[JsonSerializable(typeof(ValidationResult))]
[JsonSerializable(typeof(FixAttempt))]
[JsonSerializable(typeof(long))]
public partial class ResManJson : JsonSerializerContext;
