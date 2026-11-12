namespace BugInvestigationAgent.Core.Investigations;

/// <summary>Whether the investigation itself ran to the end (independent of what it concluded about the bug).</summary>
public enum InvestigationExecutionStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Inconclusive = 3,
    Blocked = 4,
    Failed = 5
}

/// <summary>Primary (mutually exclusive) conclusion about the bug. Secondary observations go to <see cref="InvestigationRecord.FlagsCsv"/>.</summary>
public enum BugAssessment
{
    Undetermined = 0,
    StillExists = 1,
    PossiblyFixed = 2,
    PotentialDuplicate = 3,
    PotentiallyObsolete = 4,
    CriticalCandidate = 5,
    NeedsMoreInformation = 6,
    /// <summary>Not reproduced with every step automated AND a fix commit is in the deployed version's branch.</summary>
    ConfirmedFixed = 7
}

/// <summary>Reproduction outcome as reported to people. "NotReproduced" is never treated as proof of a fix.</summary>
public enum ReproductionResult
{
    NotAttempted = 0,
    Reproduced = 1,
    NotReproduced = 2,
    Inconclusive = 3,
    Blocked = 4
}

/// <summary>The only four Bug verdicts shown to developers.</summary>
public enum BugVerdict
{
    Inconclusive = 0,
    ConfirmedFixed = 1,
    StillReproducible = 2,
    Blocked = 3
}

public enum EvidenceSufficiency
{
    Insufficient = 0,
    Partial = 1,
    Sufficient = 2
}

public enum DraftReviewStatus
{
    PendingReview = 0,
    Approved = 1,
    Rejected = 2
}

public enum CommentPublishStatus
{
    NotPublished = 0,
    /// <summary>A publish attempt is in progress (or was interrupted): retries check Jira for the comment first.</summary>
    Publishing = 1,
    Published = 2,
    Failed = 3,
    /// <summary>The Jira issue changed materially after the draft was generated; a fresh investigation or review is required.</summary>
    BlockedIssueChanged = 4
}

/// <summary>
/// Durable result of one investigation run, its report, and its Jira comment draft with review/publication audit.
/// One row per run (a re-investigation creates a new record; older records stay for audit).
/// </summary>
public sealed class InvestigationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The investigation (session) id — also the evidence folder name.</summary>
    public Guid InvestigationId { get; set; }
    public string IssueKey { get; set; } = "";
    public string? IssueSummary { get; set; }
    public Guid? BatchId { get; set; }

    public InvestigationExecutionStatus ExecutionStatus { get; set; } = InvestigationExecutionStatus.Pending;
    /// <summary>"Bug" or "Story" (from Jira, or the developer's choice).</summary>
    public string IssueType { get; set; } = "Bug";
    public BugVerdict Verdict { get; set; } = BugVerdict.Inconclusive;
    public EvidenceSufficiency EvidenceSufficiency { get; set; }
    public double? DurationSeconds { get; set; }
    /// <summary>Developer feedback on the recommendation: "Correct", "Partially correct", "Incorrect".</summary>
    public string? Feedback { get; set; }
    public string? FeedbackNote { get; set; }
    public string? FeedbackBy { get; set; }
    public DateTimeOffset? FeedbackAt { get; set; }
    /// <summary>Approved reopen of a Still Reproducible bug: who, when, result.</summary>
    public string? ReopenResult { get; set; }
    public string? ReopenedBy { get; set; }
    public DateTimeOffset? ReopenedAt { get; set; }
    public string? FixTaskPath { get; set; }
    public string? ExecutionDetail { get; set; }
    public BugAssessment Assessment { get; set; } = BugAssessment.Undetermined;
    /// <summary>Additional observations (e.g. "CriticalCandidate,MissingSteps") — never counted in the primary totals.</summary>
    public string? FlagsCsv { get; set; }
    public string? AssessmentReason { get; set; }
    public ReproductionResult ReproductionOutcome { get; set; } = ReproductionResult.NotAttempted;

    public string? EnvironmentId { get; set; }
    public string? EnvironmentName { get; set; }
    public bool EnvironmentIsProduction { get; set; }

    public string? RootCause { get; set; }
    public bool RootCauseConfirmed { get; set; }
    public string Confidence { get; set; } = "Low";
    public string? RecommendedAction { get; set; }
    public string? SimilarIssueKeysCsv { get; set; }
    public string? CodeReferences { get; set; }

    public string? KmsVersion { get; set; }
    public string? SourceBranch { get; set; }
    /// <summary>Local branch created for a still-reproducible bug (never checked out, committed or pushed).</summary>
    public string? CreatedBranch { get; set; }
    public string? BranchNote { get; set; }
    public string? ProposedFix { get; set; }
    public string? AffectedFilesCsv { get; set; }

    /// <summary>Full structured report (redacted) — kept in the database so it survives evidence-folder reuse.</summary>
    public string? ReportJson { get; set; }
    public string? ReportMarkdown { get; set; }
    public string? ReportJsonPath { get; set; }
    public string? ReportMarkdownPath { get; set; }

    /// <summary>Hash of the issue's material fields (summary, description, status, resolution) when the draft was generated.</summary>
    public string? IssueFingerprint { get; set; }
    public DateTimeOffset? IssueUpdatedAt { get; set; }

    public string? CommentDraftOriginal { get; set; }
    public string? CommentDraft { get; set; }
    public DraftReviewStatus DraftStatus { get; set; } = DraftReviewStatus.PendingReview;
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }

    public CommentPublishStatus PublishStatus { get; set; } = CommentPublishStatus.NotPublished;
    public string? PublishedCommentId { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public string? PublishError { get; set; }
    public int PublishAttempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Optimistic concurrency: two clicks on Publish can never both write.</summary>
    public int Version { get; set; }

    public IReadOnlyList<string> Flags => Split(FlagsCsv);
    public IReadOnlyList<string> SimilarIssueKeys => Split(SimilarIssueKeysCsv);

    private static IReadOnlyList<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? Array.Empty<string>() : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A frozen snapshot of a Jira filter's issues, processed one ticket at a time and resumable.</summary>
public sealed class LegacyBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string FilterId { get; set; } = "";
    public string Jql { get; set; } = "";
    public string EnvironmentId { get; set; } = "";
    public string? EnvironmentUrl { get; set; }
    public string? EnvironmentName { get; set; }
    public Guid? CredentialProfileId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastMembershipCheckAt { get; set; }
    /// <summary>"added: K1,K2; removed: K3" from the last membership check (the snapshot itself never changes).</summary>
    public string? LastMembershipChange { get; set; }
    public List<LegacyBatchItem> Items { get; set; } = new();
}

public sealed class LegacyBatchItem
{
    public long Id { get; set; }
    public Guid BatchId { get; set; }
    public int Position { get; set; }
    public string IssueKey { get; set; } = "";
    public InvestigationExecutionStatus Status { get; set; } = InvestigationExecutionStatus.Pending;
    public Guid? RecordId { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
