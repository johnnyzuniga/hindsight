using System.Collections.Concurrent;
using System.Security.Principal;
using ResMan.Core;

namespace ResMan.Service;

/// <summary>In-memory state shared by the service's background workers.</summary>
public sealed class ServiceState
{
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public volatile string ActiveProfile = Profiles.Focus;
    public DateTime? LastSnapshotUtc;
    public ConcurrentDictionary<string, string> Warnings { get; } = new();
    public ConcurrentQueue<DryRunEntry> DryRunLog { get; } = new();

    public bool Elevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public void AddDryRun(DryRunEntry e)
    {
        DryRunLog.Enqueue(e);
        while (DryRunLog.Count > 200) DryRunLog.TryDequeue(out _);
    }
}
