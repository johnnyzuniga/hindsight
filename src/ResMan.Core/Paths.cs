namespace ResMan.Core;

public static class ResManPaths
{
    /// <summary>Machine-wide data written by the service (readable by users).</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("RESMAN_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ResMan");

    public static string DbPath => Path.Combine(DataDir, "resman.db");

    /// <summary>Per-user state (agent port and token).</summary>
    public static string UserDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResMan");

    public static string AgentInfoPath => Path.Combine(UserDir, "agent.json");

    public const string ServicePipeName = "ResMan.Service.v1";

    public static string InferencePipeName => $"ResMan.Inference.v1.{Environment.UserName}";

    /// <summary>Walks up from a start directory to find a folder (dev layout: repo root holds kb/ and models/).</summary>
    public static string? FindUp(string folderName, string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, folderName);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
