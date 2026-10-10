using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ResMan.Core;
using ResMan.Data;

namespace ResMan.Service;

/// <summary>
/// Local named-pipe endpoint for the Agent. Newline-delimited JSON. Network access is denied;
/// every rule received is validated again here regardless of what the sender checked.
/// </summary>
public sealed class PipeServer(
    ServiceState state, TelemetrySampler sampler, TelemetryStore telemetry, HealthStore health,
    RuleStore rules, FixStore fixes, ChangeCollector changes, DriveHealthCollector drives, ReplayBuffer buffer, ILogger<PipeServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = Create(); }
            catch (Exception ex)
            {
                log.LogError(ex, "Can't create pipe {Name}", ResManPaths.ServicePipeName);
                await Task.Delay(5000, ct);
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                _ = Task.Run(() => HandleAsync(pipe, ct), ct);
            }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
        }
    }

    private static NamedPipeServerStream Create()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(ResManPaths.ServicePipeName, PipeDirection.InOut, 8,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            try
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    PipeResponse resp;
                    try
                    {
                        var req = JsonSerializer.Deserialize(line, ResManJson.Default.PipeRequest);
                        resp = req is null ? new(false, "Empty request") : Handle(req);
                    }
                    catch (Exception ex) { resp = new(false, ex.Message); }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(resp, ResManJson.Default.PipeResponse));
                }
            }
            catch (IOException) { /* client went away */ }
            catch (OperationCanceledException) { }
        }
    }

    internal PipeResponse Handle(PipeRequest req)
    {
        switch (req.Type)
        {
            case PipeTypes.Status:
                using (var me = Process.GetCurrentProcess())
                {
                    var status = new ServiceStatus(state.StartedUtc, me.PrivateMemorySize64, state.Elevated, state.ActiveProfile,
                        telemetry.Count(), health.Count(), state.LastSnapshotUtc, state.Warnings.Values.ToList());
                    return Ok(JsonSerializer.SerializeToElement(status, ResManJson.Default.ServiceStatus));
                }

            case PipeTypes.Hang:
            {
                var h = Payload(req, ResManJson.Default.HangReport);
                var snapId = telemetry.Insert(sampler.Sample());
                var id = health.Insert(new HealthEvent(0, h.StartUtc, "ResMan", "ResMan.HangDetector", 1, 3, HealthKind.AppHang,
                    h.AppName, $"{h.AppName} stopped responding for {h.DurationSeconds:0} seconds",
                    new Dictionary<string, string>
                    {
                        ["DurationSeconds"] = h.DurationSeconds.ToString("0.0"),
                        ["Recovered"] = h.Recovered.ToString(),
                        ["Pid"] = h.Pid.ToString(),
                    }, snapId));
                if (id is { } hangId) buffer.SaveAround(hangId, h.StartUtc);
                log.LogInformation("Hang reported: {App} for {Seconds:0.0}s", h.AppName, h.DurationSeconds);
                return Ok(JsonSerializer.SerializeToElement(id ?? 0, ResManJson.Default.Int64));
            }

            case PipeTypes.Context:
            {
                var c = Payload(req, ResManJson.Default.ContextUpdate);
                if (Profiles.All.Contains(c.Profile) && c.Profile != state.ActiveProfile)
                {
                    log.LogInformation("Profile {Old} -> {New} (foreground: {App})", state.ActiveProfile, c.Profile, c.ForegroundApp);
                    state.ActiveProfile = c.Profile;
                }
                return Ok();
            }

            case PipeTypes.RulesList:
                return Ok(JsonSerializer.SerializeToElement(rules.List(), ResManJson.Default.ListStoredRule));

            case PipeTypes.RulesAdd:
            {
                var rule = Payload(req, ResManJson.Default.RuleProposal);
                var v = RuleValidator.Validate(rule);
                if (!v.Ok) return new(false, string.Join(" ", v.Errors));
                var id = rules.Add(rule);
                log.LogInformation("Rule {Id} added: {Action} {Value} on {Process} ({Profile})", id, rule.Action, rule.Value, rule.Process, rule.Profile);
                return Ok(JsonSerializer.SerializeToElement(id, ResManJson.Default.Int64));
            }

            case PipeTypes.RulesRemove:
            {
                var id = Payload(req, ResManJson.Default.Int64);
                return rules.Remove(id) ? Ok() : new(false, $"No rule {id}");
            }

            case PipeTypes.FixApplied:
            {
                var f = Payload(req, ResManJson.Default.FixAttempt);
                if (string.IsNullOrWhiteSpace(f.IssueKey) || string.IsNullOrWhiteSpace(f.Label) || f.Label.Length > 300)
                    return new(false, "A fix needs an issue and a short description.");
                var id = fixes.Add(f with { AppliedUtc = f.AppliedUtc == default ? DateTime.UtcNow : f.AppliedUtc });
                log.LogInformation("Fix recorded for {Issue}: {Label}", f.IssueKey, f.Label);
                return Ok(JsonSerializer.SerializeToElement(id, ResManJson.Default.Int64));
            }

            case PipeTypes.ScanNow:
                changes.ScanInventory(DateTime.UtcNow);
                drives.Collect(DateTime.UtcNow);
                return Ok();

            case PipeTypes.DryRunLog:
                return Ok(JsonSerializer.SerializeToElement(state.DryRunLog.Reverse().ToList(), ResManJson.Default.ListDryRunEntry));

            default:
                return new(false, $"Unknown request type '{req.Type}'");
        }
    }

    private static T Payload<T>(PipeRequest req, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        req.Payload is { } p ? p.Deserialize(info) ?? throw new InvalidDataException("Bad payload") : throw new InvalidDataException("Missing payload");

    private static PipeResponse Ok(JsonElement? payload = null) => new(true, null, payload);
}
