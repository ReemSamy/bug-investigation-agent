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
    public string? Username { get; set; }
    public string? Role { get; set; }
    public string? WorkspacePath { get; set; }

    /// <summary>Credential profile id only — never the secret itself.</summary>
    public Guid? CredentialProfileId { get; set; }

    /// <summary>True after the user explicitly continues past the pre-reproduce gate (credentials / confirm).</summary>
    public bool UserConfirmedReadyToReproduce { get; set; }

    public IList<MissingField> MissingFields { get; set; } = new List<MissingField>();
    public string? MissingInfoReason { get; set; }

    public Workflow.ReproductionOutcome ReproductionOutcome { get; set; }
    public string? ReproductionNotes { get; set; }
    public IList<EvidenceItem> Evidence { get; set; } = new List<EvidenceItem>();

    public string? RootCause { get; set; }
    public string? ProposedFixSummary { get; set; }
    public IList<string> ProposedFiles { get; set; } = new List<string>();
    public string? ProposedDiff { get; set; }
    public bool FixApproved { get; set; }
    public string? AppliedDiff { get; set; }

    public IList<ValidationResultItem> ValidationResults { get; set; } = new List<ValidationResultItem>();
    public bool RetestPassed { get; set; }
    public string? FinalStatusMessage { get; set; }

    public IList<string> StatusLog { get; set; } = new List<string>();
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
}

public sealed class ValidationResultItem
{
    public required string Command { get; init; }
    public int ExitCode { get; init; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
    public bool Succeeded => ExitCode == 0;
}
