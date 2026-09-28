namespace BugInvestigationAgent.Core.Knowledge;

public interface IKnowledgeIngestionService
{
    Task<int> IngestAsync(string rootPath, int chunkSizeChars, CancellationToken cancellationToken = default);
}

public interface IKnowledgeRetriever
{
    Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken = default);
}

public sealed class KnowledgeChunk
{
    public string Id { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string? Title { get; set; }
    public string Text { get; set; } = "";
    public string? ModuleHint { get; set; }
    public double Score { get; set; }
}

/// <summary>Optional embeddings provider — null/no-op when unavailable.</summary>
public interface IEmbeddingProvider
{
    bool IsAvailable { get; }
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
