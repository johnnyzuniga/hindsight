using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using ResMan.Core;
using ResMan.Inference;

// ResMan.Inference: the on-demand local model sidecar.
// Started by the Agent when a question needs the model, serves requests over a per-user named pipe,
// and exits after a short idle period so every byte of RAM/VRAM goes back to the system.

var opts = Options.Parse(args);
if (opts.ModelPath is null || !File.Exists(opts.ModelPath))
{
    Console.Error.WriteLine($"Model not found: {opts.ModelPath ?? "(none)"}. Usage: ResMan.Inference --model <file.gguf> [--pipe name] [--idle 30] [--gpu-layers 99] [--ctx 4096] [--prompt \"text\"]");
    return 2;
}

using (var me = Process.GetCurrentProcess())
{
    try { me.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
}

static void Log(string line)
{
    try { File.AppendAllText(Path.Combine(ResManPaths.UserDir, "inference.log"), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
}

// Prefer the GPU through Vulkan (works on NVIDIA, AMD and Intel with a normal driver); fall back to CPU.

string? device = null;
NativeLibraryConfig.All.WithVulkan(opts.GpuLayers > 0).WithCuda(false).WithAutoFallback(true)
    .WithLogCallback((level, message) =>
    {
        // e.g. "using device Vulkan0 (NVIDIA GeForce RTX 4050 Laptop GPU) ..." tells us which GPU actually runs the model.
        var at = message.IndexOf("using device ", StringComparison.Ordinal);
        if (at >= 0 && message.IndexOf('(', at) is var open and > 0 && message.IndexOf(')', open) is var close and > 0)
            device = message[(open + 1)..close];
        if (level >= LLamaLogLevel.Error || Environment.GetEnvironmentVariable("RESMAN_LLAMA_LOG") == "1") Console.Error.Write(message);
    });

var sw = Stopwatch.StartNew();
var modelParams = new ModelParams(opts.ModelPath)
{
    ContextSize = (uint)opts.ContextSize,
    GpuLayerCount = opts.GpuLayers,
    // One GPU only: on laptops with an integrated + discrete GPU, splitting layers onto the iGPU is much slower.
    SplitMode = GPUSplitMode.None,
    MainGpu = 0,
    BatchSize = uint.TryParse(Environment.GetEnvironmentVariable("RESMAN_BATCH"), out var bsz) ? bsz : 2048,
    FlashAttention = Environment.GetEnvironmentVariable("RESMAN_FLASH_ATTN") == "1",
    UBatchSize = uint.TryParse(Environment.GetEnvironmentVariable("RESMAN_UBATCH"), out var ubsz) ? ubsz : 128, // measured: on this RTX 4050 + Vulkan, micro-batches of 256+ hit a ~6x slower path
    Threads = Math.Max(1, Environment.ProcessorCount / 2),
};
using var weights = await LLamaWeights.LoadFromFileAsync(modelParams);
using var engine = new Engine(weights, modelParams);
var loadMs = sw.Elapsed.TotalMilliseconds;
var backend = NativeApi.llama_supports_gpu_offload() && opts.GpuLayers > 0 ? $"gpu: {device ?? "vulkan"}" : "cpu";
Console.Error.WriteLine($"Loaded {Path.GetFileName(opts.ModelPath)} in {loadMs / 1000:0.0}s ({backend}).");
var idleSeconds = opts.IdleSeconds;
double? lastTokensPerSecond = null;
double? lastInferenceMs = null;
var requests = 0;

async Task<InferenceResponse> RunAsync(InferenceRequest req, CancellationToken ct)
{
    var t = Stopwatch.StartNew();
    try
    {
        var template = new LLamaTemplate(weights) { AddAssistant = true };
        template.Add("system", req.System);
        template.Add("user", req.User);
        var prompt = Encoding.UTF8.GetString(template.Apply());

        var g = await engine.GenerateAsync(prompt, req.Gbnf, Math.Clamp(req.MaxTokens, 16, 2048), req.Temperature, ct);
        var tokens = g.Tokens;
        Log($"prompt {g.PromptTokens} tokens ({g.ReusedTokens} reused from cache) decoded in {g.PromptMs:0} ms, first token {g.FirstTokenMs:0} ms, {g.Tokens} tokens in {g.TotalMs:0} ms, grammar {(req.Gbnf is null ? "no" : "yes")}");
        var text = g.Text.Replace("<|im_end|>", "").Replace("<|endoftext|>", "").Trim();
        lastInferenceMs = t.Elapsed.TotalMilliseconds;
        lastTokensPerSecond = g.Tokens / Math.Max(0.001, (g.TotalMs - g.FirstTokenMs) / 1000);
        requests++;
        return Stats(new InferenceResponse(text, null, t.Elapsed.TotalMilliseconds, tokens, backend));
    }
    catch (Exception ex)
    {
        return new InferenceResponse(null, ex.Message, t.Elapsed.TotalMilliseconds, 0, backend);
    }
}

InferenceResponse Stats(InferenceResponse r) => r with
{
    LoadMilliseconds = loadMs, Model = Path.GetFileName(opts.ModelPath), TokensPerSecond = lastTokensPerSecond,
    LastInferenceMilliseconds = lastInferenceMs, IdleSeconds = idleSeconds, Requests = requests, ContextSize = opts.ContextSize,
};

// Benchmark mode: prompt-processing and generation speed, with and without a grammar.
if (args.Contains("--bench"))
{
    var filler = string.Join(" ", Enumerable.Range(0, 700).Select(i => $"word{i % 50}"));
    const string json = "root ::= \"{\" \"\\\"a\\\"\" \":\" \"\\\"\" [a-z ]{1,200} \"\\\"\" \"}\"";
    foreach (var (label, gbnf, salt) in new[] { ("no grammar", (string?)null, "x"), ("grammar", json, "y"), ("no grammar again", null, "z") })
    {
        var r = await RunAsync(new InferenceRequest("Summarize.", $"{salt} {filler}", gbnf, 64), CancellationToken.None);
        Console.WriteLine($"{label}: {r.Error ?? "ok"}");
    }
    Console.WriteLine(File.ReadAllText(Path.Combine(ResManPaths.UserDir, "inference.log")).Split('\n').Where(l => l.Length > 0).TakeLast(3).Aggregate((a, b) => a + "\n" + b));
    return 0;
}

// Self-test mode: one prompt, print the result, exit.
if (opts.Prompt is not null)
{
    var r = await RunAsync(new InferenceRequest("You are a helpful assistant. Be brief.", opts.Prompt, null, 128), CancellationToken.None);
    Console.WriteLine(r.Error ?? r.Text);
    Console.Error.WriteLine($"{r.Tokens} tokens in {r.Milliseconds:0} ms ({backend})");
    return r.Error is null ? 0 : 1;
}

var pipeName = opts.PipeName ?? ResManPaths.InferencePipeName;
while (true)
{
    var idle = TimeSpan.FromSeconds(idleSeconds);
    var security = new PipeSecurity();
    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
    using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

    using var idleCts = new CancellationTokenSource(idle);
    try { await pipe.WaitForConnectionAsync(idleCts.Token); }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine($"Idle for {idle.TotalSeconds:0}s, exiting to free memory.");
        return 0;
    }

    // A client that disconnects mid-reply must never take the sidecar down (it did once; ResMan logged its own crash).
    try
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync();
        while (line is not null)
        {
            var req = JsonSerializer.Deserialize(line, ResManJson.Default.InferenceRequest);
            InferenceResponse resp;
            Task<string?>? next = null;
            if (req is null) resp = new InferenceResponse(null, "Empty request", 0, 0, backend);
            else if (req.Command == "stats") resp = Stats(new InferenceResponse("", null, 0, 0, backend));
            else if (req.Command == "keepalive")
            {
                // Pre-load for demos: stay warm for longer than the normal 30 s idle timeout.
                idleSeconds = Math.Clamp(req.IdleSeconds ?? opts.IdleSeconds, 10, 4 * 3600);
                resp = Stats(new InferenceResponse("", null, 0, 0, backend));
            }
            else
            {
                // Watch the pipe while generating: if the caller gives up (it closes the pipe), stop at once
                // instead of keeping the graphics card busy for an answer nobody will read.
                using var abandon = new CancellationTokenSource();
                next = reader.ReadLineAsync();
                var run = RunAsync(req, abandon.Token);
                if (await Task.WhenAny(run, next) == next && next.Result is null)
                {
                    abandon.Cancel();
                    await run;
                    Log("caller disconnected; generation stopped");
                    break;
                }
                resp = await run;
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(resp, ResManJson.Default.InferenceResponse));
            line = next is not null ? await next : await reader.ReadLineAsync();
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* client disconnected */ }
}

sealed record Options(string? ModelPath, string? PipeName, int IdleSeconds, int GpuLayers, int ContextSize, string? Prompt)
{
    public static Options Parse(string[] a)
    {
        string? Get(string k) { var i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        return new(Get("--model"), Get("--pipe"),
            int.TryParse(Get("--idle"), out var idle) ? idle : 30,
            int.TryParse(Get("--gpu-layers"), out var gl) ? gl : 99,
            int.TryParse(Get("--ctx"), out var ctx) ? ctx : 4096,
            Get("--prompt"));
    }
}
