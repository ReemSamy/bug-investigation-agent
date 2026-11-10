using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Sessions;
using BugInvestigationAgent.Core.Workflow;
using Microsoft.Extensions.Logging;

namespace BugInvestigationAgent.Core.Investigations;

public sealed class LegacyBatchOptions
{
    public TimeSpan TicketTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RecordWait { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed record BatchDashboard(
    int Total, int Pending, int Running, int Completed, int Inconclusive, int Blocked, int Failed,
    int StillReproducible, int ConfirmedFixed, int BranchesCreated);

/// <summary>
/// Resumable investigation of a frozen snapshot of a Jira filter, one ticket at a time.
/// Reads Jira only: nothing is posted and no issue is modified. The only write anywhere is a new local branch for a bug
/// reproduced with evidence (created by the investigation itself).
/// </summary>
public sealed class LegacyBatchService
{
    private readonly ILegacyBatchStore _batches;
    private readonly IInvestigationRecordStore _records;
    private readonly IJiraIssueGateway _jira;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAgentSessionStore _sessions;
    private readonly IEnvironmentSelectionService _environments;
    private readonly LegacyBatchOptions _options;
    private readonly ILogger<LegacyBatchService> _logger;
    private readonly HashSet<Guid> _running = new();
    private readonly object _gate = new();

    public LegacyBatchService(ILegacyBatchStore batches, IInvestigationRecordStore records, IJiraIssueGateway jira,
        IAgentOrchestrator orchestrator, IAgentSessionStore sessions, IEnvironmentSelectionService environments,
        LegacyBatchOptions options, ILogger<LegacyBatchService> logger)
    {
        _batches = batches;
        _records = records;
        _jira = jira;
        _orchestrator = orchestrator;
        _sessions = sessions;
        _environments = environments;
        _options = options;
        _logger = logger;
    }

    public bool IsRunning(Guid batchId)
    {
        lock (_gate)
            return _running.Contains(batchId);
    }

    /// <summary>Freezes the filter's current issues (all pages) as the batch scope. Production environments are refused.</summary>
    public async Task<LegacyBatch> CreateSnapshotAsync(string filterId, string environmentId, string? environmentUrl,
        Guid? credentialProfileId, string? createdBy, CancellationToken cancellationToken = default)
    {
        var env = _environments.Validate(environmentId, environmentUrl);
        if (!env.IsValid || env.Environment is null)
            throw new InvalidOperationException(env.Error ?? "Select a valid environment.");
        if (env.Environment.IsProduction)
            throw new InvalidOperationException($"{env.Environment.DisplayName} is a Production environment — batches only run on approved non-production environments.");

        var (jql, keys) = await _jira.GetFilterIssuesAsync(filterId, cancellationToken);
        var batch = new LegacyBatch
        {
            Name = $"Filter {filterId} — {keys.Count} issues — {DateTimeOffset.Now:yyyy-MM-dd HH:mm}",
            FilterId = filterId,
            Jql = jql,
            EnvironmentId = env.Environment.Id,
            EnvironmentUrl = environmentUrl,
            EnvironmentName = env.Environment.DisplayName,
            CredentialProfileId = credentialProfileId,
            CreatedBy = createdBy,
            Items = keys.Select((k, i) => new LegacyBatchItem { IssueKey = k, Position = i }).ToList()
        };
        await _batches.AddAsync(batch, cancellationToken);
        return batch;
    }

    /// <summary>Compares the filter's CURRENT members with the frozen snapshot (the snapshot is not changed).</summary>
    public async Task<(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)> CheckMembershipAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await _batches.GetAsync(batchId, cancellationToken) ?? throw new InvalidOperationException("Batch not found.");
        var (_, current) = await _jira.GetFilterIssuesAsync(batch.FilterId, cancellationToken);
        var snapshot = batch.Items.Select(i => i.IssueKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = current.Where(k => !snapshot.Contains(k)).ToList();
        var removed = batch.Items.Select(i => i.IssueKey).Where(k => !now.Contains(k)).ToList();
        batch.LastMembershipCheckAt = DateTimeOffset.UtcNow;
        batch.LastMembershipChange = added.Count == 0 && removed.Count == 0
            ? "No change since the snapshot."
            : $"Added since snapshot: {(added.Count == 0 ? "none" : string.Join(", ", added))}. Removed since snapshot: {(removed.Count == 0 ? "none" : string.Join(", ", removed))}.";
        await _batches.UpdateBatchAsync(batch, cancellationToken);
        return (added, removed);
    }

    public async Task<BatchDashboard> GetDashboardAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await _batches.GetAsync(batchId, cancellationToken) ?? throw new InvalidOperationException("Batch not found.");
        var latest = await LatestRecordsAsync(batch, cancellationToken);
        int Count(InvestigationExecutionStatus s) => batch.Items.Count(i => i.Status == s);
        return new BatchDashboard(batch.Items.Count,
            Count(InvestigationExecutionStatus.Pending), Count(InvestigationExecutionStatus.Running),
            Count(InvestigationExecutionStatus.Completed), Count(InvestigationExecutionStatus.Inconclusive),
            Count(InvestigationExecutionStatus.Blocked), Count(InvestigationExecutionStatus.Failed),
            latest.Values.Count(r => r.Assessment == BugAssessment.StillExists),
            latest.Values.Count(r => r.Assessment == BugAssessment.ConfirmedFixed),
            latest.Values.Count(r => !string.IsNullOrWhiteSpace(r.CreatedBranch)));
    }

    /// <summary>Latest record per batch item (by the item's record link).</summary>
    public async Task<IReadOnlyDictionary<string, InvestigationRecord>> LatestRecordsAsync(LegacyBatch batch, CancellationToken cancellationToken = default)
    {
        var records = (await _records.ListAsync(batch.Id, cancellationToken)).ToDictionary(r => r.Id);
        return batch.Items
            .Where(i => i.RecordId is Guid id && records.ContainsKey(id))
            .ToDictionary(i => i.IssueKey, i => records[i.RecordId!.Value], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Processes Pending items (and Failed/Blocked ones when <paramref name="retryUnsuccessful"/>), one at a time.
    /// Completed and Inconclusive items are never investigated twice. Safe to call again after a restart.
    /// </summary>
    /// <param name="maxItems">Stop after this many tickets (null = all eligible tickets).</param>
    /// <param name="rerunInvestigated">Investigate again tickets that already have a finished investigation (this or any other batch).</param>
    public async Task RunAsync(Guid batchId, bool retryUnsuccessful, int? maxItems = null, CancellationToken cancellationToken = default,
        bool rerunInvestigated = false)
    {
        lock (_gate)
        {
            if (!_running.Add(batchId))
                throw new InvalidOperationException("This batch is already running.");
        }
        try
        {
            var batch = await _batches.GetAsync(batchId, cancellationToken) ?? throw new InvalidOperationException("Batch not found.");
            // An item left "Running" by a previous process (restart/crash) is picked up again.
            foreach (var stale in batch.Items.Where(i => i.Status == InvestigationExecutionStatus.Running))
            {
                stale.Status = InvestigationExecutionStatus.Pending;
                stale.LastError = "Interrupted (application restart) — re-queued.";
                await _batches.UpdateItemAsync(stale, cancellationToken);
            }

            // Never investigate the same ticket twice unless explicitly requested: finished investigations from any batch
            // (or the Workflow page) count.
            var finished = (await _records.ListAsync(null, cancellationToken))
                .Where(r => r.ExecutionStatus is InvestigationExecutionStatus.Completed or InvestigationExecutionStatus.Inconclusive)
                .GroupBy(r => r.IssueKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAt).First(), StringComparer.OrdinalIgnoreCase);

            var processed = 0;
            foreach (var item in batch.Items.OrderBy(i => i.Position))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (maxItems is int max && processed >= max)
                    break;
                var eligible = item.Status == InvestigationExecutionStatus.Pending ||
                               (retryUnsuccessful && item.Status is InvestigationExecutionStatus.Failed or InvestigationExecutionStatus.Blocked) ||
                               (rerunInvestigated && item.Status is InvestigationExecutionStatus.Completed or InvestigationExecutionStatus.Inconclusive);
                if (!eligible)
                    continue;
                if (!rerunInvestigated && finished.TryGetValue(item.IssueKey, out var earlier) && earlier.BatchId != batch.Id)
                {
                    item.Status = earlier.ExecutionStatus;
                    item.RecordId = earlier.Id;
                    item.LastError = $"Already investigated on {earlier.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} — skipped (tick re-run to investigate again).";
                    await _batches.UpdateItemAsync(item, cancellationToken);
                    continue;
                }
                await RunItemAsync(batch, item, cancellationToken);
                processed++;
            }
        }
        finally
        {
            lock (_gate)
                _running.Remove(batchId);
        }
    }

    private async Task RunItemAsync(LegacyBatch batch, LegacyBatchItem item, CancellationToken cancellationToken)
    {
        item.Status = InvestigationExecutionStatus.Running;
        item.Attempts++;
        item.LastError = null;
        await _batches.UpdateItemAsync(item, cancellationToken);

        var session = _sessions.Create();
        var started = DateTimeOffset.UtcNow;
        try
        {
            session.Context.CustomFields[InvestigationRecorder.BatchIdField] = batch.Id.ToString();
            await _orchestrator.StartAsync(session.Id, item.IssueKey, null, cancellationToken);
            // Set after StartAsync: starting a run resets the environment/credential selection.
            session.Context.CredentialProfileId = batch.CredentialProfileId;

            var deadline = DateTimeOffset.UtcNow + _options.TicketTimeout;
            var environmentSubmitted = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = session.State;
                if (state is AgentState.Completed or AgentState.Failed or AgentState.Blocked)
                    break;
                if (state == AgentState.AwaitingApproval)
                {
                    await _orchestrator.ApproveFixAsync(session.Id, approve: false, cancellationToken); // batches never apply fixes
                }
                else if (state == AgentState.AwaitingMissingInfo)
                {
                    if (environmentSubmitted)
                    {
                        // Second prompt (rejected login, MFA, missing data): needs a person — record as Blocked.
                        session.Context.FinalStatusMessage = "Blocked: " + (session.Context.MissingInfoReason ?? "the investigation needs input from a person.");
                        await _orchestrator.CancelAsync(session.Id);
                        break;
                    }
                    environmentSubmitted = true;
                    var values = new Dictionary<string, string> { ["environmentId"] = batch.EnvironmentId };
                    if (!string.IsNullOrWhiteSpace(batch.EnvironmentUrl))
                        values["environmentUrl"] = batch.EnvironmentUrl!;
                    await _orchestrator.SubmitMissingInfoAsync(session.Id, values, newCredentials: null, saveCredentialProfile: false, cancellationToken);
                }
                if (DateTimeOffset.UtcNow > deadline)
                {
                    session.Context.FinalStatusMessage = $"Blocked: no result within {_options.TicketTimeout.TotalMinutes:0} minutes.";
                    await _orchestrator.CancelAsync(session.Id);
                    break;
                }
                await Task.Delay(_options.PollInterval, cancellationToken);
            }

            // The record is written by the completion sink when the run ends.
            var record = await WaitForRecordAsync(session.Id, started, cancellationToken);
            if (record is null)
            {
                item.Status = InvestigationExecutionStatus.Failed;
                item.LastError = $"No investigation record was saved (final state {session.State}).";
            }
            else
            {
                item.RecordId = record.Id;
                item.Status = record.ExecutionStatus;
                item.LastError = record.ExecutionStatus is InvestigationExecutionStatus.Failed or InvestigationExecutionStatus.Blocked
                    ? record.ExecutionDetail
                    : null;
            }
        }
        catch (OperationCanceledException)
        {
            await _orchestrator.CancelAsync(session.Id);
            item.Status = InvestigationExecutionStatus.Pending;
            item.LastError = "Batch stopped — will resume here.";
            await _batches.UpdateItemAsync(item, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Batch item {Issue} failed: {Error}", item.IssueKey, ex.Message);
            item.Status = InvestigationExecutionStatus.Failed;
            item.LastError = ex.Message;
        }
        finally
        {
            _sessions.Remove(session.Id);
        }
        await _batches.UpdateItemAsync(item, cancellationToken);
    }

    private async Task<InvestigationRecord?> WaitForRecordAsync(Guid investigationId, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.RecordWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var record = await _records.FindByInvestigationAsync(investigationId, notBefore, cancellationToken);
            if (record is not null)
                return record;
            await Task.Delay(_options.PollInterval, cancellationToken);
        }
        return null;
    }
}
