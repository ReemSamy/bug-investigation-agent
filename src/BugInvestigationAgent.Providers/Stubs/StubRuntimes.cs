using System.Runtime.CompilerServices;
using BugInvestigationAgent.Core.Abstractions;

namespace BugInvestigationAgent.Providers.Stubs;

/// <summary>
/// Placeholder adapter for Azure OpenAI. Wire a real implementation later without changing Core.
/// </summary>
public sealed class AzureOpenAiRuntime : IAgentRuntime
{
    public const string Id = "azure-openai";
    public string ProviderId => Id;

    public Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Azure OpenAI adapter is registered as a stub. Configure OpenAI-compatible or Anthropic for V1, or implement this adapter.");

    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new NotSupportedException("Azure OpenAI adapter is registered as a stub.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

/// <summary>
/// Placeholder adapter for Cursor agent runtime.
/// </summary>
public sealed class CursorRuntime : IAgentRuntime
{
    public const string Id = "cursor";
    public string ProviderId => Id;

    public Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Cursor adapter is registered as a stub for future use. Select openai-compatible or anthropic for V1.");

    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new NotSupportedException("Cursor adapter is registered as a stub.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

/// <summary>
/// Offline fallback used when no API key is configured — keeps the UI pipeline testable.
/// </summary>
public sealed class NullAgentRuntime : IAgentRuntime
{
    public const string Id = "null";
    public string ProviderId => Id;

    public Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default)
    {
        var lastUser = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var content = lastUser.Contains("root cause", StringComparison.OrdinalIgnoreCase)
            ? """
              {
                "rootCause": "Likely defect in the code path matching the reproduction steps. Manual review recommended when no LLM key is configured.",
                "proposedFixSummary": "Add targeted null/state handling in the implicated module and cover with a focused test.",
                "proposedFiles": [],
                "proposedDiff": ""
              }
              """
            : "No LLM provider API key configured. Using null runtime placeholder response.";
        return Task.FromResult(new AgentTurnResult { Content = content, FinishReason = "stop" });
    }

    public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await CompleteAsync(request, cancellationToken);
        yield return new AgentStreamEvent { Kind = "delta", TextDelta = result.Content };
        yield return new AgentStreamEvent { Kind = "done" };
    }
}
