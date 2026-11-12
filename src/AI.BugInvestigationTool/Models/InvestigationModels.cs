using System.Text.Json.Serialization;

namespace AI.BugInvestigationTool.Models;

public enum TargetKind { JiraIssue, PullRequest }

/// <summary>A parsed, validated input URL. Exactly one of the two shapes is filled.</summary>
public sealed record InvestigationTarget(
    TargetKind Kind,
    string Url,
    string? JiraBaseUrl = null,
    string? JiraKey = null,
    string? AdoOrganization = null,
    string? AdoProject = null,
    string? AdoRepository = null,
    int PullRequestId = 0)
{
    public string DisplayName => Kind == TargetKind.JiraIssue ? JiraKey! : $"PR {PullRequestId} ({AdoRepository})";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InvestigationOutcome { Resolved, Unresolved, NotReproducible, Inconclusive, Blocked }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InvestigationStep
{
    Created,
    JiraDataCollection,
    ContextValidation,
    WaitingForInput,
    BrowserInvestigation,
    CodeInvestigation,
    PullRequestCollection,
    TestExecution,
    ClaudeAnalysis,
    ReportGeneration,
    Completed,
    JiraPublishing,
    Failed,
    Cancelled,
}

public sealed class ProgressEntry
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public InvestigationStep Step { get; set; }
    public string Message { get; set; } = "";
}

public sealed class MissingInfoItem
{
    public string Key { get; set; } = "";
    public string Question { get; set; } = "";
    public bool Required { get; set; }
}

public sealed class CodeReference
{
    public string Repository { get; set; } = "";
    public string Path { get; set; } = "";
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public string Note { get; set; } = "";

    public override string ToString() =>
        $"{(string.IsNullOrEmpty(Repository) ? "" : Repository + ":")}{Path}" +
        (StartLine is null ? "" : EndLine is null || EndLine == StartLine ? $" L{StartLine}" : $" L{StartLine}-{EndLine}");
}

public sealed class EvidenceItem
{
    public string Kind { get; set; } = ""; // screenshot | console | network | attachment | test-output
    public string Label { get; set; } = "";
    /// <summary>Path relative to the investigation's evidence folder.</summary>
    public string? File { get; set; }
    public string? Detail { get; set; }
}

public sealed class BrowserResult
{
    public bool Attempted { get; set; }
    public bool LoginSucceeded { get; set; }
    /// <summary>yes | no | unclear</summary>
    public string Reproduced { get; set; } = "unclear";
    /// <summary>True only when the expected behaviour was explicitly observed in the environment.</summary>
    public bool ExpectedBehaviorObserved { get; set; }
    public string ActualBehavior { get; set; } = "";
    public List<string> StepsExecuted { get; set; } = [];
    public List<string> ConsoleErrors { get; set; } = [];
    public List<string> NetworkFailures { get; set; } = [];
    public string? ClarificationNeeded { get; set; }
    public string? Error { get; set; }
    public string Notes { get; set; } = "";
}

public sealed class Finding
{
    public string Title { get; set; } = "";
    /// <summary>Critical | Medium | Low (PR review) or free text for bug findings.</summary>
    public string Severity { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public CodeReference? Reference { get; set; }
    /// <summary>Set when the file/line could not be matched to the inspected code or diff.</summary>
    public bool ReferenceUnverified { get; set; }
}

public sealed class TestRunResult
{
    public bool Executed { get; set; }
    public string Summary { get; set; } = "";
    public int? ExitCode { get; set; }
    public string? OutputFile { get; set; }
}

public sealed class InvestigationReport
{
    public InvestigationOutcome Outcome { get; set; } = InvestigationOutcome.Inconclusive;
    /// <summary>The outcome Claude proposed before the safety rules were applied (if it differs).</summary>
    public InvestigationOutcome? ProposedOutcome { get; set; }
    public string? OutcomeAdjustmentReason { get; set; }
    public string Summary { get; set; } = "";
    public string ExpectedBehavior { get; set; } = "";
    public string ActualBehavior { get; set; } = "";
    public string ReproductionResult { get; set; } = "";
    public string RootCause { get; set; } = "";
    /// <summary>"confirmed" or "hypothesis" — assumptions must never be presented as facts.</summary>
    public string RootCauseStatus { get; set; } = "hypothesis";
    public List<Finding> Findings { get; set; } = [];
    public List<CodeReference> CodeReferences { get; set; } = [];
    public List<string> Recommendations { get; set; } = [];
    public List<string> MissingTests { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public double Confidence { get; set; }
    public string OverallAssessment { get; set; } = "";
    /// <summary>Jira transition the agent proposes (e.g. "Reopen", "Close"). Never executed without approval.</summary>
    public string? ProposedJiraAction { get; set; }
    /// <summary>PR workflow only: Approve | Approve with suggestions | Waiting for author (advisory; never submitted as a vote).</summary>
    public string? PrVerdict { get; set; }
}

public sealed class PublishRecord
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = "";
    public bool Success { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>Everything persisted for one investigation. Contains NO credentials.</summary>
public sealed class Investigation
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public InvestigationTarget Target { get; set; } = null!;
    public InvestigationStep Step { get; set; } = InvestigationStep.Created;
    public string? Error { get; set; }
    public List<ProgressEntry> Progress { get; set; } = [];
    public JiraIssue? Issue { get; set; }
    public PullRequestData? PullRequest { get; set; }
    public TicketUnderstanding? Understanding { get; set; }
    /// <summary>Non-secret answers supplied by the user (expected behaviour, steps, environment URL...).</summary>
    public Dictionary<string, string> UserInfo { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MissingInfoItem> MissingInfo { get; set; } = [];
    public BrowserResult? Browser { get; set; }
    public string? CodeFindings { get; set; }
    public TestRunResult? Tests { get; set; }
    public List<EvidenceItem> Evidence { get; set; } = [];
    public InvestigationReport? Report { get; set; }
    public string? JiraCommentDraft { get; set; }
    public List<PublishRecord> Publishing { get; set; } = [];

    [JsonIgnore] public bool IsFinished => Step is InvestigationStep.Completed or InvestigationStep.Failed or InvestigationStep.Cancelled or InvestigationStep.WaitingForInput;
}

/// <summary>Claude's structured reading of the ticket (prompt 1: ticket understanding).</summary>
public sealed class TicketUnderstanding
{
    public string Summary { get; set; } = "";
    public string ExpectedBehavior { get; set; } = "";
    public string ActualBehavior { get; set; } = "";
    public List<string> ReproductionSteps { get; set; } = [];
    public string? EnvironmentUrl { get; set; }
    public bool RequiresLogin { get; set; } = true;
    public List<string> CodeSearchTerms { get; set; } = [];
    public List<MissingInfoItem> MissingInformation { get; set; } = [];
}
