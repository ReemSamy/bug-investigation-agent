namespace BugInvestigationAgent.Core.Abstractions;

/// <summary>
/// Provider-agnostic AI runtime adapter. Core depends only on this interface.
/// </summary>
public interface IAgentRuntime
{
    string ProviderId { get; }

    Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentTurnRequest request, CancellationToken cancellationToken = default);
}

public sealed class AgentTurnRequest
{
    public required IReadOnlyList<AgentMessage> Messages { get; init; }
    public string? SystemPrompt { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int? MaxTokens { get; init; }
    /// <summary>Ask the provider to reply with a single JSON object (OpenAI/Ollama "json_object" mode) when it supports it.</summary>
    public bool JsonOnly { get; init; }
}

public sealed class AgentMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public sealed class AgentTurnResult
{
    public required string Content { get; init; }
    public string? FinishReason { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed class AgentStreamEvent
{
    public required string Kind { get; init; }
    public string? TextDelta { get; init; }
}
