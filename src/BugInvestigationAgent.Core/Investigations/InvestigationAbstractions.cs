using BugInvestigationAgent.Core.Sessions;

namespace BugInvestigationAgent.Core.Investigations;

public interface IInvestigationRecordStore
{
    Task AddAsync(InvestigationRecord record, CancellationToken cancellationToken = default);
    Task<InvestigationRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<InvestigationRecord?> FindByInvestigationAsync(Guid investigationId, DateTimeOffset notBefore, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvestigationRecord>> ListAsync(Guid? batchId = null, CancellationToken cancellationToken = default);
    /// <summary>Saves changes when <paramref name="record"/>.Version still matches the stored row; false if someone else changed it.</summary>
    Task<bool> TryUpdateAsync(InvestigationRecord record, CancellationToken cancellationToken = default);
}

public interface ILegacyBatchStore
{
    Task AddAsync(LegacyBatch batch, CancellationToken cancellationToken = default);
    Task<LegacyBatch?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LegacyBatch>> ListAsync(CancellationToken cancellationToken = default);
    Task UpdateItemAsync(LegacyBatchItem item, CancellationToken cancellationToken = default);
    Task UpdateBatchAsync(LegacyBatch batch, CancellationToken cancellationToken = default);
}

/// <summary>Material fields of a Jira issue, read with the user's own permissions.</summary>
public sealed record JiraIssueSnapshot(
    string Key,
    string? Summary,
    string? Status,
    string? Resolution,
    string? Priority,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    IReadOnlyList<string> Labels,
    /// <summary>SHA-256 of summary + description + status + resolution (comments and watchers are ignored).</summary>
    string Fingerprint);

/// <summary>
/// The only Jira access used by records and batches. READ-ONLY: the legacy investigation workflow has no way to add
/// comments or change any Jira field.
/// </summary>
public interface IJiraIssueGateway
{
    Task<JiraIssueSnapshot?> GetSnapshotAsync(string issueKey, CancellationToken cancellationToken = default);
    /// <summary>Saved filter → JQL and every matching issue key (all pages).</summary>
    Task<(string Jql, IReadOnlyList<string> Keys)> GetFilterIssuesAsync(string filterId, CancellationToken cancellationToken = default);
}

/// <summary>Called once when an investigation run ends (completed, failed, blocked or cancelled).</summary>
public interface IInvestigationCompletionSink
{
    Task OnInvestigationFinishedAsync(AgentSession session, CancellationToken cancellationToken = default);
}
