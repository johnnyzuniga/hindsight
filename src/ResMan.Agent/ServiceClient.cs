using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ResMan.Core;

namespace ResMan.Agent;

/// <summary>Named-pipe client for the ResMan service. One short connection per request.</summary>
public sealed class ServiceClient(ILogger<ServiceClient> log)
{
    public async Task<PipeResponse> SendAsync(string type, JsonElement? payload = null, CancellationToken ct = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", ResManPaths.ServicePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, ct);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeRequest(type, payload), ResManJson.Default.PipeRequest));
            var line = await reader.ReadLineAsync(ct);
            return line is null ? new(false, "No reply from the ResMan service.") : JsonSerializer.Deserialize(line, ResManJson.Default.PipeResponse)!;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            log.LogDebug("Service unreachable: {Message}", ex.Message);
            return new(false, "The ResMan service isn't running.");
        }
    }

    public Task<PipeResponse> SendAsync<T>(string type, T payload, JsonTypeInfo<T> info, CancellationToken ct = default) =>
        SendAsync(type, JsonSerializer.SerializeToElement(payload, info), ct);

    public async Task<T?> GetAsync<T>(string type, JsonTypeInfo<T> info, CancellationToken ct = default)
    {
        var r = await SendAsync(type, null, ct);
        return r.Ok && r.Payload is { } p ? p.Deserialize(info) : default;
    }
}
