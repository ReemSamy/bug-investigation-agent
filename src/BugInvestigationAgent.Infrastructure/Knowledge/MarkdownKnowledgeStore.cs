using System.Text;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Knowledge;
using Microsoft.Extensions.Logging;

namespace BugInvestigationAgent.Infrastructure.Knowledge;

/// <summary>
/// File-backed markdown chunk index with lexical retrieval (no required embedding API).
/// Suitable as V1 RAG for Visium KMS docs cloned from ADO.
/// </summary>
public sealed class MarkdownKnowledgeStore : IKnowledgeIngestionService, IKnowledgeRetriever
{
    private readonly object _gate = new();
    private readonly List<KnowledgeChunk> _chunks = new();
    private readonly ILogger<MarkdownKnowledgeStore> _logger;

    public MarkdownKnowledgeStore(ILogger<MarkdownKnowledgeStore> logger) => _logger = logger;

    public Task<int> IngestAsync(string rootPath, int chunkSizeChars, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            _logger.LogInformation("KMS docs root missing or empty: {Path}", rootPath);
            lock (_gate) _chunks.Clear();
            return Task.FromResult(0);
        }

        var size = Math.Clamp(chunkSizeChars, 400, 4000);
        var built = new List<KnowledgeChunk>();
        foreach (var file in Directory.EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var rel = Path.GetRelativePath(rootPath, file);
            var title = ExtractTitle(text) ?? Path.GetFileNameWithoutExtension(file);
            var moduleHint = GuessModule(rel + " " + title);
            foreach (var (idx, piece) in ChunkText(text, size).Select((p, i) => (i, p)))
            {
                built.Add(new KnowledgeChunk
                {
                    Id = $"{rel}#{idx}",
                    SourcePath = rel,
                    Title = title,
                    Text = piece,
                    ModuleHint = moduleHint
                });
            }
        }

        lock (_gate)
        {
            _chunks.Clear();
            _chunks.AddRange(built);
        }

        _logger.LogInformation("Ingested {Count} KMS doc chunks from {Path}", built.Count, rootPath);
        return Task.FromResult(built.Count);
    }

    public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken = default)
    {
        var terms = Tokenize(query);
        List<KnowledgeChunk> snapshot;
        lock (_gate) snapshot = _chunks.ToList();

        if (snapshot.Count == 0 || terms.Count == 0)
            return Task.FromResult<IReadOnlyList<KnowledgeChunk>>(Array.Empty<KnowledgeChunk>());

        var ranked = snapshot
            .Select(c =>
            {
                var hay = $"{c.Title}\n{c.ModuleHint}\n{c.Text}";
                var score = terms.Sum(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0);
                if (!string.IsNullOrWhiteSpace(c.ModuleHint) &&
                    terms.Any(t => c.ModuleHint.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    score += 1.5;
                return (Chunk: c, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(Math.Clamp(topK, 1, 20))
            .Select(x =>
            {
                x.Chunk.Score = x.Score;
                return x.Chunk;
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<KnowledgeChunk>>(ranked);
    }

    private static IEnumerable<string> ChunkText(string text, int size)
    {
        var normalized = Regex.Replace(text.Replace("\r\n", "\n"), @"\n{3,}", "\n\n").Trim();
        if (normalized.Length <= size)
        {
            yield return normalized;
            yield break;
        }

        for (var i = 0; i < normalized.Length; i += size - 100)
        {
            var len = Math.Min(size, normalized.Length - i);
            yield return normalized.Substring(i, len);
            if (i + len >= normalized.Length)
                yield break;
        }
    }

    private static string? ExtractTitle(string text)
    {
        var m = Regex.Match(text, @"^#\s+(.+)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? GuessModule(string corpus)
    {
        string[] modules =
        [
            "Administration", "Auditor", "Investigator", "Incident", "Tracker", "Checklist",
            "Risk Assessor", "MOC", "OII", "Recurring", "Security", "Persons", "Workflow"
        ];
        return modules.FirstOrDefault(m => corpus.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Tokenize(string query) =>
        Regex.Matches(query ?? "", @"\b[A-Za-z][A-Za-z0-9_-]{2,}\b")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
}

public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public bool IsAvailable => false;
    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult<float[]?>(null);
}
