using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ResMan.App.Services;

/// <summary>
/// Keeps a WebSocket open to the agent's live feed (new events, freezes, investigation steps)
/// and reconnects on its own after the agent restarts.
/// </summary>
public sealed class LiveClient(AgentClient agent)
{
    /// <summary>Raised off the UI thread with (type, data).</summary>
    public event Action<string, JsonNode?>? Message;
    public bool Connected { get; private set; }
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits briefly for the feed, so a question's first steps aren't missed.</summary>
    public Task WaitConnectedAsync(TimeSpan timeout) => Connected ? Task.CompletedTask : Task.WhenAny(_connected.Task, Task.Delay(timeout));

    public async Task RunAsync(CancellationToken ct = default)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            if (!agent.EnsureConnected() || agent.LiveUrl is not { } url)
            {
                await Task.Delay(2000, ct);
                continue;
            }
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(url), ct);
                Connected = true;
                _connected.TrySetResult();
                var sb = new StringBuilder();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    try
                    {
                        var node = JsonNode.Parse(sb.ToString());
                        if (node?["type"]?.GetValue<string>() is { } type) Message?.Invoke(type, node["data"]);
                    }
                    catch (Exception ex) { Log.Write($"Live message ignored: {ex.Message}"); }
                    sb.Clear();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write($"Live feed: {ex.Message}"); }
            finally
            {
                Connected = false;
                _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            await Task.Delay(2000, ct);
        }
    }
}
