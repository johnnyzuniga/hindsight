using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ResMan.Core;

namespace ResMan.Agent;

/// <summary>Shared in-memory state of the agent, plus the live-update fan-out to WebSocket clients.</summary>
public sealed class AgentState
{
    public volatile bool Paused;
    public volatile string Profile = Profiles.Focus;
    public string? ForegroundApp;
    public DateTime? LastAiRunUtc;
    public int HangsDetected;

    /// <summary>Raised for user-facing notifications (title, message). The tray shows them.</summary>
    public event Action<string, string>? Notify;
    public void RaiseNotify(string title, string message) => Notify?.Invoke(title, message);

    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task HoldAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _sockets[id] = ws;
        var buf = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally
        {
            _sockets.TryRemove(id, out _);
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }

    // A WebSocket allows one send at a time, and broadcasts can overlap (investigation steps, hangs, events).
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task BroadcastAsync(string type, object data)
    {
        if (_sockets.IsEmpty) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, data, timeUtc = DateTime.UtcNow }, Json));
        await _sendLock.WaitAsync();
        try
        {
            foreach (var (id, ws) in _sockets)
            {
                try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { _sockets.TryRemove(id, out _); }
            }
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>Streams investigation steps to live clients, tagged with the caller's request id.</summary>
    public Action<ResMan.Brain.Harness.InvestigationStep>? StepSink(string? requestId) =>
        string.IsNullOrWhiteSpace(requestId) ? null
            : s => _ = BroadcastAsync("investigation_step", new { requestId, s.Step, s.Kind, s.Tool, s.Input, s.Label, s.Finding, ms = Math.Round(s.Ms), elapsedMs = Math.Round(s.ElapsedMs) });
}
