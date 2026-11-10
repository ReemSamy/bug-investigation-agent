using System.Collections.Concurrent;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Investigations;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Memory;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Reporting;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Sessions;
using BugInvestigationAgent.Core.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace BugInvestigationAgent.Core.Tests;

// ── Fakes ────────────────────────────────────────────────────────────────────────────────────────────

internal sealed class MemoryRecordStore : IInvestigationRecordStore
{
    private readonly ConcurrentDictionary<Guid, InvestigationRecord> _rows = new();
    private readonly object _gate = new();

    private static InvestigationRecord Copy(InvestigationRecord r) => (InvestigationRecord)typeof(InvestigationRecord)
        .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(r, null)!;

    public Task AddAsync(InvestigationRecord record, CancellationToken cancellationToken = default)
    {
        record.CreatedAt = record.UpdatedAt = DateTimeOffset.UtcNow;
        _rows[record.Id] = Copy(record);
        return Task.CompletedTask;
    }

    public Task<InvestigationRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.TryGetValue(id, out var r) ? Copy(r) : null);

    public Task<InvestigationRecord?> FindByInvestigationAsync(Guid investigationId, DateTimeOffset notBefore, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.Values.Where(r => r.InvestigationId == investigationId && r.CreatedAt >= notBefore).Select(Copy).FirstOrDefault());

    public Task<IReadOnlyList<InvestigationRecord>> ListAsync(Guid? batchId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InvestigationRecord>>(_rows.Values.Where(r => batchId is null || r.BatchId == batchId).Select(Copy).ToList());

    public Task<bool> TryUpdateAsync(InvestigationRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(record.Id, out var current) || current.Version != record.Version)
                return Task.FromResult(false);
            record.Version++;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            _rows[record.Id] = Copy(record);
            return Task.FromResult(true);
        }
    }
}

internal sealed class FakeJiraGateway : IJiraIssueGateway
{
    public string Fingerprint = "FP1";
    public string? ExistingMarkerCommentId;
    public Exception? AddCommentError;
    public int Reads;
    public readonly List<(string Key, string Body)> AddedComments = new();
    public List<string> FilterKeys = new();
    public TimeSpan AddDelay = TimeSpan.Zero;

    public Task<JiraIssueSnapshot?> GetSnapshotAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        return Task.FromResult<JiraIssueSnapshot?>(new JiraIssueSnapshot(issueKey, "s", "Backlog", null, "Low",
            DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow, Array.Empty<string>(), Fingerprint));
    }

    public Task<(string Jql, IReadOnlyList<string> Keys)> GetFilterIssuesAsync(string filterId, CancellationToken cancellationToken = default) =>
        Task.FromResult<(string, IReadOnlyList<string>)>(("project = VKMS", FilterKeys));

    public Task<string?> FindCommentContainingAsync(string issueKey, string marker, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        lock (AddedComments)
            return Task.FromResult(ExistingMarkerCommentId ?? (AddedComments.Any(c => c.Body.Contains(marker)) ? "c-existing" : null));
    }

    public async Task<string> AddCommentAsync(string issueKey, string adfBodyJson, CancellationToken cancellationToken = default)
    {
        if (AddDelay > TimeSpan.Zero)
            await Task.Delay(AddDelay, cancellationToken);
        if (AddCommentError is not null)
            throw AddCommentError;
        lock (AddedComments)
        {
            AddedComments.Add((issueKey, adfBodyJson));
            return "c" + AddedComments.Count;
        }
    }
}

internal sealed class MemoryAudit : IAuditLog
{
    public readonly ConcurrentQueue<AuditEntry> Entries = new();
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) { Entries.Enqueue(entry); return Task.CompletedTask; }
    public Task<IReadOnlyList<AuditEntry>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(Entries.Where(e => e.JobId == jobId).ToList());
}

// ── Assessment and draft ─────────────────────────────────────────────────────────────────────────────

public class InvestigationAssessmentAndDraftTests
{
    private static RuntimeContext Ctx(ReproductionOutcome outcome, bool screenshot = false)
    {
        var ctx = new RuntimeContext { JiraTicketKey = "VKMS-1", Summary = "Badgermail does not re-trigger", Description = new string('x', 120), ReproductionOutcome = outcome };
        ctx.CustomFields["stepsFromJira"] = "true";
        if (screenshot)
            ctx.Evidence.Add(new EvidenceItem { Kind = "screenshot", Path = "C:/e/1.png", Caption = "step" });
        return ctx;
    }

    private static InvestigationAssessment Assess(AgentState state, RuntimeContext ctx) =>
        InvestigationAssessor.Assess(state, ctx, InvestigationReportBuilder.Build(ctx, new SecretRedactor()), null, DateTimeOffset.UtcNow);

    [Test]
    public void Not_reproduced_alone_is_never_possibly_fixed()
    {
        var a = Assess(AgentState.Completed, Ctx(ReproductionOutcome.NotReproduced));
        Assert.That(a.Reproduction, Is.EqualTo(ReproductionResult.NotReproduced));
        Assert.That(a.Assessment, Is.EqualTo(BugAssessment.Undetermined));
        Assert.That(a.ExecutionStatus, Is.EqualTo(InvestigationExecutionStatus.Inconclusive), "no evidence and no code analysis");
    }

    [Test]
    public void Reproduced_with_evidence_is_still_exists_and_a_fix_link_without_reproduction_is_possibly_fixed()
    {
        Assert.That(Assess(AgentState.Completed, Ctx(ReproductionOutcome.Reproduced, screenshot: true)).Assessment, Is.EqualTo(BugAssessment.StillExists));

        var ctx = Ctx(ReproductionOutcome.NotReproduced);
        ctx.FixPullRequestUrl = "https://dev.azure.com/x/_git/y/pullrequest/1";
        ctx.RootCause = "hypothesis";
        var a = Assess(AgentState.Completed, ctx);
        Assert.That(a.Assessment, Is.EqualTo(BugAssessment.PossiblyFixed));
        Assert.That(a.ExecutionStatus, Is.EqualTo(InvestigationExecutionStatus.Completed));
        Assert.That(a.RecommendedAction, Does.Contain("not proof"));
    }

    [Test]
    public void Secondary_signals_are_flags_not_extra_primary_categories()
    {
        var ctx = Ctx(ReproductionOutcome.Reproduced, screenshot: true);
        ctx.Summary = "Crash when saving the incident";
        var a = Assess(AgentState.Completed, ctx);
        Assert.That(a.Assessment, Is.EqualTo(BugAssessment.StillExists));
        Assert.That(a.Flags, Is.EqualTo(new[] { "CriticalCandidate" }));
    }

    [Test]
    public void Failed_and_blocked_runs_map_to_execution_status_with_reason()
    {
        Assert.That(Assess(AgentState.Failed, Ctx(ReproductionOutcome.NotStarted)).ExecutionStatus, Is.EqualTo(InvestigationExecutionStatus.Failed));
        var blocked = Assess(AgentState.AwaitingMissingInfo, Ctx(ReproductionOutcome.NotStarted));
        Assert.That(blocked.ExecutionStatus, Is.EqualTo(InvestigationExecutionStatus.Blocked));
        Assert.That(blocked.Reproduction, Is.EqualTo(ReproductionResult.NotAttempted));
    }

    [Test]
    public void Draft_is_labelled_ai_generated_marks_hypotheses_and_keeps_the_marker()
    {
        var record = new InvestigationRecord
        {
            IssueKey = "VKMS-1", ExecutionDetail = "Completed.", Assessment = BugAssessment.PossiblyFixed, AssessmentReason = "fix linked",
            ReproductionOutcome = ReproductionResult.NotReproduced, EnvironmentName = "KMSQA", RootCause = "Alert not re-queued",
            RecommendedAction = "Verify on an environment with the fix.", SimilarIssueKeysCsv = "VKMS-2,VKMS-3"
        };
        var ctx = Ctx(ReproductionOutcome.NotReproduced);
        var draft = CommentDraftBuilder.Build(record, InvestigationReportBuilder.Build(ctx, new SecretRedactor()));

        Assert.That(draft, Does.StartWith(CommentDraftBuilder.Header));
        Assert.That(draft, Does.Contain("Root cause (Hypothesis): Alert not re-queued"));
        Assert.That(draft, Does.Contain("not proof that the bug is fixed"));
        Assert.That(draft, Does.Contain("Related issues: VKMS-2, VKMS-3"));
        Assert.That(draft, Does.EndWith(CommentDraftBuilder.Marker(record.Id)));

        // A reviewer deleting the marker line cannot defeat duplicate prevention.
        var adf = CommentDraftBuilder.ToAdf("Edited text\n- point one", record.Id);
        Assert.That(adf, Does.Contain(CommentDraftBuilder.Marker(record.Id)).And.Contain("bulletList"));
    }
}

// ── Record creation, review and publication ─────────────────────────────────────────────────────────

public class CommentReviewServiceTests
{
    private MemoryRecordStore _store = null!;
    private FakeJiraGateway _jira = null!;
    private MemoryAudit _audit = null!;
    private CommentReviewService _review = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new MemoryRecordStore();
        _jira = new FakeJiraGateway();
        _audit = new MemoryAudit();
        _review = new CommentReviewService(_store, _audit, new SecretRedactor());
    }

    private async Task<InvestigationRecord> RecordFromFinishedRunAsync()
    {
        var session = new AgentSession();
        session.Context.SessionId = session.Id;
        session.Context.JiraTicketKey = "VKMS-7";
        session.Context.Summary = "Badgermail does not re-trigger";
        session.Context.ReproductionOutcome = ReproductionOutcome.NotReproduced;
        session.Context.RootCause = "Alert not re-queued (password=Hunter2!)";
        session.Context.DeployedVersion = "12.28.4";
        session.Context.SourceVersionRef = "origin/release/12.28";
        session.SetState(AgentState.Completed);
        var recorder = new InvestigationRecorder(_store, _jira, new SecretRedactor(), _audit, NullLogger<InvestigationRecorder>.Instance);
        await recorder.OnInvestigationFinishedAsync(session);
        return (await _store.ListAsync()).Single(r => r.InvestigationId == session.Id);
    }

    [Test]
    public async Task Finished_run_creates_a_persisted_local_record_with_version_and_no_jira_write()
    {
        var r = await RecordFromFinishedRunAsync();

        Assert.That(r.ExecutionStatus, Is.EqualTo(InvestigationExecutionStatus.Completed));
        Assert.That(r.KmsVersion, Is.EqualTo("12.28.4"));
        Assert.That(r.SourceBranch, Is.EqualTo("origin/release/12.28"));
        Assert.That(r.ReportMarkdown, Does.Contain("VKMS-7").And.Contain("KMS version: 12.28.4"));
        Assert.That(r.CommentDraft, Does.Contain("not posted to Jira"));
        Assert.That(r.CommentDraft + r.ReportJson + r.RootCause, Does.Not.Contain("Hunter2!"), "secrets redacted");
        Assert.That(_jira.AddedComments, Is.Empty);
    }

    [Test]
    public async Task Editing_and_rejecting_the_suggested_comment_are_local_only()
    {
        var r = await RecordFromFinishedRunAsync();
        var readsBefore = _jira.Reads;

        Assert.That((await _review.SaveEditAsync(r.Id, "Edited text", "Reem")).Ok, Is.True);
        Assert.That((await _review.RejectAsync(r.Id, "Reem", "not accurate")).Ok, Is.True);

        var after = (await _store.GetAsync(r.Id))!;
        Assert.That(after.CommentDraft, Is.EqualTo("Edited text"));
        Assert.That(after.DraftStatus, Is.EqualTo(DraftReviewStatus.Rejected));
        Assert.That(_jira.Reads, Is.EqualTo(readsBefore), "no Jira call at all");
    }

    [Test]
    public void The_workflow_has_no_way_to_write_to_jira()
    {
        // The gateway contract is read-only and the review service has no Jira dependency or publish method.
        var gatewayMethods = typeof(IJiraIssueGateway).GetMethods().Select(m => m.Name).ToList();
        Assert.That(gatewayMethods, Is.EquivalentTo(new[] { "GetSnapshotAsync", "GetFilterIssuesAsync" }));
        Assert.That(typeof(CommentReviewService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType),
            Has.None.EqualTo(typeof(IJiraIssueGateway)));
        Assert.That(typeof(CommentReviewService).GetMethods().Select(m => m.Name), Has.None.Contains("Publish"));
    }
}

// ── Legacy batch ─────────────────────────────────────────────────────────────────────────────────────

internal sealed class MemoryBatchStore : ILegacyBatchStore
{
    public readonly Dictionary<Guid, LegacyBatch> Rows = new();
    public Task AddAsync(LegacyBatch batch, CancellationToken cancellationToken = default) { Rows[batch.Id] = batch; return Task.CompletedTask; }
    public Task<LegacyBatch?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Rows.GetValueOrDefault(id));
    public Task<IReadOnlyList<LegacyBatch>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LegacyBatch>>(Rows.Values.ToList());
    public Task UpdateItemAsync(LegacyBatchItem item, CancellationToken cancellationToken = default) => Task.CompletedTask; // items are shared references
    public Task UpdateBatchAsync(LegacyBatch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Plays the orchestrator: asks for the environment, then finishes per ticket and records via the real recorder.</summary>
internal sealed class ScriptedOrchestrator : IAgentOrchestrator
{
    private readonly IAgentSessionStore _sessions;
    private readonly InvestigationRecorder _recorder;
    public readonly Dictionary<string, AgentState> FinalStates = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Started = new();
    public readonly List<string> SubmittedEnvironments = new();

    public ScriptedOrchestrator(IAgentSessionStore sessions, InvestigationRecorder recorder) { _sessions = sessions; _recorder = recorder; }

    public Task StartAsync(Guid sessionId, string jiraTicketOrUrl, string? workspacePath, CancellationToken cancellationToken = default)
    {
        var s = _sessions.Get(sessionId)!;
        s.Context.JiraTicketKey = jiraTicketOrUrl;
        lock (Started) Started.Add(jiraTicketOrUrl);
        s.SetState(AgentState.AwaitingMissingInfo, "Select the environment.");
        return Task.CompletedTask;
    }

    public async Task SubmitMissingInfoAsync(Guid sessionId, IDictionary<string, string> values, CredentialSecret? newCredentials, bool saveCredentialProfile, CancellationToken cancellationToken = default)
    {
        var s = _sessions.Get(sessionId)!;
        SubmittedEnvironments.Add(values["environmentId"]);
        var final = FinalStates.GetValueOrDefault(s.Context.JiraTicketKey!, AgentState.Completed);
        if (final == AgentState.Completed)
        {
            s.Context.ReproductionOutcome = ReproductionOutcome.NotReproduced;
            s.Context.RootCause = "hypothesis";
        }
        s.SetState(final);
        await _recorder.OnInvestigationFinishedAsync(s, cancellationToken);
    }

    public Task RunAsync(Guid sessionId, string jiraTicketOrUrl, string? workspacePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CancelAsync(Guid sessionId) => Task.CompletedTask;
    public Task ApproveFixAsync(Guid sessionId, bool approve, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SaveLessonAsync(Guid sessionId, string lesson, MemoryScope scope, IEnumerable<string>? tags, string? validatedBy, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class LegacyBatchServiceTests
{
    private MemoryBatchStore _batches = null!;
    private MemoryRecordStore _records = null!;
    private FakeJiraGateway _jira = null!;
    private InMemoryAgentSessionStore _sessions = null!;
    private ScriptedOrchestrator _orchestrator = null!;
    private LegacyBatchService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _batches = new MemoryBatchStore();
        _records = new MemoryRecordStore();
        _jira = new FakeJiraGateway { FilterKeys = new List<string> { "VKMS-1", "VKMS-2", "VKMS-3" } };
        _sessions = new InMemoryAgentSessionStore();
        var recorder = new InvestigationRecorder(_records, _jira, new SecretRedactor(), new MemoryAudit(), NullLogger<InvestigationRecorder>.Instance);
        _orchestrator = new ScriptedOrchestrator(_sessions, recorder);
        _service = new LegacyBatchService(_batches, _records, _jira, _orchestrator, _sessions, TestEnvironments.Selection(),
            new LegacyBatchOptions { PollInterval = TimeSpan.FromMilliseconds(5), RecordWait = TimeSpan.FromSeconds(2) },
            NullLogger<LegacyBatchService>.Instance);
    }

    [Test]
    public void Production_environment_is_refused()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateSnapshotAsync("11265", "KMSPROD", null, null, "Reem"));
        Assert.That(ex!.Message, Does.Contain("Production"));
    }

    [Test]
    public async Task Batch_records_its_snapshot_runs_each_ticket_once_and_never_publishes()
    {
        var batch = await _service.CreateSnapshotAsync("11265", "KMSQA", null, null, "Reem");
        Assert.That(batch.Items.Select(i => i.IssueKey), Is.EqualTo(new[] { "VKMS-1", "VKMS-2", "VKMS-3" }));

        _orchestrator.FinalStates["VKMS-2"] = AgentState.Failed;
        await _service.RunAsync(batch.Id, retryUnsuccessful: false, maxItems: 2);
        Assert.That(_orchestrator.Started, Is.EqualTo(new[] { "VKMS-1", "VKMS-2" }), "limited to 2");
        Assert.That(batch.Items[2].Status, Is.EqualTo(InvestigationExecutionStatus.Pending));

        // Resume: only the pending ticket runs; completed ones are not investigated again.
        await _service.RunAsync(batch.Id, retryUnsuccessful: false);
        Assert.That(_orchestrator.Started, Is.EqualTo(new[] { "VKMS-1", "VKMS-2", "VKMS-3" }));

        var statuses = batch.Items.Select(i => i.Status).ToList();
        Assert.That(statuses, Is.EqualTo(new[] { InvestigationExecutionStatus.Completed, InvestigationExecutionStatus.Failed, InvestigationExecutionStatus.Completed }));
        Assert.That(_orchestrator.SubmittedEnvironments.Distinct(), Is.EqualTo(new[] { "KMSQA" }));
        Assert.That(_jira.AddedComments, Is.Empty, "a batch never publishes");

        // Retry failed: only VKMS-2 runs again, and succeeds now.
        _orchestrator.FinalStates.Remove("VKMS-2");
        await _service.RunAsync(batch.Id, retryUnsuccessful: true);
        Assert.That(_orchestrator.Started.Count(k => k == "VKMS-2"), Is.EqualTo(2));
        Assert.That(_orchestrator.Started.Count(k => k == "VKMS-1"), Is.EqualTo(1));
        Assert.That(batch.Items[1].Status, Is.EqualTo(InvestigationExecutionStatus.Completed));
        Assert.That(batch.Items[1].Attempts, Is.EqualTo(2));

        var dashboard = await _service.GetDashboardAsync(batch.Id);
        Assert.That(dashboard.Total, Is.EqualTo(3));
        Assert.That(dashboard.Completed, Is.EqualTo(3));
        Assert.That(dashboard.BranchesCreated, Is.EqualTo(0));
    }

    [Test]
    public async Task A_ticket_investigated_before_is_not_investigated_again_unless_requested()
    {
        var first = await _service.CreateSnapshotAsync("11265", "KMSQA", null, null, "Reem");
        await _service.RunAsync(first.Id, retryUnsuccessful: false, maxItems: 1);   // VKMS-1
        var second = await _service.CreateSnapshotAsync("11265", "KMSQA", null, null, "Reem");

        await _service.RunAsync(second.Id, retryUnsuccessful: false, maxItems: 1);
        Assert.That(_orchestrator.Started.Count(k => k == "VKMS-1"), Is.EqualTo(1), "skipped in the second batch");
        Assert.That(second.Items[0].LastError, Does.Contain("Already investigated"));
        Assert.That(second.Items[0].RecordId, Is.Not.Null);

        await _service.RunAsync(second.Id, retryUnsuccessful: false, maxItems: 1, rerunInvestigated: true);
        Assert.That(_orchestrator.Started.Count(k => k == "VKMS-1"), Is.EqualTo(2), "explicit re-run");
    }

    [Test]
    public async Task Item_left_running_by_a_restart_is_requeued_and_membership_changes_are_reported()
    {
        var batch = await _service.CreateSnapshotAsync("11265", "KMSQA", null, null, "Reem");
        batch.Items[0].Status = InvestigationExecutionStatus.Running; // process died mid-ticket

        await _service.RunAsync(batch.Id, retryUnsuccessful: false, maxItems: 1);
        Assert.That(_orchestrator.Started, Is.EqualTo(new[] { "VKMS-1" }));
        Assert.That(batch.Items[0].Status, Is.EqualTo(InvestigationExecutionStatus.Completed));

        _jira.FilterKeys = new List<string> { "VKMS-1", "VKMS-3", "VKMS-9" };
        var (added, removed) = await _service.CheckMembershipAsync(batch.Id);
        Assert.That(added, Is.EqualTo(new[] { "VKMS-9" }));
        Assert.That(removed, Is.EqualTo(new[] { "VKMS-2" }));
        Assert.That(batch.Items, Has.Count.EqualTo(3), "the snapshot itself never changes");
        Assert.That(batch.LastMembershipChange, Does.Contain("VKMS-9").And.Contain("VKMS-2"));
    }

    [Test]
    public async Task Consolidated_report_counts_each_ticket_once_and_lists_flags_separately()
    {
        var batch = await _service.CreateSnapshotAsync("11265", "KMSQA", null, null, "Reem");
        await _service.RunAsync(batch.Id, retryUnsuccessful: false, maxItems: 2);
        var rows = ConsolidatedReportBuilder.Rows(batch, await _service.LatestRecordsAsync(batch));
        var md = ConsolidatedReportBuilder.ToMarkdown(batch, rows);

        Assert.That(md, Does.Contain("**3 tickets in scope**"));
        Assert.That(md, Does.Contain("- Completed: 2").And.Contain("- Pending: 1"));
        Assert.That(md, Does.Contain("- Not yet investigated: 1"));
        Assert.That(md, Does.Contain("No Jira issue was modified"));
        Assert.That(ConsolidatedReportBuilder.ToJson(batch, rows), Does.Contain("\"total\": 3"));
    }
}
