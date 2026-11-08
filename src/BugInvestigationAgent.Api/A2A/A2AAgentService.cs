using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Infrastructure.Forge;

namespace BugInvestigationAgent.Api.A2A;

/// <summary>Tokens forwarded by Forge with a remote invocation (never logged, never returned).</summary>
public sealed record ForgeTokens(string? UserToken, string? SystemToken);

/// <summary>
/// Implements the A2A methods Jira calls on a Forge remote agent: SendMessage (new invocation or follow-up reply),
/// GetTask (progress polling), CancelTask. Identity and tenancy come only from the validated Forge Invocation Token
/// and the user's own token — never from the JSON body.
/// </summary>
public sealed class A2AAgentService
{
    private static readonly TimeSpan UserTokenTtl = TimeSpan.FromHours(3.5);   // Forge: max 4h
    private static readonly TimeSpan SystemTokenTtl = TimeSpan.FromMinutes(110); // Forge: min 2h

    private readonly IInvestigationJobStore _jobs;
    private readonly IJobSecretStore _secrets;
    private readonly IInvestigationQueue _queue;
    private readonly IAuditLog _audit;
    private readonly JiraCloudClient _jira;
    private readonly ILogger<A2AAgentService> _logger;

    public A2AAgentService(IInvestigationJobStore jobs, IJobSecretStore secrets, IInvestigationQueue queue, IAuditLog audit,
        JiraCloudClient jira, ILogger<A2AAgentService> logger)
    {
        _jobs = jobs;
        _secrets = secrets;
        _queue = queue;
        _audit = audit;
        _jira = jira;
        _logger = logger;
    }

    public async Task<JsonObject> HandleAsync(JsonDocument request, ForgeInvocation fit, ForgeTokens tokens, CancellationToken ct)
    {
        var root = request.RootElement;
        JsonNode? id = root.TryGetProperty("id", out var idEl) ? JsonNode.Parse(idEl.GetRawText()) : null;
        if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            return A2AJson.Error(id, A2AErrors.InvalidRequest, "Missing method.");
        var @params = root.TryGetProperty("params", out var p) ? p : default;

        return methodEl.GetString() switch
        {
            "SendMessage" => await SendMessageAsync(id, @params, fit, tokens, ct),
            "GetTask" => await GetTaskAsync(id, @params, fit, tokens, ct),
            "CancelTask" => await CancelTaskAsync(id, @params, fit, tokens, ct),
            "SendStreamingMessage" or "SubscribeToTask" =>
                A2AJson.Error(id, A2AErrors.UnsupportedOperation, "Streaming is not enabled for this agent; Jira polls GetTask."),
            _ => A2AJson.Error(id, A2AErrors.MethodNotFound, "Unsupported method.")
        };
    }

    // ── SendMessage ─────────────────────────────────────────────────────────
    private async Task<JsonObject> SendMessageAsync(JsonNode? id, JsonElement @params, ForgeInvocation fit, ForgeTokens tokens, CancellationToken ct)
    {
        if (@params.ValueKind != JsonValueKind.Object)
            return A2AJson.Error(id, A2AErrors.InvalidParams, "Missing params.");
        var message = IncomingMessage.Parse(@params);

        // Authoritative user = the account behind the user's own token (Atlassian-issued), cross-checked with the FIT.
        var installation = await _jobs.GetInstallationAsync(fit.InstallationId, ct);
        var cloudId = fit.CloudId ?? installation?.CloudId;
        if (string.IsNullOrWhiteSpace(cloudId))
            return Reply(id, Guid.NewGuid().ToString(), message.ContextId, A2ATaskStates.AuthRequired,
                "The agent is not configured for this Jira site yet (no installation record). Ask a Jira admin to reinstall or upgrade the app.");
        var apiBase = JiraCloudUrls.ApiBase(cloudId, fit.ApiBaseUrl ?? installation?.ApiBaseUrl);

        if (string.IsNullOrWhiteSpace(tokens.UserToken))
            return Reply(id, Guid.NewGuid().ToString(), message.ContextId, A2ATaskStates.AuthRequired,
                "Jira did not provide your user authorization, so the agent cannot read the issue on your behalf.");

        var me = await _jira.GetMyselfAsync(apiBase, tokens.UserToken, ct);
        if (me is null)
            return Reply(id, Guid.NewGuid().ToString(), message.ContextId, A2ATaskStates.AuthRequired,
                "Your Jira authorization could not be verified (it may have expired). Invoke the agent again.");
        if ((fit.PrincipalAccountId is not null && fit.PrincipalAccountId != me.AccountId) ||
            (message.UserAccountId is not null && message.UserAccountId != me.AccountId))
        {
            await Audit(fit.InstallationId, me.AccountId, null, AuditActions.AccessDenied, "Invoking user mismatch between token and request.", false, ct);
            return Reply(id, Guid.NewGuid().ToString(), message.ContextId, A2ATaskStates.Rejected, "The request could not be attributed to you.");
        }

        // Follow-up in an existing conversation: only the invoking user's own job is visible (user isolation).
        if (!string.IsNullOrWhiteSpace(message.ContextId))
        {
            var existing = await _jobs.FindByContextAsync(fit.InstallationId, message.ContextId!, me.AccountId, ct);
            if (existing is not null)
                return await FollowUpAsync(id, existing, message, tokens, me.AccountId, ct);
        }

        var issueRef = message.IssueId ?? message.IssueKey ?? ExtractIssueKey(message.Text);
        var contextId = message.ContextId ?? Guid.NewGuid().ToString();
        if (string.IsNullOrWhiteSpace(issueRef))
            return Reply(id, Guid.NewGuid().ToString(), contextId, A2ATaskStates.Rejected,
                "Assign me to a bug or @mention me on the issue (or include an issue key such as ABC-123) to start an investigation.");

        // Permission check: the issue is read WITH THE USER'S TOKEN — no access, no investigation.
        var (issue, status) = await _jira.GetIssueAsUserAsync(apiBase, tokens.UserToken, issueRef!, ct);
        if (issue is null)
        {
            await Audit(fit.InstallationId, me.AccountId, null, AuditActions.AccessDenied,
                $"Issue {issueRef} not readable by the invoking user (HTTP {(int)status}).", false, ct);
            return Reply(id, Guid.NewGuid().ToString(), contextId, A2ATaskStates.Rejected,
                status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                    ? "You don't have access to that issue (or it does not exist), so I can't investigate it."
                    : $"Jira returned HTTP {(int)status} when reading the issue.");
        }

        var job = await _jobs.CreateAsync(new InvestigationJob
        {
            TenantId = fit.InstallationId,
            CloudId = cloudId,
            SiteUrl = fit.SiteUrl ?? installation?.SiteUrl,
            ApiBaseUrl = apiBase.ToString(),
            IssueId = issue.Id,
            IssueKey = issue.Key,
            InvokingAccountId = me.AccountId,
            InvocationType = message.InvocationType ?? "CHAT",
            ContextId = contextId,
            ForgeEnvironmentId = fit.EnvironmentId,
            StatusMessage = $"Investigation of {issue.Key} queued."
        }, ct);

        await StoreTokensAsync(job.Id, tokens, includeUser: true, ct);
        await Audit(job.TenantId, job.InvokingAccountId, job.Id, AuditActions.InvestigationCreated,
            $"Investigation of {issue.Key} requested via {job.InvocationType}.", true, ct);
        await _jobs.AppendEventAsync(job.Id, "status", $"Queued by {job.InvocationType}.", ct);
        await _queue.EnqueueAsync(job.Id, null, ct);

        return A2AJson.Result(id, A2AJson.Task(job.Id.ToString(), contextId, A2ATaskStates.Submitted,
            $"Investigation of {issue.Key} queued. I'll read the ticket with your permissions, then ask which environment to use."));
    }

    private async Task<JsonObject> FollowUpAsync(JsonNode? id, InvestigationJob job, IncomingMessage message, ForgeTokens tokens, string accountId, CancellationToken ct)
    {
        await StoreTokensAsync(job.Id, tokens, includeUser: true, ct);
        var text = message.Text;

        if (Regex.IsMatch(text, @"^\s*(cancel|stop|abort)\b", RegexOptions.IgnoreCase) && !job.IsTerminal)
        {
            job = await RequestCancelAsync(job, accountId, ct);
            return A2AJson.Result(id, ToTask(job));
        }

        if (job.IsTerminal)
        {
            if (Regex.IsMatch(text, @"\b(retry|rerun|re-run|again|restart)\b", RegexOptions.IgnoreCase))
            {
                var retry = await _jobs.CreateAsync(new InvestigationJob
                {
                    TenantId = job.TenantId, CloudId = job.CloudId, SiteUrl = job.SiteUrl, ApiBaseUrl = job.ApiBaseUrl,
                    IssueId = job.IssueId, IssueKey = job.IssueKey, InvokingAccountId = job.InvokingAccountId,
                    InvocationType = "RERUN", ContextId = job.ContextId, ForgeEnvironmentId = job.ForgeEnvironmentId,
                    EnvironmentId = job.EnvironmentId, StatusMessage = $"Re-investigation of {job.IssueKey} queued."
                }, ct);
                await StoreTokensAsync(retry.Id, tokens, includeUser: true, ct);
                await Audit(retry.TenantId, accountId, retry.Id, AuditActions.InvestigationCreated, $"Re-run of {job.IssueKey}.", true, ct);
                await _queue.EnqueueAsync(retry.Id, null, ct);
                return A2AJson.Result(id, ToTask(retry));
            }
            return A2AJson.Result(id, ToTask(job));
        }

        if (LooksLikeSecret(text))
        {
            // Never keep something that looks like a password in the job record or chat-derived state.
            return A2AJson.Result(id, A2AJson.Task(job.Id.ToString(), job.ContextId!, A2ATaskStates.InputRequired,
                "Please don't type passwords or codes in this chat. Use the agent's personal settings page in Jira, then reply \"continue\"."));
        }

        job = await _jobs.UpdateAsync(job.Id, j =>
        {
            j.PendingUserReply = text.Length > 500 ? text[..500] : text;
            if (j.Status == InvestigationJobStatus.AwaitingUserInput)
            {
                j.Status = InvestigationJobStatus.Running;
                j.StatusMessage = "Thanks — continuing the investigation.";
            }
        }, ct) ?? job;
        await _jobs.AppendEventAsync(job.Id, "reply", "User replied in Jira.", ct);
        return A2AJson.Result(id, ToTask(job));
    }

    // ── GetTask / CancelTask ───────────────────────────────────────────────
    private async Task<JsonObject> GetTaskAsync(JsonNode? id, JsonElement @params, ForgeInvocation fit, ForgeTokens tokens, CancellationToken ct)
    {
        var job = await FindTaskAsync(@params, fit, ct);
        if (job is null)
            return A2AJson.Error(id, A2AErrors.TaskNotFound, "Task not found.");

        // Polls keep the job's Jira authorizations fresh (user token only when the poll is for the same user).
        await StoreTokensAsync(job.Id, tokens, includeUser: fit.PrincipalAccountId == job.InvokingAccountId, ct);
        return A2AJson.Result(id, ToTask(job));
    }

    private async Task<JsonObject> CancelTaskAsync(JsonNode? id, JsonElement @params, ForgeInvocation fit, ForgeTokens tokens, CancellationToken ct)
    {
        var job = await FindTaskAsync(@params, fit, ct);
        if (job is null)
            return A2AJson.Error(id, A2AErrors.TaskNotFound, "Task not found.");
        if (job.IsTerminal)
            return A2AJson.Error(id, A2AErrors.TaskNotCancelable, "Task already finished.");

        job = await RequestCancelAsync(job, fit.PrincipalAccountId ?? job.InvokingAccountId, ct);
        return A2AJson.Result(id, ToTask(job));
    }

    private async Task<InvestigationJob?> FindTaskAsync(JsonElement @params, ForgeInvocation fit, CancellationToken ct)
    {
        if (@params.ValueKind != JsonValueKind.Object ||
            !@params.TryGetProperty("id", out var taskIdEl) || !Guid.TryParse(taskIdEl.GetString(), out var taskId))
            return null;
        var job = await _jobs.GetAsync(taskId, ct);
        // Tenant isolation always; user isolation whenever Jira identifies the caller.
        if (job is null || job.TenantId != fit.InstallationId)
            return null;
        if (fit.PrincipalAccountId is not null && fit.PrincipalAccountId != job.InvokingAccountId)
            return null;
        return job;
    }

    private async Task<InvestigationJob> RequestCancelAsync(InvestigationJob job, string accountId, CancellationToken ct)
    {
        var updated = await _jobs.UpdateAsync(job.Id, j =>
        {
            j.CancelRequested = true;
            if (j.Status == InvestigationJobStatus.Queued || j.LeaseOwner is null)
            {
                j.Status = InvestigationJobStatus.Cancelled;
                j.CompletedAt = DateTimeOffset.UtcNow;
            }
            j.StatusMessage = "Investigation cancelled.";
        }, ct) ?? job;
        await Audit(job.TenantId, accountId, job.Id, AuditActions.InvestigationCancelled, $"Cancel requested for {job.IssueKey}.", true, ct);
        return updated; // a running job stops at the worker's next check (reported as cancelled immediately)
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    internal static JsonObject ToTask(InvestigationJob job)
    {
        var cancelling = job.CancelRequested && !job.IsTerminal;
        var state = cancelling ? A2ATaskStates.Canceled : A2ATaskStates.For(job.Status);
        var text = cancelling ? "Investigation cancelled." : job.Status switch
        {
            InvestigationJobStatus.Queued => job.StatusMessage ?? $"Investigation of {job.IssueKey} queued.",
            InvestigationJobStatus.AwaitingUserInput => job.AwaitingInputPrompt ?? "I need more information to continue.",
            InvestigationJobStatus.Completed => (job.ResultSummary ?? "Investigation completed.") +
                (job.ReportPublishedAt is not null ? "\n\nThe full report was posted as a comment on the issue." : ""),
            InvestigationJobStatus.TimedOut => $"Timed out: {job.StatusMessage}",
            _ => job.ResultSummary ?? job.StatusMessage ?? job.Status.ToString()
        };
        return A2AJson.Task(job.Id.ToString(), job.ContextId ?? job.Id.ToString(), state, text);
    }

    private static JsonObject Reply(JsonNode? id, string taskId, string? contextId, string state, string text) =>
        A2AJson.Result(id, A2AJson.Task(taskId, contextId ?? Guid.NewGuid().ToString(), state, text));

    private async Task StoreTokensAsync(Guid jobId, ForgeTokens tokens, bool includeUser, CancellationToken ct)
    {
        if (includeUser && !string.IsNullOrWhiteSpace(tokens.UserToken))
            await _secrets.SetAsync(jobId, JobSecretKinds.JiraUserToken, tokens.UserToken, DateTimeOffset.UtcNow.Add(UserTokenTtl), ct);
        if (!string.IsNullOrWhiteSpace(tokens.SystemToken))
            await _secrets.SetAsync(jobId, JobSecretKinds.JiraSystemToken, tokens.SystemToken, DateTimeOffset.UtcNow.Add(SystemTokenTtl), ct);
    }

    private Task Audit(string tenant, string? account, Guid? jobId, string action, string detail, bool success, CancellationToken ct) =>
        _audit.WriteAsync(new AuditEntry { TenantId = tenant, AccountId = account, JobId = jobId, Action = action, Detail = detail, Success = success }, ct);

    internal static string? ExtractIssueKey(string text)
    {
        var m = Regex.Match(text ?? "", @"\b[A-Z][A-Z0-9]{1,9}-\d+\b");
        return m.Success ? m.Value : null;
    }

    internal static bool LooksLikeSecret(string text) =>
        Regex.IsMatch(text, @"(?i)\b(password|passwd|pwd|passcode|otp|token)\b\s*[:=]?\s*\S+") ||
        Regex.IsMatch(text, @"^\s*\d{6,8}\s*$"); // bare one-time codes
}
