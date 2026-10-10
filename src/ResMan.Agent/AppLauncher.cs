using System.Diagnostics;
using ResMan.Core;

namespace ResMan.Agent;

/// <summary>Opens the ResMan window (ResMan.App.exe), or brings it to the front if it's already open.</summary>
public static class AppLauncher
{
    private const int SwRestore = 9;

    /// <summary>Next to the agent when installed; in development, the newest build under src/ResMan.App/bin.</summary>
    public static string? FindExe()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "ResMan.App.exe");
        if (File.Exists(beside)) return beside;
        var src = ResManPaths.FindUp("src");
        var bin = src is null ? null : Path.Combine(src, "ResMan.App", "bin");
        if (bin is null || !Directory.Exists(bin)) return null;
        return new DirectoryInfo(bin).EnumerateFiles("ResMan.App.exe", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    public static string? IconPath() =>
        FindExe() is { } exe && Path.Combine(Path.GetDirectoryName(exe)!, "Assets", "ResMan.ico") is var ico && File.Exists(ico) ? ico : null;

    /// <summary>Returns false (with a reason) when the window can't be opened.</summary>
    public static bool Open(out string? problem, params string[] args)
    {
        problem = null;
        var running = Process.GetProcessesByName("ResMan.App").FirstOrDefault(p => p.MainWindowHandle != 0);
        if (running is not null)
        {
            // Never close a window the user has open; bring it forward as it is.
            User32.ShowWindow(running.MainWindowHandle, SwRestore);
            User32.SetForegroundWindow(running.MainWindowHandle);
            return true;
        }
        var exe = FindExe();
        if (exe is null)
        {
            problem = "The Hindsight window isn't installed next to the agent. For now, run resman.exe in a terminal.";
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            problem = $"The Hindsight window couldn't start: {ex.Message}";
            return false;
        }
    }
}
