using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Security;

namespace BugInvestigationAgent.Core.Investigations;

/// <summary>
/// Local review of the suggested Jira comment text kept in each investigation record. Nothing here talks to Jira:
/// the legacy investigation workflow never publishes comments, drafts or reports.
/// </summary>
public sealed class CommentReviewService
{
    private readonly IInvestigationRecordStore _records;
    private readonly IAuditLog _audit;
    private readonly ISecretRedactor _redactor;

    public CommentReviewService(IInvestigationRecordStore records, IAuditLog audit, ISecretRedactor redactor)
    {
        _records = records;
        _audit = audit;
        _redactor = redactor;
    }

    public async Task<(bool Ok, string Message)> SaveEditAsync(Guid recordId, string text, string editor, CancellationToken cancellationToken = default)
    {
        var r = await _records.GetAsync(recordId, cancellationToken);
        if (r is null)
            return (false, "Record not found.");
        if (r.PublishStatus == CommentPublishStatus.Published || r.DraftStatus == DraftReviewStatus.Rejected)
            return (false, "This draft can no longer be edited.");
        if (string.IsNullOrWhiteSpace(text))
            return (false, "The draft cannot be empty.");
        r.CommentDraft = _redactor.Redact(text.Trim());
        if (!await _records.TryUpdateAsync(r, cancellationToken))
            return (false, "The draft was changed elsewhere — reload and try again.");
        await Audit(r, InvestigationAuditActions.DraftEdited, $"Draft edited by {editor}.", true, cancellationToken);
        return (true, "Suggested comment saved locally (never posted to Jira).");
    }

    /// <summary>Rejects the draft. Kept for audit. No Jira call of any kind.</summary>
    public async Task<(bool Ok, string Message)> RejectAsync(Guid recordId, string reviewer, string? reason, CancellationToken cancellationToken = default)
    {
        var r = await _records.GetAsync(recordId, cancellationToken);
        if (r is null)
            return (false, "Record not found.");
        if (r.PublishStatus == CommentPublishStatus.Published)
            return (false, "Already published — it cannot be rejected.");
        r.DraftStatus = DraftReviewStatus.Rejected;
        r.ReviewedBy = reviewer;
        r.ReviewedAt = DateTimeOffset.UtcNow;
        r.RejectionReason = string.IsNullOrWhiteSpace(reason) ? null : _redactor.Redact(reason.Trim());
        if (!await _records.TryUpdateAsync(r, cancellationToken))
            return (false, "The draft was changed elsewhere — reload and try again.");
        await Audit(r, InvestigationAuditActions.DraftRejected, $"Rejected by {reviewer}: {r.RejectionReason ?? "(no reason)"}", true, cancellationToken);
        return (true, "Suggested comment rejected (kept for audit).");
    }

    private Task Audit(InvestigationRecord r, string action, string detail, bool success, CancellationToken cancellationToken) =>
        _audit.WriteAsync(new AuditEntry { JobId = r.Id, Action = action, Detail = $"{r.IssueKey}: {detail}", Success = success }, cancellationToken);
}
