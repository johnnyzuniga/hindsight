using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ResMan.Core;

namespace ResMan.App.Services;

/// <summary>Raised with a plain-language message the page can show as-is.</summary>
public sealed class AgentException(string message) : Exception(message);

/// <summary>
/// Talks to the local ResMan Agent API. The agent picks a new port and token on every start,
/// so connection details are re-read from agent.json whenever a call can't get through.
/// </summary>
public sealed class AgentClient
{
    private HttpClient? _http;
    private string? _url;
    public string? Token { get; private set; }

    public string? LiveUrl => _url is null || Token is null ? null
        : _url.Replace("http://", "ws://", StringComparison.Ordinal) + "/api/v1/live?token=" + Token;

    private bool Connect()
    {
        try
        {
            var info = JsonNode.Parse(File.ReadAllText(ResManPaths.AgentInfoPath))!;
            var pid = info["pid"]!.GetValue<int>();
            try { using var _ = Process.GetProcessById(pid); } catch { return false; }
            var url = info["url"]!.GetValue<string>();
            var token = info["token"]!.GetValue<string>();
            if (url == _url && token == Token && _http is not null) return true;
            _http?.Dispose();
            // Investigations run several model calls; give them room.
            _http = new HttpClient { BaseAddress = new Uri(url + "/api/v1/"), Timeout = TimeSpan.FromMinutes(5) };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (_url, Token) = (url, token);
            return true;
        }
        catch { return false; }
    }

    public bool EnsureConnected() => (_http is not null || Connect()) && _http is not null;

    private async Task<HttpResponseMessage> SendAsync(Func<HttpClient, Task<HttpResponseMessage>> send)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!Connect()) break;
            try { return await send(_http!); }
            catch (HttpRequestException) when (attempt == 0) { _http = null; _url = null; } // agent restarted: re-read agent.json
        }
        throw new AgentException("Hindsight isn't running. Start it from the Start menu or the tray, then try again.");
    }

    public async Task<JsonNode> GetAsync(string path, CancellationToken ct = default) =>
        await ReadAsync(await SendAsync(h => h.GetAsync(path, ct)), ct);

    public async Task<JsonNode> PostAsync(string path, object? body = null, CancellationToken ct = default) =>
        await ReadAsync(await SendAsync(h => h.PostAsJsonAsync(path, body ?? new { }, ct)), ct);

    /// <summary>The report endpoint returns HTML; saved to a temp file so the browser can open and print it.</summary>
    public async Task<string> SaveReportAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(h => h.GetAsync("report?days=30", ct));
        var html = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new AgentException("The report couldn't be made right now.");
        var file = Path.Combine(Path.GetTempPath(), $"ResMan-report-{DateTime.Now:yyyyMMdd-HHmm}.html");
        await File.WriteAllTextAsync(file, html, ct);
        return file;
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage r, CancellationToken ct)
    {
        var text = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode)
        {
            string? msg = null;
            try { msg = text.Length > 0 ? JsonNode.Parse(text)?["error"]?.ToString() : null; } catch { }
            throw new AgentException(msg ?? (int)r.StatusCode switch
            {
                503 => "Hindsight is still starting up. Give it a few seconds.",
                404 => "That record no longer exists.",
                409 => "The AI is paused while you're gaming.",
                _ => $"Something went wrong ({(int)r.StatusCode}).",
            });
        }
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }
}
