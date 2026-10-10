using ResMan.Brain.Doctor;
using ResMan.Brain.Harness;
using ResMan.Brain.Knowledge;
using ResMan.Brain.Models;
using ResMan.Core;
using ResMan.Data;

namespace ResMan.Agent;

/// <summary>
/// Lazily opens the service's database read-only. The agent may start before the service has
/// created it, so this keeps retrying until it exists.
/// </summary>
public sealed class DoctorHost(KnowledgeBase kb, IModelProvider model)
{
    private readonly Lock _lock = new();
    private DoctorService? _doctor;
    private DoctorHarness? _harness;

    public IModelProvider Model => model;
    public KnowledgeBase Knowledge => kb;

    public DoctorService? Doctor { get { Ensure(); return _doctor; } }
    public DoctorHarness? Harness { get { Ensure(); return _harness; } }

    private void Ensure()
    {
        if (_doctor is not null) return;
        lock (_lock)
        {
            if (_doctor is not null || !File.Exists(ResManPaths.DbPath)) return;
            var d = new DoctorService(new ResManDb(ResManPaths.DbPath, readOnly: true), kb);
            _harness = new DoctorHarness(d, model);
            _doctor = d;
        }
    }

    /// <summary>Finds the sidecar and model: config, then env vars, then next to the app, then the dev repo layout.</summary>
    public static SidecarOptions ResolveInference(IConfiguration config)
    {
        var o = config.GetSection("Inference").Get<SidecarOptions>() ?? new SidecarOptions();
        o.InferencePath ??= Environment.GetEnvironmentVariable("RESMAN_INFERENCE");
        o.ModelPath ??= Environment.GetEnvironmentVariable("RESMAN_MODEL");

        if (o.InferencePath is null)
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "ResMan.Inference.exe");
            if (File.Exists(beside)) o.InferencePath = beside;
            else if (ResManPaths.FindUp("src") is { } src)
                o.InferencePath = new[] { "Release", "Debug" }
                    .Select(c => Path.Combine(src, "ResMan.Inference", "bin", c, "net10.0-windows10.0.19041.0", "ResMan.Inference.exe"))
                    .FirstOrDefault(File.Exists);
        }

        if (o.ModelPath is null)
        {
            var dirs = new[] { Path.Combine(ResManPaths.UserDir, "models"), ResManPaths.FindUp("models") };
            o.ModelPath = dirs.Where(d => d is not null && Directory.Exists(d))
                .SelectMany(d => Directory.EnumerateFiles(d!, "*.gguf"))
                .Where(f => !Path.GetFileName(f).Contains("embed", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f).Contains("4b", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        return o;
    }
}
