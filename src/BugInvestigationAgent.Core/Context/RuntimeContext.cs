namespace BugInvestigationAgent.Core.Context;

/// <summary>
/// Per-ticket runtime facts collected from Jira + user input. Never stores credential secrets.
/// </summary>
public sealed class RuntimeContext
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public string? JiraTicketKey { get; set; }
    public string? JiraTicketUrl { get; set; }
    /// <summary>True when full ticket fields were loaded from Jira API or pasted by the user.</summary>
    public bool JiraDetailsLoaded { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public IList<string> ReproductionSteps { get; set; } = new List<string>();
    public IList<string> Comments { get; set; } = new List<string>();
    public IList<AttachmentInfo> Attachments { get; set; } = new List<AttachmentInfo>();
    public IDictionary<string, string> CustomFields { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? SystemName { get; set; }
    public string? EnvironmentName { get; set; }
    public string? FixVersion { get; set; }
    public string? PlatformOs { get; set; }
    public string? EnvironmentUrl { get; set; }
    /// <summary>
    /// Azure DevOps / GitHub pull request linked on the ticket (fix reference).
    /// Never used as the portal EnvironmentUrl.
    /// </summary>
    public string? FixPullRequestUrl { get; set; }
    public string? Username { get; set; }
    public string? Role { get; set; }
    public string? WorkspacePath { get; set; }

    /// <summary>Jira numeric issue id (hosted mode).</summary>
    public string? JiraIssueId { get; set; }
    /// <summary>Jira components of the issue (used for repository mapping).</summary>
    public IList<string> Components { get; set; } = new List<string>();
    /// <summary>Code correlation lines (commits mentioning the ticket, code-search hits). No secrets.</summary>
    public IList<string> CodeCorrelation { get; set; } = new List<string>();
    /// <summary>Repository investigated (display only).</summary>
    public string? RepositoryDisplay { get; set; }

    /// <summary>Investigation identifier (one per Analyze); evidence is filed under it.</summary>
    public Guid InvestigationId => SessionId;

    // ── Environment selection (registry-backed) ──────────────────────────────
    /// <summary>Application/system identifier from the environment registry (e.g. "KMS").</summary>
    public string? ApplicationId { get; set; }
    /// <summary>Environment suggested from the ticket (registry ID, or "adhoc"). Never auto-confirmed.</summary>
    public string? SuggestedEnvironmentId { get; set; }
    public string? SuggestedEnvironmentReason { get; set; }
    public string? SuggestedAdHocUrl { get; set; }
    /// <summary>Environment the user confirmed for this investigation (registry ID or "adhoc:host").</summary>
    public string? SelectedEnvironmentId { get; set; }
    public string? SelectedEnvironmentDisplayName { get; set; }
    public Environments.EnvironmentType? SelectedEnvironmentType { get; set; }
    public bool EnvironmentSelectionConfirmed { get; set; }
    public string? EnvironmentSelectionError { get; set; }

    /// <summary>Production (or configured-as-production) environment: read-only investigation by default.</summary>
    public bool IsReadOnlyEnvironment { get; set; }
    /// <summary>User explicitly allowed potentially state-changing steps on a read-only environment.</summary>
    public bool StateChangingActionsConfirmed { get; set; }
    public IList<string> SkippedStateChangingSteps { get; set; } = new List<string>();

    // ── Authentication (no secrets here — only status) ─────────────────────
    public Authentication.AuthenticationStatus AuthenticationStatus { get; set; }
    public string? AuthenticationMessage { get; set; }
    /// <summary>User says the MFA/manual step is done — re-check the open browser instead of logging in again.</summary>
    public bool AuthenticationRecheckRequested { get; set; }

    /// <summary>Credential profile id only — never the secret itself.</summary>
    public Guid? CredentialProfileId { get; set; }

    /// <summary>True after the user explicitly continues past the pre-reproduce gate (credentials / confirm).</summary>
    public bool UserConfirmedReadyToReproduce { get; set; }

    public IList<MissingField> MissingFields { get; set; } = new List<MissingField>();
    public string? MissingInfoReason { get; set; }

    public Workflow.ReproductionOutcome ReproductionOutcome { get; set; }
    public string? ReproductionNotes { get; set; }
    public IList<EvidenceItem> Evidence { get; set; } = new SynchronizedList<EvidenceItem>();

    public string? RootCause { get; set; }
    public string? ProposedFixSummary { get; set; }
    public IList<string> ProposedFiles { get; set; } = new List<string>();
    public string? ProposedDiff { get; set; }
    public bool FixApproved { get; set; }
    public string? AppliedDiff { get; set; }

    public IList<ValidationResultItem> ValidationResults { get; set; } = new List<ValidationResultItem>();
    public bool RetestPassed { get; set; }
    public string? FinalStatusMessage { get; set; }

    public IList<string> StatusLog { get; set; } = new SynchronizedList<string>();
    public IList<string> AppliedMemoryLessonIds { get; set; } = new List<string>();

    /// <summary>
    /// Adaptive investigation bundle (LLM intent, similar Jira, KMS docs, etc.).
    /// Raw Jira fields above remain the source of truth.
    /// </summary>
    public InvestigationBundle Investigation { get; set; } = new();
}

public sealed class AttachmentInfo
{
    public string Id { get; set; } = "";
    public string Filename { get; set; } = "";
    public string? MimeType { get; set; }
    public string? Url { get; set; }
    /// <summary>Local path after downloading the Jira attachment (images used as repro reference).</summary>
    public string? LocalPath { get; set; }
}

public sealed class MissingField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Reason { get; init; }
    public bool IsSecret { get; init; }
    public string? InputType { get; init; }
}

public sealed class EvidenceItem
{
    public required string Kind { get; init; }
    public string? Path { get; init; }
    public string? Caption { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    // Association metadata — every screenshot is tied to its ticket, investigation and environment.
    public string? JiraTicketKey { get; init; }
    public Guid? InvestigationId { get; init; }
    public string? EnvironmentId { get; init; }
    public string? EnvironmentName { get; init; }
    /// <summary>See <see cref="EvidenceCategories"/>.</summary>
    public string? Category { get; init; }

    /// <summary>Creates an evidence item stamped with the context's ticket, investigation and selected environment.</summary>
    public static EvidenceItem For(RuntimeContext ctx, string kind, string? path, string? caption, string? category = null) => new()
    {
        Kind = kind,
        Path = path,
        Caption = caption,
        Category = category,
        JiraTicketKey = ctx.JiraTicketKey,
        InvestigationId = ctx.InvestigationId,
        EnvironmentId = ctx.SelectedEnvironmentId,
        EnvironmentName = ctx.SelectedEnvironmentDisplayName ?? ctx.EnvironmentName
    };
}

public static class EvidenceCategories
{
    public const string Environment = "environment";
    public const string PostLogin = "post-login";
    public const string Workflow = "workflow";
    public const string FailureState = "failure-state";
    public const string ErrorMessage = "error-message";
    public const string Note = "note";
}

public sealed class ValidationResultItem
{
    public required string Command { get; init; }
    public int ExitCode { get; init; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
    public bool Succeeded => ExitCode == 0;
}
