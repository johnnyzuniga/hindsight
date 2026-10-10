using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ResMan.Core;

namespace ResMan.Brain.Models;

public sealed record ModelResult(string? Text, string? Error, double Milliseconds, string Backend)
{
    public bool Ok => Error is null && Text is not null;
    public double? TokensPerSecond { get; init; }
    public int Tokens { get; init; }
}

/// <summary>Swappable model backend: the bundled sidecar by default; Ollama, NPU or cloud could be added later.</summary>
public interface IModelProvider
{
    bool Enabled { get; }
    string Describe { get; }
    Task<ModelResult> CompleteAsync(InferenceRequest request, CancellationToken ct = default);
}

/// <summary>No-AI tier: the doctor still works from analyzers and signatures.</summary>
public sealed class NullModelProvider : IModelProvider
{
    public bool Enabled => false;
    public string Describe => "AI turned off";
    public Task<ModelResult> CompleteAsync(InferenceRequest request, CancellationToken ct = default) =>
        Task.FromResult(new ModelResult(null, "AI is turned off.", 0, "none"));
}

public sealed class SidecarOptions
{
    /// <summary>Path to ResMan.Inference.exe (or .dll in development).</summary>
    public string? InferencePath { get; set; }
    public string? ModelPath { get; set; }
    public int IdleSeconds { get; set; } = 30;
    public int GpuLayers { get; set; } = 99;
    public int ContextSize { get; set; } = 8192;
}

/// <summary>
/// Talks to the ResMan.Inference sidecar over a per-user named pipe, starting it on demand.
/// The sidecar exits on its own after a short idle period so its memory goes back to the system.
/// </summary>
public sealed class SidecarModelProvider(SidecarOptions options) : IModelProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string _state = "unloaded";
    private DateTime? _loadStartedUtc;

    public SidecarOptions Options => options;
    public double? LastColdStartMilliseconds { get; private set; }

    /// <summary>unloaded, loading, ready or busy. Derived from the sidecar process and the last request.</summary>
    public string State
    {
        get
        {
            if (_state is "loading" or "busy") return _state;
            return SidecarProcessId() is null ? "unloaded" : "ready";
        }
    }

    public static int? SidecarProcessId()
    {
        foreach (var p in Process.GetProcessesByName("ResMan.Inference"))
            using (p)
                if (p.SessionId == Process.GetCurrentProcess().SessionId) return p.Id;
        return null;
    }

    /// <summary>Asks a running sidecar for its numbers (load time, tokens/s, idle timeout). Never starts it.</summary>
    public async Task<InferenceResponse?> StatsAsync(CancellationToken ct = default)
    {
        if (SidecarProcessId() is null || _state is "loading" or "busy") return null;
        return await SendAsync(new InferenceRequest("", "", null, Command: "stats"), start: false, ct);
    }

    /// <summary>Loads the model now and keeps it warm for a while (for demos).</summary>
    public Task<InferenceResponse?> PreloadAsync(int minutes, CancellationToken ct = default) =>
        SendAsync(new InferenceRequest("", "", null, Command: "keepalive", IdleSeconds: Math.Clamp(minutes, 1, 240) * 60), start: true, ct);

    private async Task<InferenceResponse?> SendAsync(InferenceRequest request, bool start, CancellationToken ct)
    {
        if (!Enabled) return null;
        await _gate.WaitAsync(ct);
        try
        {
            await using var pipe = start ? await ConnectAsync(ct) : await TryConnectAsync(ct);
            if (pipe is null) return null;
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ResManJson.Default.InferenceRequest).AsMemory(), ct);
            var line = await reader.ReadLineAsync(ct);
            return line is null ? null : JsonSerializer.Deserialize(line, ResManJson.Default.InferenceResponse);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException) { return null; }
        finally { _gate.Release(); }
    }

    private async Task<NamedPipeClientStream?> TryConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", ResManPaths.InferencePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(500, ct); return pipe; }
        catch (TimeoutException) { await pipe.DisposeAsync(); return null; }
    }

    public bool Enabled => File.Exists(options.ModelPath) && File.Exists(options.InferencePath);

    public string Describe => Enabled
        ? $"Local model: {Path.GetFileNameWithoutExtension(options.ModelPath)}"
        : $"Local model not found (model: {options.ModelPath ?? "unset"}, sidecar: {options.InferencePath ?? "unset"})";

    public async Task<ModelResult> CompleteAsync(InferenceRequest request, CancellationToken ct = default)
    {
        if (!Enabled) return new(null, Describe, 0, "none");
        await _gate.WaitAsync(ct);
        try
        {
            await using var pipe = await ConnectAsync(ct);
            _state = "busy";
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ResManJson.Default.InferenceRequest).AsMemory(), ct);
            var line = await reader.ReadLineAsync(ct) ?? throw new IOException("The local model closed the connection.");
            var resp = JsonSerializer.Deserialize(line, ResManJson.Default.InferenceResponse)!;
            return new(resp.Text, resp.Error, resp.Milliseconds, resp.Backend) { TokensPerSecond = resp.TokensPerSecond, Tokens = resp.Tokens };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException)
        {
            return new(null, $"The local model isn't responding: {ex.Message}", 0, "none");
        }
        finally { _state = "idle"; _gate.Release(); }
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", ResManPaths.InferencePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(300, ct);
            return pipe;
        }
        catch (TimeoutException) { }

        if (SidecarProcessId() is null) StartSidecar();
        _state = "loading";
        _loadStartedUtc = DateTime.UtcNow;
        try
        {
            // Loading a few GB of model weights can take a while on first start.
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await pipe.ConnectAsync(1000, ct);
                    LastColdStartMilliseconds = (DateTime.UtcNow - _loadStartedUtc.Value).TotalMilliseconds;
                    return pipe;
                }
                catch (TimeoutException) { }
            }
            await pipe.DisposeAsync();
            throw new TimeoutException("The local model didn't start in time.");
        }
        finally { _state = "idle"; }
    }

    private void StartSidecar()
    {
        var path = options.InferencePath!;
        var args = $"--model \"{options.ModelPath}\" --pipe \"{ResManPaths.InferencePipeName}\" --idle {options.IdleSeconds} --gpu-layers {options.GpuLayers} --ctx {options.ContextSize}";
        var psi = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("dotnet", $"\"{path}\" {args}")
            : new ProcessStartInfo(path, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var p = Process.Start(psi);
        try { if (p is not null) p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* it lowers itself too */ }
    }
}
