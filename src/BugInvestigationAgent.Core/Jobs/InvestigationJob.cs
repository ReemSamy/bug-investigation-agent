namespace BugInvestigationAgent.Core.Jobs;

public enum InvestigationJobStatus
{
    Queued = 0,
    Running,
    AwaitingUserInput,
    Completed,
    Failed,
    Cancelled,
    TimedOut
}

/// <summary>What the job is waiting for while <see cref="InvestigationJobStatus.AwaitingUserInput"/>.</summary>
public static class AwaitingInputKinds
{
    public const string Environment = "environment";
    public const string Credentials = "credentials";
    public const string MfaCode = "mfa-code";
    public const string MfaApproval = "mfa-approval";
    public const string Repository = "repository";
}

/// <summary>
/// Durable investigation job (one per Jira invocation). Persisted in the database; workers lease it while running.
/// Never contains secrets — tokens and environment credentials live encrypted in the job secret store.
/// </summary>
public sealed class InvestigationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // ── Jira / tenant identity (validated from the Forge Invocation Token, never from the request body) ──
    public string TenantId { get; set; } = "";          // Forge installationId
    public string CloudId { get; set; } = "";
    public string? SiteUrl { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string IssueId { get; set; } = "";
    public string IssueKey { get; set; } = "";
    public string InvokingAccountId { get; set; } = "";
    public string? InvocationType { get; set; }
    /// <summary>Forge environment id from the invocation token (for settings-page links).</summary>
    public string? ForgeEnvironmentId { get; set; }
    /// <summary>A2A context id (Rovo conversation) — follow-up replies are routed to the job by it.</summary>
    public string? ContextId { get; set; }

    // ── Selection ──
    public string? ApplicationId { get; set; }
    public string? EnvironmentId { get; set; }
    public bool AllowStateChangingActions { get; set; }

    // ── Lifecycle ──
    public InvestigationJobStatus Status { get; set; } = InvestigationJobStatus.Queued;
    public string? StatusMessage { get; set; }
    public string? AwaitingInputKind { get; set; }
    public string? AwaitingInputPrompt { get; set; }
    /// <summary>Latest non-secret chat reply from the invoking user, consumed by the worker.</summary>
    public string? PendingUserReply { get; set; }
    public bool CancelRequested { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Earliest time a worker may pick the job up (retry back-off), as Unix ms (queryable on SQLite).</summary>
    public long NotBeforeUnixMs { get; set; }
    public long CreatedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── Worker lease ──
    public string? LeaseOwner { get; set; }
    public long LeaseExpiresUnixMs { get; set; }

    // ── Retry / result ──
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 2;
    public string? ReproductionOutcome { get; set; }
    /// <summary>Markdown report shown in the Jira agent task.</summary>
    public string? ResultSummary { get; set; }
    /// <summary>Structured report (JSON) — source for the Jira comment.</summary>
    public string? ReportJson { get; set; }
    /// <summary>Where evidence is stored (blob prefix or local directory).</summary>
    public string? EvidenceReference { get; set; }
    public string? ErrorDetails { get; set; }
    public string? ReportCommentId { get; set; }
    public DateTimeOffset? ReportPublishedAt { get; set; }
    public int Version { get; set; }

    public bool IsTerminal => Status is InvestigationJobStatus.Completed or InvestigationJobStatus.Failed
        or InvestigationJobStatus.Cancelled or InvestigationJobStatus.TimedOut;
}

public sealed class InvestigationJobEvent
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Kind { get; set; } = "progress";
    public string Message { get; set; } = "";
}

/// <summary>Registered Jira Cloud site (from the Forge installed event / validated invocations).</summary>
public sealed class JiraInstallation
{
    public string InstallationId { get; set; } = "";
    public string CloudId { get; set; } = "";
    public string? SiteUrl { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? InstallerAccountId { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string? TenantId { get; set; }
    public string? AccountId { get; set; }
    public Guid? JobId { get; set; }
    /// <summary>See <see cref="AuditActions"/>.</summary>
    public string Action { get; set; } = "";
    /// <summary>Human-readable detail. Must never contain secrets (redacted before write).</summary>
    public string? Detail { get; set; }
    public bool Success { get; set; } = true;
}

public static class AuditActions
{
    public const string InvestigationCreated = "investigation.created";
    public const string InvestigationCancelled = "investigation.cancelled";
    public const string InvestigationCompleted = "investigation.completed";
    public const string InvestigationFailed = "investigation.failed";
    public const string EnvironmentSelected = "environment.selected";
    public const string CredentialsSubmitted = "credentials.submitted";
    public const string CredentialsSaved = "credentials.saved";
    public const string AuthenticationAttempt = "authentication.attempt";
    public const string ReportPublished = "report.published";
    public const string AccessDenied = "access.denied";
    public const string AppInstalled = "app.installed";
}
