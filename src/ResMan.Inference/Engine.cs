using System.Diagnostics;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace ResMan.Inference;

public sealed record Generation(string Text, int Tokens, int PromptTokens, int ReusedTokens, double FirstTokenMs, double TotalMs, double PromptMs = 0);

/// <summary>
/// One long-lived llama.cpp context with prompt-prefix caching. The agent loop sends a growing prompt
/// (same instructions, more evidence each step), so only the new part has to be processed each time.
/// </summary>
public sealed class Engine(LLamaWeights weights, ModelParams parameters) : IDisposable
{
    private readonly LLamaContext _context = weights.CreateContext(parameters);
    private readonly List<LLamaToken> _cached = [];
    private readonly int _batchSize = (int)(parameters.BatchSize);

    public int ContextSize => (int)_context.ContextSize;

    public async Task<Generation> GenerateAsync(string prompt, string? gbnf, int maxTokens, float temperature, CancellationToken ct)
    {
        var t = Stopwatch.StartNew();
        var tokens = _context.Tokenize(prompt, addBos: false, special: true);
        if (tokens.Length + maxTokens > ContextSize)
            throw new InvalidOperationException($"Prompt too long: {tokens.Length} tokens + {maxTokens} to generate exceeds the {ContextSize}-token context.");

        // Keep the longest shared prefix in the KV cache; always re-decode at least the last token to get fresh logits.
        var common = 0;
        while (common < _cached.Count && common < tokens.Length && (int)_cached[common] == (int)tokens[common]) common++;
        if (common == tokens.Length) common--;
        _context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, common, -1);
        _cached.RemoveRange(common, _cached.Count - common);

        var batch = new LLamaBatch();
        for (var i = common; i < tokens.Length; i += _batchSize)
        {
            batch.Clear();
            var end = Math.Min(tokens.Length, i + _batchSize);
            for (var j = i; j < end; j++) batch.Add(tokens[j], j, LLamaSeqId.Zero, j == tokens.Length - 1);
            var r = await _context.DecodeAsync(batch, ct);
            if (r != DecodeResult.Ok) { Reset(); throw new InvalidOperationException($"Prompt decode failed: {r}"); }
        }
        for (var j = common; j < tokens.Length; j++) _cached.Add(tokens[j]);
        _context.NativeHandle.Synchronize(); // decode is async on the GPU; wait so timings are honest
        var promptMs = t.Elapsed.TotalMilliseconds;

        using var sampler = new DefaultSamplingPipeline
        {
            Temperature = temperature,
            Grammar = string.IsNullOrWhiteSpace(gbnf) ? null : new Grammar(gbnf, "root"),
        };
        var decoder = new StreamingTokenDecoder(_context);
        var logitsIndex = batch.TokenCount - 1;
        var pos = tokens.Length;
        var generated = 0;
        double firstTokenMs = 0;
        while (generated < maxTokens)
        {
            ct.ThrowIfCancellationRequested();
            var token = sampler.Sample(_context.NativeHandle, logitsIndex);
            if (generated == 0) firstTokenMs = t.Elapsed.TotalMilliseconds;
            if (token.IsEndOfGeneration(weights.Vocab)) break;
            decoder.Add(token);
            generated++;
            batch.Clear();
            batch.Add(token, pos, LLamaSeqId.Zero, true);
            var r = await _context.DecodeAsync(batch, ct);
            if (r != DecodeResult.Ok) { Reset(); throw new InvalidOperationException($"Decode failed: {r}"); }
            _cached.Add(token);
            pos++;
            logitsIndex = 0;
        }
        return new Generation(decoder.Read(), generated, tokens.Length, common, firstTokenMs, t.Elapsed.TotalMilliseconds, promptMs);
    }

    private void Reset()
    {
        _context.NativeHandle.MemoryClear(true);
        _cached.Clear();
    }

    public void Dispose() => _context.Dispose();
}
