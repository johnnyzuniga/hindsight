using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ResMan.Core;

namespace ResMan.Brain.Knowledge;

public sealed record ProcessInfo(string Name, string Title, string Publisher, string What, bool SafeToLimit, string Notes);

public sealed record Fix(string Label, string? Uri = null);

public sealed record Signature(
    string Id,
    string Title,
    string[] Kinds,
    int MinCount,
    double WindowHours,
    string Severity,
    string Explanation,
    string[] LikelyCauses,
    Fix[] Fixes,
    bool NeedsHuman,
    bool Noise = false,
    bool SameApp = false,
    string[]? Providers = null,
    int[]? EventIds = null,
    Dictionary<string, string>? DataEquals = null,
    Dictionary<string, double>? DataGreaterThan = null,
    string[]? Supersedes = null)
{
    [JsonIgnore]
    public Severity SeverityLevel => Enum.TryParse<Severity>(Severity, true, out var s) ? s : Core.Severity.Info;
}

public sealed record KbHit(string Type, string Id, string Title, string Text, double Rank);

/// <summary>
/// The curated knowledge base (processes and problem signatures), indexed with SQLite FTS5 in memory.
/// Exact process-name lookups come first; full-text search is the fuzzy fallback.
/// </summary>
public sealed partial class KnowledgeBase : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
    private readonly SqliteConnection _index;
    private readonly Dictionary<string, ProcessInfo> _processes;

    public IReadOnlyList<Signature> Signatures { get; }
    public IReadOnlyCollection<ProcessInfo> Processes => _processes.Values;

    public KnowledgeBase(IEnumerable<ProcessInfo> processes, IEnumerable<Signature> signatures)
    {
        _processes = processes.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        Signatures = signatures.ToList();
        _index = new SqliteConnection("Data Source=:memory:");
        _index.Open();
        using var cmd = _index.CreateCommand();
        cmd.CommandText = "CREATE VIRTUAL TABLE kb USING fts5(type UNINDEXED, id UNINDEXED, title, body, tokenize='porter unicode61');";
        cmd.ExecuteNonQuery();
        foreach (var p in _processes.Values)
            Add("process", p.Name, $"{p.Title} ({p.Name})", $"{p.What} {p.Notes} Publisher: {p.Publisher}.");
        foreach (var s in Signatures.Where(s => !s.Noise))
            Add("signature", s.Id, s.Title, $"{s.Explanation} Causes: {string.Join("; ", s.LikelyCauses)}. Fixes: {string.Join("; ", s.Fixes.Select(f => f.Label))}.");
    }

    public static KnowledgeBase Load(string kbDir)
    {
        var processes = ReadAll<ProcessInfo>(Path.Combine(kbDir, "processes"));
        var signatures = ReadAll<Signature>(Path.Combine(kbDir, "signatures"));
        return new KnowledgeBase(processes, signatures);
    }

    /// <summary>Finds the kb/ folder next to the app (installed) or in the repo (development).</summary>
    public static KnowledgeBase LoadDefault()
    {
        var dir = Environment.GetEnvironmentVariable("RESMAN_KB_DIR") ?? ResManPaths.FindUp("kb")
                  ?? throw new DirectoryNotFoundException("Couldn't find the kb/ folder.");
        return Load(dir);
    }

    private static List<T> ReadAll<T>(string dir) =>
        !Directory.Exists(dir) ? [] :
        Directory.EnumerateFiles(dir, "*.json").SelectMany(f => JsonSerializer.Deserialize<List<T>>(File.ReadAllText(f), Json) ?? []).ToList();

    private void Add(string type, string id, string title, string body)
    {
        using var cmd = _index.CreateCommand();
        cmd.CommandText = "INSERT INTO kb(type,id,title,body) VALUES($t,$i,$ti,$b)";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$ti", title);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.ExecuteNonQuery();
    }

    public ProcessInfo? LookupProcess(string name) =>
        _processes.GetValueOrDefault(RuleValidator.NormalizeProcessName(name));

    /// <summary>Friendly app name ("chrome.exe" becomes "Google Chrome") when known.</summary>
    public string FriendlyName(string? exeOrName) =>
        exeOrName is null ? "An app" : LookupProcess(exeOrName)?.Title ?? RuleValidator.NormalizeProcessName(exeOrName);

    public Signature? GetSignature(string id) => Signatures.FirstOrDefault(s => s.Id == id);

    [GeneratedRegex(@"[A-Za-z0-9]{2,}")]
    private static partial Regex Words();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    { "my", "the", "and", "is", "it", "pc", "computer", "laptop", "when", "why", "does", "keeps", "keep", "to", "of", "a", "an", "in", "on", "so", "very", "really", "what", "how" };

    public IReadOnlyList<KbHit> Search(string query, int k = 3)
    {
        var exact = LookupProcess(query.Trim());
        var hits = new List<KbHit>();
        if (exact is not null) hits.Add(new("process", exact.Name, exact.Title, $"{exact.What} {exact.Notes}", 100));

        var terms = Words().Matches(query).Select(m => m.Value).Where(w => !StopWords.Contains(w)).Distinct().ToList();
        if (terms.Count == 0) return hits;
        using var cmd = _index.CreateCommand();
        cmd.CommandText = "SELECT type,id,title,body,bm25(kb) FROM kb WHERE kb MATCH $q ORDER BY bm25(kb) LIMIT $k";
        cmd.Parameters.AddWithValue("$q", string.Join(" OR ", terms.Select(t => $"\"{t}\"")));
        cmd.Parameters.AddWithValue("$k", k);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (hits.All(h => h.Id != r.GetString(1)))
                hits.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), -r.GetDouble(4)));
        return hits.Take(k).ToList();
    }

    public void Dispose() => _index.Dispose();
}
