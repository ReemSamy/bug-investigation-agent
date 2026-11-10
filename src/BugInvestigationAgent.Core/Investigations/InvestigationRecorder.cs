using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Reporting;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace BugInvestigationAgent.Core.Investigations;

/// <summary>
/// Persists every finished investigation as an <see cref="InvestigationRecord"/>: assessment, full report and a
/// Jira comment draft in <see cref="DraftReviewStatus.PendingReview"/>. Reads Jira (snapshot) — never writes it.
/// </summary>
public sealed class InvestigationRecorder : IInvestigationCompletionSink
{
    public const string BatchIdField = "legacyBatchId";
    public const string ReportDirectoryField = "reportDirectory";

    private readonly IInvestigationRecordStore _records;
    private readonly IJiraIssueGateway _jira;
    private readonly ISecretRedactor _redactor;
    private readonly IAuditLog _audit;
    private readonly ILogger<InvestigationRecorder> _logger;

    public InvestigationRecorder(IInvestigationRecordStore records, IJiraIssueGateway jira, ISecretRedactor redactor, IAuditLog audit,
        ILogger<InvestigationRecorder> logger)
    {
        _records = records;
        _jira = jira;
        _redactor = redactor;
        _audit = audit;
        _logger = logger;
    }

    public async Task OnInvestigationFinishedAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        var ctx = session.Context;
        if (string.IsNullOrWhiteSpace(ctx.JiraTicketKey))
            return;

        JiraIssueSnapshot? issue = null;
        try
        {
            issue = await _jira.GetSnapshotAsync(ctx.JiraTicketKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No baseline fingerprint → publishing this draft will require a fresh investigation.
            _logger.LogWarning("Could not read the Jira snapshot for {Issue}: {Error}", ctx.JiraTicketKey, _redactor.Redact(ex.Message));
        }

        var report = InvestigationReportBuilder.Build(ctx, _redactor, ctx.RepositoryDisplay ?? ctx.WorkspacePath);
        var a = InvestigationAssessor.Assess(session.State, ctx, report, issue, DateTimeOffset.UtcNow);
        var reportDir = ctx.CustomFields.TryGetValue(ReportDirectoryField, out var d) ? d : null;

        var record = new InvestigationRecord
        {
            InvestigationId = ctx.InvestigationId,
            IssueKey = ctx.JiraTicketKey,
            IssueSummary = _redactor.Redact(ctx.Summary),
            BatchId = ctx.CustomFields.TryGetValue(BatchIdField, out var b) && Guid.TryParse(b, out var batchId) ? batchId : null,
            ExecutionStatus = a.ExecutionStatus,
            ExecutionDetail = _redactor.Redact(a.ExecutionDetail),
            Assessment = a.Assessment,
            AssessmentReason = _redactor.Redact(a.AssessmentReason),
            FlagsCsv = a.Flags.Count == 0 ? null : string.Join(",", a.Flags),
            ReproductionOutcome = a.Reproduction,
            EnvironmentId = ctx.SelectedEnvironmentId,
            EnvironmentName = ctx.SelectedEnvironmentDisplayName ?? ctx.EnvironmentName,
            EnvironmentIsProduction = ctx.IsReadOnlyEnvironment && ctx.SelectedEnvironmentType == Environments.EnvironmentType.Production,
            RootCause = ctx.CustomFields.ContainsKey("investigationBlockedNoLlm") ? null : NullIfEmpty(_redactor.Redact(ctx.RootCause)),
            RootCauseConfirmed = a.RootCauseConfirmed,
            Confidence = report.Confidence,
            RecommendedAction = a.RecommendedAction,
            SimilarIssueKeysCsv = NullIfEmpty(string.Join(",", ctx.Investigation.SimilarIssues.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Take(10))),
            CodeReferences = NullIfEmpty(string.Join("\n", report.CodeFindings.Concat(report.FixCommitsInVersion))),
            KmsVersion = report.KmsVersion,
            SourceBranch = report.SourceBranch,
            CreatedBranch = report.CreatedBranch,
            BranchNote = report.BranchNote,
            ProposedFix = report.ProposedFix,
            AffectedFilesCsv = NullIfEmpty(string.Join(",", report.AffectedFiles)),
            ReportJson = System.Text.Json.JsonSerializer.Serialize(report),
            ReportMarkdown = InvestigationReportBuilder.ToMarkdown(report),
            ReportJsonPath = reportDir is null ? null : Path.Combine(reportDir, "report.json"),
            ReportMarkdownPath = reportDir is null ? null : Path.Combine(reportDir, "report.md"),
            IssueFingerprint = issue?.Fingerprint,
            IssueUpdatedAt = issue?.Updated,
            DraftStatus = DraftReviewStatus.PendingReview,
            PublishStatus = CommentPublishStatus.NotPublished
        };
        var draft = _redactor.Redact(CommentDraftBuilder.Build(record, report));
        record.CommentDraftOriginal = draft;
        record.CommentDraft = draft;

        await _records.AddAsync(record, cancellationToken);
        await _audit.WriteAsync(new AuditEntry
        {
            JobId = record.Id,
            Action = InvestigationAuditActions.DraftCreated,
            Detail = $"{record.IssueKey}: {record.ExecutionStatus}, assessment {record.Assessment}, draft pending review."
        }, cancellationToken);
        ctx.CustomFields["investigationRecordId"] = record.Id.ToString();
        ctx.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Investigation record saved — Jira comment draft is PENDING REVIEW (nothing was posted to Jira).");
        session.NotifyChanged();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public static class InvestigationAuditActions
{
    public const string DraftCreated = "comment.draft.created";
    public const string DraftEdited = "comment.draft.edited";
    public const string DraftRejected = "comment.draft.rejected";
    public const string DraftApproved = "comment.draft.approved";
    public const string IssueReconfirmed = "comment.issue.reconfirmed";
    public const string CommentPublished = "comment.published";
    public const string CommentPublishBlocked = "comment.publish.blocked";
    public const string CommentPublishFailed = "comment.publish.failed";
}
