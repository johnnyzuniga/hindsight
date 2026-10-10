using ResMan.Core;

namespace ResMan.App.Services;

/// <summary>A tiny append-only log in %LOCALAPPDATA%\ResMan\app.log, for diagnosing the UI itself.</summary>
public static class Log
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ResManPaths.UserDir);
                var path = Path.Combine(ResManPaths.UserDir, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
