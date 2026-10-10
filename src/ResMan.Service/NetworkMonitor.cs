using System.Net.NetworkInformation;
using System.Net.Sockets;
using ResMan.Core;
using ResMan.Data;
using Windows.Networking.Connectivity;
using WinNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace ResMan.Service;

/// <summary>
/// Records network state every 10 s (and immediately on changes): connectivity level, Wi-Fi name and signal,
/// and whether the router answers a ping. Nothing leaves the local network, so it keeps working
/// when the internet is down. That's exactly when it's needed.
/// </summary>
public sealed class NetworkMonitor(ReplayStore store, HealthStore health, ReplayBuffer buffer, ILogger<NetworkMonitor> log) : BackgroundService
{
    private NetworkSample? _last;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<NetworkSample> _recent = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        void OnChange(object? _) => _ = Task.Run(() => SampleAsync(ct), ct);
        WinNetworkInformation.NetworkStatusChanged += OnChange;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do { await SampleAsync(ct); } while (await timer.WaitForNextTickAsync(ct));
        }
        finally { WinNetworkInformation.NetworkStatusChanged -= OnChange; }
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var s = await TakeAsync();
            store.InsertNetwork(s);
            _recent.Enqueue(s);
            while (_recent.Count > 30) _recent.Dequeue();
            if (_last is not null && _last.Connectivity != s.Connectivity) RecordChange(_last, s);
            _last = s;
        }
        catch (Exception ex) { log.LogWarning("Network sample failed: {Message}", ex.Message); }
        finally { _gate.Release(); }
    }

    private void RecordChange(NetworkSample before, NetworkSample now)
    {
        var bars = string.Join(",", _recent.Select(r => r.SignalBars?.ToString() ?? "-"));
        var summary = now.Connectivity switch
        {
            Connectivity.Internet => "Internet access came back",
            Connectivity.None => before.IsWifi ? $"Wi-Fi disconnected{(before.Ssid is null ? "" : $" from '{before.Ssid}'")}" : "Network disconnected",
            _ => $"Internet access lost; still connected to {(now.Ssid is null ? "the local network" : $"'{now.Ssid}'")}",
        };
        var id = health.Insert(new HealthEvent(0, now.TimeUtc, "ResMan", "ResMan.NetworkMonitor", 1, now.Connectivity == Connectivity.Internet ? 4 : 3,
            HealthKind.NetworkChange, null, summary, new Dictionary<string, string>
            {
                ["From"] = before.Connectivity,
                ["To"] = now.Connectivity,
                ["Ssid"] = before.Ssid ?? now.Ssid ?? "",
                ["SignalBarsBefore"] = before.SignalBars?.ToString() ?? "",
                ["SignalBarsNow"] = now.SignalBars?.ToString() ?? "",
                ["RecentSignalBars"] = bars,
                ["RouterReachableNow"] = now.GatewayReachable.ToString(),
                ["RouterPingMsBefore"] = before.GatewayPingMs?.ToString() ?? "",
                ["Adapter"] = before.AdapterName ?? now.AdapterName ?? "",
            }, null));
        log.LogInformation("Network: {Summary}", summary);
        if (id is { } eventId && now.Connectivity != Connectivity.Internet) buffer.SaveAround(eventId, now.TimeUtc);
    }

    internal static async Task<NetworkSample> TakeAsync()
    {
        var profile = WinNetworkInformation.GetInternetConnectionProfile();
        var level = profile?.GetNetworkConnectivityLevel() switch
        {
            NetworkConnectivityLevel.InternetAccess => Connectivity.Internet,
            NetworkConnectivityLevel.ConstrainedInternetAccess => Connectivity.Constrained,
            NetworkConnectivityLevel.LocalAccess => Connectivity.LocalOnly,
            _ => Connectivity.None,
        };
        string? ssid = null;
        int? bars = null;
        var isWifi = profile?.IsWlanConnectionProfile ?? false;
        try { if (isWifi) ssid = profile!.WlanConnectionProfileDetails?.GetConnectedSsid(); } catch { }
        try { if (profile?.GetSignalBars() is { } b) bars = b; } catch { }

        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => (n, gw: n.GetIPProperties().GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)))
            .Where(x => x.gw is not null && !x.gw.Equals(System.Net.IPAddress.Any))
            .OrderByDescending(x => x.n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .FirstOrDefault();
        isWifi |= nic.n?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

        int? pingMs = null;
        var reachable = false;
        if (nic.gw is not null)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(nic.gw, 1000);
                reachable = reply.Status == IPStatus.Success;
                if (reachable) pingMs = (int)reply.RoundtripTime;
            }
            catch (PingException) { }
        }
        return new NetworkSample(DateTime.UtcNow, level, ssid, bars, isWifi, nic.n?.Description, nic.gw?.ToString(), pingMs, reachable);
    }
}
