namespace BugInvestigationAgent.Core.Jobs;

public interface IInvestigationJobStore
{
    Task<InvestigationJob> CreateAsync(InvestigationJob job, CancellationToken cancellationToken = default);
    Task<InvestigationJob?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Job for a Rovo conversation, only if it belongs to the given tenant and account (user isolation).</summary>
    Task<InvestigationJob?> FindByContextAsync(string tenantId, string contextId, string accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvestigationJob>> ListAwaitingInputForAccountAsync(string tenantId, string accountId, CancellationToken cancellationToken = default);
    /// <summary>Applies a mutation and saves (optimistic concurrency with retry).</summary>
    Task<InvestigationJob?> UpdateAsync(Guid id, Action<InvestigationJob> mutate, CancellationToken cancellationToken = default);
    Task AppendEventAsync(Guid jobId, string kind, string message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvestigationJobEvent>> ListEventsAsync(Guid jobId, int max = 50, CancellationToken cancellationToken = default);

    /// <summary>Atomically claims the job for a worker if unleased or the previous lease expired.</summary>
    Task<bool> TryAcquireLeaseAsync(Guid jobId, string owner, TimeSpan duration, CancellationToken cancellationToken = default);
    Task<bool> RenewLeaseAsync(Guid jobId, string owner, TimeSpan duration, CancellationToken cancellationToken = default);
    Task ReleaseLeaseAsync(Guid jobId, string owner, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ListRunnableAsync(int max, CancellationToken cancellationToken = default);
    /// <summary>Non-terminal jobs whose worker lease expired (worker crashed) — recovery candidates.</summary>
    Task<IReadOnlyList<Guid>> ListOrphanedAsync(int max, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ListUnpublishedCompletedAsync(int max, CancellationToken cancellationToken = default);

    Task UpsertInstallationAsync(JiraInstallation installation, CancellationToken cancellationToken = default);
    Task<JiraInstallation?> GetInstallationAsync(string installationId, CancellationToken cancellationToken = default);
}

/// <summary>Work queue of job ids. Exclusivity is always enforced by the job lease, so at-least-once delivery is fine.</summary>
public interface IInvestigationQueue
{
    Task EnqueueAsync(Guid jobId, TimeSpan? delay = null, CancellationToken cancellationToken = default);
    /// <summary>Waits for the next job id (null on timeout).</summary>
    Task<Guid?> DequeueAsync(TimeSpan wait, CancellationToken cancellationToken = default);
}

/// <summary>
/// Short-lived, encrypted, per-job secrets: the invoking user's Jira token, the app system token,
/// environment credentials typed for this investigation and MFA codes. Purpose-bound to job + kind; deleted when the job ends.
/// </summary>
public interface IJobSecretStore
{
    Task SetAsync(Guid jobId, string kind, string value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(Guid jobId, string kind, CancellationToken cancellationToken = default);
    /// <summary>Returns and deletes the secret (single use).</summary>
    Task<string?> TakeAsync(Guid jobId, string kind, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid jobId, string kind, CancellationToken cancellationToken = default);
    /// <summary>Deletes every secret of the job except the listed kinds (e.g. keep the app token until the report is published).</summary>
    Task DeleteAllAsync(Guid jobId, IReadOnlyCollection<string>? keepKinds = null, CancellationToken cancellationToken = default);
    Task PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

public static class JobSecretKinds
{
    public const string JiraUserToken = "jira-user-token";
    public const string JiraSystemToken = "jira-system-token";
    public const string MfaCode = "mfa-code";
    /// <summary>Hash of a one-time ticket that authorizes a direct credential/MFA submission for this job.</summary>
    public const string SubmissionTicket = "submission-ticket";
    public static string EnvironmentCredentials(string environmentId) => "env-credentials:" + environmentId.Trim().ToUpperInvariant();
}

public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEntry>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>Durable evidence storage (local folder or Azure Blob Storage).</summary>
public interface IEvidenceStore
{
    /// <summary>Uploads the files of an investigation's local evidence folder; returns a stable reference.</summary>
    Task<string> PublishAsync(Guid jobId, string localDirectory, CancellationToken cancellationToken = default);
    /// <summary>Opens one evidence file of a job (null if missing). File name only — no paths.</summary>
    Task<Stream?> OpenReadAsync(Guid jobId, string fileName, CancellationToken cancellationToken = default);
}

/// <summary>Immutable identity of the job being executed (validated tenant, site and invoking user).</summary>
public sealed record HostedJobIdentity(
    Guid JobId,
    string TenantId,
    string CloudId,
    string? ApiBaseUrl,
    string AccountId,
    string IssueId,
    string IssueKey,
    string? SiteUrl = null)
{
    /// <summary>Credential-vault owner for this user on this Jira site — isolates saved credentials per user.</summary>
    public string OwnerId => $"atlassian:{CloudId}:{AccountId}";

    public static HostedJobIdentity From(InvestigationJob job) =>
        new(job.Id, job.TenantId, job.CloudId, job.ApiBaseUrl, job.InvokingAccountId, job.IssueId, job.IssueKey, job.SiteUrl);
}

/// <summary>
/// Ambient per-job identity for hosted (Jira) mode. Flows with the async execution of ONE job, so concurrent jobs in
/// the same worker never see each other's identity. Null in the standalone UI.
/// </summary>
public static class HostedJobScope
{
    private static readonly AsyncLocal<HostedJobIdentity?> CurrentIdentity = new();

    public static HostedJobIdentity? Current => CurrentIdentity.Value;

    public static IDisposable Enter(HostedJobIdentity identity)
    {
        var previous = CurrentIdentity.Value;
        CurrentIdentity.Value = identity;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly HostedJobIdentity? _previous;
        public Restore(HostedJobIdentity? previous) => _previous = previous;
        public void Dispose() => CurrentIdentity.Value = _previous;
    }
}
