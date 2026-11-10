using System.Text.Json;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Reporting;
using BugInvestigationAgent.Core.Repositories;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Sessions;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Infrastructure.Forge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Infrastructure.Hosting;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";
    /// <summary>Concurrent investigations per worker replica (each owns an isolated browser).</summary>
    public int MaxConcurrentJobs { get; set; } = 3;
    /// <summary>Hard limit per job. Jira's work-item trigger allows ~60 minutes for remote agents.</summary>
    public int JobTimeoutMinutes { get; set; } = 55;
    /// <summary>How long a suspended job may wait for user input before it times out (enforced by the sweeper).</summary>
    public int AwaitingInputTimeoutMinutes { get; set; } = 30;
    /// <summary>
    /// MFA is the only input for which a worker keeps the job (the signed-in-in-progress browser page cannot be
    /// checkpointed). After this many minutes the job is suspended and the user must sign in again.
    /// </summary>
    public int MfaHoldMinutes { get; set; } = 5;
    /// <summary>Maximum total lifetime of a job (Forge user tokens live at most 4 hours).</summary>
    public int MaxJobLifetimeMinutes { get; set; } = 230;
    public int LeaseSeconds { get; set; } = 60;
    public int PollIntervalMs { get; set; } = 1500;
    /// <summary>Unique worker id (container replica name). Defaults to machine name + process id.</summary>
    public string? WorkerId { get; set; }
}

public enum JobRunOutcome
{
    /// <summary>Another worker owns the job, or it is already finished — nothing was done.</summary>
    NotLeased,
    /// <summary>The job reached a terminal state.</summary>
    Finished,
    /// <summary>The job waits for user input; state checkpointed and the worker slot released.</summary>
    Suspended,
    /// <summary>Technical error: re-queued for retry (or failed after the retry budget).</summary>
    Requeued,
    /// <summary>The lease was lost (another worker took over) or the worker is shutting down.</summary>
    Abandoned
}

/// <summary>Non-secret payload stored (encrypted) as a job secret when the user submits environment credentials.</summary>
public sealed class SubmittedCredentials
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Remember { get; set; }
}

/// <summary>
/// Executes ONE durable investigation job on a worker: runs the existing agent pipeline in-process (isolated session,
/// isolated browser, per-job identity), persists progress, maps "missing information" to Awaiting User Input,
/// feeds user replies / securely submitted credentials back in, and finishes with a structured report.
/// </summary>
public sealed class InvestigationJobRunner
{
    private readonly IInvestigationJobStore _jobs;
    private readonly IJobSecretStore _secrets;
    private readonly IAuditLog _audit;
    private readonly IAgentSessionStore _sessions;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IEnvironmentSelectionService _environments;
    private readonly IEvidenceStore _evidence;
    private readonly JiraReportPublisher _publisher;
    private readonly ISecretRedactor _redactor;
    private readonly IServiceScopeFactory _scopes;
    private readonly WorkerOptions _options;
    private readonly AgentOptions _agentOptions;
    private readonly RepositoryMappingOptions _repoOptions;
    private readonly ForgeOptions _forge;
    private readonly ILogger<InvestigationJobRunner> _logger;
    private readonly JobInputService _input;

    public InvestigationJobRunner(
        IInvestigationJobStore jobs, IJobSecretStore secrets, IAuditLog audit, IAgentSessionStore sessions,
        IAgentOrchestrator orchestrator, IEnvironmentSelectionService environments, IEvidenceStore evidence,
        JiraReportPublisher publisher, ISecretRedactor redactor, IServiceScopeFactory scopes,
        IOptions<WorkerOptions> options, IOptions<AgentOptions> agentOptions, IOptions<RepositoryMappingOptions> repoOptions,
        IOptions<ForgeOptions> forge, ILogger<InvestigationJobRunner> logger, JobInputService input)
    {
        _input = input;
        _jobs = jobs;
        _secrets = secrets;
        _audit = audit;
        _sessions = sessions;
        _orchestrator = orchestrator;
        _environments = environments;
        _evidence = evidence;
        _publisher = publisher;
        _redactor = redactor;
        _scopes = scopes;
        _options = options.Value;
        _agentOptions = agentOptions.Value;
        _repoOptions = repoOptions.Value;
        _forge = forge.Value;
        _logger = logger;
    }

    private TimeSpan Lease => TimeSpan.FromSeconds(Math.Max(15, _options.LeaseSeconds));

    /// <param name="onLeased">Invoked once the job lease is held (e.g. to complete the queue message).</param>
    public async Task<JobRunOutcome> RunAsync(Guid jobId, string workerId, CancellationToken stoppingToken, Func<Task>? onLeased = null)
    {
        // Lease owner is unique PER RUN (not per worker process): a duplicate delivery of the same job to the same
        // worker can never run it twice concurrently.
        workerId = $"{workerId}/{Guid.NewGuid():N}";
        if (!await _jobs.TryAcquireLeaseAsync(jobId, workerId, Lease, stoppingToken))
            return JobRunOutcome.NotLeased; // another worker owns it, or it is finished
        if (onLeased is not null)
            await onLeased();

        var job = await _jobs.GetAsync(jobId, stoppingToken);
        if (job is null || job.IsTerminal)
        {
            await _jobs.ReleaseLeaseAsync(jobId, workerId, CancellationToken.None);
            return JobRunOutcome.NotLeased;
        }

        var identity = HostedJobIdentity.From(job);
        using var _ = HostedJobScope.Enter(identity);
        using var logScope = _logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["IssueKey"] = job.IssueKey });

        _sessions.Remove(job.Id);
        var session = _sessions.Create(job.Id);
        var resume = !string.IsNullOrWhiteSpace(job.ContextSnapshotJson);
        if (resume)
            session.Context = RuntimeContextSnapshot.Restore(job.ContextSnapshotJson!, job.Id);
        var runStartedAt = DateTimeOffset.UtcNow;
        await _jobs.UpdateAsync(job.Id, j =>
        {
            j.Status = InvestigationJobStatus.Running;
            j.StartedAt ??= runStartedAt;
            j.StatusMessage = resume ? "Resuming the investigation."
                : j.RetryCount > 0 ? $"Restarted (attempt {j.RetryCount + 1})." : "Investigation started.";
        }, stoppingToken);
        await _jobs.AppendEventAsync(job.Id, "status", resume ? "Investigation resumed on a worker." : "Investigation started on a worker.", stoppingToken);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // Active-execution budget for this run; waiting time is not counted (the job is suspended while waiting).
        var deadline = runStartedAt.AddMinutes(_options.JobTimeoutMinutes);
        var runTask = resume
            ? _orchestrator.ResumeAsync(job.Id, runCts.Token)
            : _orchestrator.RunAsync(job.Id, job.IssueKey, null, runCts.Token);
        var loggedLines = session.Context.StatusLog.Count;
        var bridge = new InputBridge();
        var finalStatus = (InvestigationJobStatus?)null;
        string? finalMessage = null;
        var outcome = JobRunOutcome.Finished;

        try
        {
            while (!runTask.IsCompleted)
            {
                await Task.WhenAny(runTask, Task.Delay(_options.PollIntervalMs, stoppingToken));
                if (runTask.IsCompleted)
                    break;

                if (!await _jobs.RenewLeaseAsync(job.Id, workerId, Lease, stoppingToken))
                {
                    _logger.LogWarning("Lost the lease for job {JobId}; stopping local execution.", job.Id);
                    await _orchestrator.CancelAsync(job.Id);
                    await SafeAwait(runTask);
                    return JobRunOutcome.Abandoned; // another worker will continue; do not touch the job status
                }

                job = await _jobs.GetAsync(job.Id, stoppingToken) ?? job;
                loggedLines = await SyncProgressAsync(job, session, loggedLines, stoppingToken);

                if (job.CancelRequested)
                {
                    await _orchestrator.CancelAsync(job.Id);
                    finalStatus = InvestigationJobStatus.Cancelled;
                    finalMessage = "Investigation cancelled by the user.";
                    break;
                }

                if (DateTimeOffset.UtcNow > deadline)
                {
                    await _orchestrator.CancelAsync(job.Id);
                    finalStatus = InvestigationJobStatus.TimedOut;
                    finalMessage = $"Investigation exceeded the {_options.JobTimeoutMinutes}-minute limit.";
                    break;
                }

                // Feed input only while the pipeline is really blocked on it (avoids acting on a stale state).
                if (session.State is AgentState.AwaitingMissingInfo or AgentState.Blocked
                    && session.MissingInfoWaiter is { Task.IsCompleted: false })
                {
                    var decision = await HandleMissingInfoAsync(job, session, bridge, stoppingToken);
                    if (decision.TerminalStatus is { } terminal)
                    {
                        await _orchestrator.CancelAsync(job.Id);
                        (finalStatus, finalMessage) = (terminal, decision.Message!);
                        break;
                    }

                    if (decision.AwaitKind is { } kind)
                    {
                        // MFA: keep the open sign-in page for a short, bounded time; everything else releases the slot now.
                        var holdExpired = kind == AwaitingInputKinds.MfaCode &&
                                          DateTimeOffset.UtcNow - bridge.AwaitingSince > TimeSpan.FromMinutes(_options.MfaHoldMinutes);
                        if (kind != AwaitingInputKinds.MfaCode || holdExpired)
                        {
                            await SuspendAsync(job, session, holdExpired, stoppingToken);
                            await _orchestrator.CancelAsync(job.Id);
                            await SafeAwait(runTask);
                            outcome = JobRunOutcome.Suspended;
                            return outcome;
                        }
                    }
                }
            }

            await SafeAwait(runTask);
            loggedLines = await SyncProgressAsync(job, session, loggedLines, CancellationToken.None);

            if (finalStatus is null)
            {
                (finalStatus, finalMessage) = session.State switch
                {
                    AgentState.Completed => (InvestigationJobStatus.Completed, session.Context.FinalStatusMessage ?? "Investigation completed."),
                    _ => (InvestigationJobStatus.Failed, _redactor.Redact(session.LastError ?? session.Context.ReproductionNotes ?? "Investigation stopped."))
                };
            }

            await FinishAsync(job, session, finalStatus.Value, finalMessage!, CancellationToken.None);
            return JobRunOutcome.Finished;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Worker shutting down: leave the job non-terminal; the lease expires and recovery re-queues it.
            await _orchestrator.CancelAsync(job.Id);
            await SafeAwait(runTask);
            outcome = JobRunOutcome.Abandoned;
            return outcome;
        }
        catch (Exception ex)
        {
            _logger.LogError("Job {JobId} crashed: {Message}", job.Id, _redactor.Redact(ex.Message));
            await _orchestrator.CancelAsync(job.Id);
            await SafeAwait(runTask);
            await RetryOrFailAsync(job.Id, _redactor.Redact(ex.Message), CancellationToken.None);
            return JobRunOutcome.Requeued;
        }
        finally
        {
            _sessions.Remove(job.Id);
            TryDeleteDirectory(_repoOptions.CheckoutDirectoryFor(job.Id));
            await _jobs.ReleaseLeaseAsync(job.Id, workerId, CancellationToken.None);
            if (outcome == JobRunOutcome.Suspended)
                await _input.WakeIfInputPendingAsync(job.Id, CancellationToken.None); // input that raced the suspension
        }
    }

    /// <summary>
    /// Checkpoints the investigation (secret-free context JSON), keeps evidence captured so far, and leaves the job in
    /// Awaiting User Input WITHOUT a worker. It resumes on any worker when the user responds.
    /// </summary>
    private async Task SuspendAsync(InvestigationJob job, AgentSession session, bool mfaHoldExpired, CancellationToken ct)
    {
        var ctx = session.Context;
        var snapshot = RuntimeContextSnapshot.Serialize(ctx);
        try
        {
            await _evidence.PublishAsync(job.Id, Path.Combine(EvidenceRoot(), job.Id.ToString("N")), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Evidence upload at suspension failed for job {JobId}: {Message}", job.Id, _redactor.Redact(ex.Message));
        }

        await _jobs.UpdateAsync(job.Id, j =>
        {
            j.ContextSnapshotJson = snapshot;
            j.Status = InvestigationJobStatus.AwaitingUserInput;
            if (mfaHoldExpired)
            {
                j.AwaitingInputKind = AwaitingInputKinds.Credentials;
                j.AwaitingInputPrompt = j.StatusMessage =
                    $"The verification step was not completed within {_options.MfaHoldMinutes} minutes, so the sign-in was discarded. " +
                    $"Submit your {j.EnvironmentId ?? ctx.SelectedEnvironmentId} credentials again on the agent's personal settings page ({SettingsLink(j)}), then reply \"continue\".";
                j.AwaitingSinceUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }, ct);
        await _jobs.AppendEventAsync(job.Id, "status", "Waiting for user input — investigation checkpointed, worker released.", ct);
    }

    private sealed class InputBridge
    {
        public string? LastSubmittedFingerprint;
        public DateTimeOffset? AwaitingSince;
        public void Reset()
        {
            LastSubmittedFingerprint = null;
            AwaitingSince = null;
        }
    }

    /// <summary>
    /// Satisfies the pipeline's missing information from job inputs, or moves the job to Awaiting User Input.
    /// Returns a terminal outcome when the job cannot continue.
    /// </summary>
    private sealed record InputDecision(InvestigationJobStatus? TerminalStatus = null, string? Message = null, string? AwaitKind = null)
    {
        public static readonly InputDecision Continue = new();
        public static InputDecision Terminal(InvestigationJobStatus status, string message) => new(status, message);
        public static InputDecision Await(string kind) => new(null, null, kind);
    }

    private async Task<InputDecision> HandleMissingInfoAsync(
        InvestigationJob job, AgentSession session, InputBridge bridge, CancellationToken ct)
    {
        var ctx = session.Context;
        if (ctx.MissingFields.Count == 0)
            return InputDecision.Continue; // pipeline is about to continue (input already accepted)
        var keys = ctx.MissingFields.Select(f => f.Key).Where(k => k != "reproductionSteps").ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (keys.Contains("jiraSettings"))
            return InputDecision.Terminal(InvestigationJobStatus.Failed, "The issue could not be read with your Jira permissions (or the authorization expired). Invoke the agent again from the issue.");
        if (keys.Contains("workspacePath"))
            return InputDecision.Terminal(InvestigationJobStatus.Failed, "No source repository is mapped for this project (Repositories:Mappings).");

        // Apply a pending free-text reply (environment choice / MFA approval).
        if (!string.IsNullOrWhiteSpace(job.PendingUserReply))
        {
            var reply = job.PendingUserReply!;
            var envFromReply = _environments.MatchEnvironmentReply(reply);
            var allow = reply.Contains("allow state-changing", StringComparison.OrdinalIgnoreCase);
            job = await _jobs.UpdateAsync(job.Id, j =>
            {
                if (envFromReply is not null && envFromReply != j.EnvironmentId)
                {
                    j.EnvironmentId = envFromReply;
                    j.AllowStateChangingActions = false;
                }
                if (allow)
                    j.AllowStateChangingActions = true;
            }, ct) ?? job;
            if (envFromReply is not null)
                await _audit.WriteAsync(new AuditEntry
                {
                    TenantId = job.TenantId, AccountId = job.InvokingAccountId, JobId = job.Id,
                    Action = AuditActions.EnvironmentSelected, Detail = $"Environment {envFromReply} selected in Jira."
                }, ct);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CredentialSecret? credentials = null;
        var remember = false;
        string? awaitingKind = null;
        string? prompt = null;

        if (keys.Contains(MissingInfoResolver.AuthenticationActionFieldKey))
        {
            var code = await _secrets.TakeAsync(job.Id, JobSecretKinds.MfaCode, ct);
            var approved = job.PendingUserReply is { } r &&
                           (r.Contains("approved", StringComparison.OrdinalIgnoreCase) || r.Contains("done", StringComparison.OrdinalIgnoreCase)
                            || r.Contains("continue", StringComparison.OrdinalIgnoreCase));
            if (code is not null || approved)
            {
                values["authAction"] = "recheck";
                if (code is not null)
                    values["mfaCode"] = code;
            }
            else
            {
                awaitingKind = AwaitingInputKinds.MfaCode;
                prompt = (ctx.AuthenticationMessage ?? "The environment asks for additional verification.") +
                         $" Enter the verification code on the agent's personal settings page ({SettingsLink(job)}), " +
                         "or approve the sign-in on your device and reply \"approved\".";
            }
        }
        else
        {
            if (keys.Contains(MissingInfoResolver.EnvironmentFieldKey))
            {
                var check = job.EnvironmentId is null ? null : _environments.Validate(job.EnvironmentId);
                if (check is { IsValid: true })
                {
                    values["environmentId"] = check.Environment!.Id;
                    values["allowStateChangingActions"] = job.AllowStateChangingActions ? "true" : "false";
                }
                else
                {
                    awaitingKind = AwaitingInputKinds.Environment;
                    prompt = EnvironmentPrompt(ctx, check?.Error);
                }
            }

            var envId = values.TryGetValue("environmentId", out var e) ? e : ctx.SelectedEnvironmentId;
            if (awaitingKind is null && keys.Contains(MissingInfoResolver.CredentialsFieldKey) && envId is not null)
            {
                var raw = await _secrets.TakeAsync(job.Id, JobSecretKinds.EnvironmentCredentials(envId), ct);
                if (raw is not null)
                {
                    var submitted = JsonSerializer.Deserialize<SubmittedCredentials>(raw);
                    raw = null;
                    if (submitted is not null && submitted.Username.Length > 0 && submitted.Password.Length > 0)
                    {
                        credentials = new CredentialSecret { Username = submitted.Username, Password = submitted.Password };
                        remember = submitted.Remember;
                    }
                }

                if (credentials is null)
                {
                    using var scope = _scopes.CreateScope();
                    var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();
                    var saved = await vault.FindForEnvironmentAsync(envId, null, ct);
                    // Saved (opted-in) credentials are used once per job; after a failed sign-in, ask the user instead.
                    if (saved is not null && ctx.AuthenticationStatus is not (AuthenticationStatus.InvalidCredentials or AuthenticationStatus.SessionExpired))
                        values["credentialProfileId"] = saved.Id.ToString();
                    else
                    {
                        awaitingKind = AwaitingInputKinds.Credentials;
                        prompt = $"{(ctx.AuthenticationStatus is AuthenticationStatus.InvalidCredentials ? "Sign-in was rejected. " : "")}" +
                                 $"Enter your {envId} username and password on the agent's personal settings page: {SettingsLink(job)} " +
                                 "(do NOT type passwords in this chat), then reply \"continue\" here.";
                    }
                }
            }
        }

        if (awaitingKind is not null)
        {
            bridge.AwaitingSince ??= DateTimeOffset.UtcNow;
            if (job.Status != InvestigationJobStatus.AwaitingUserInput || job.AwaitingInputPrompt != prompt || job.PendingUserReply is not null)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await _jobs.UpdateAsync(job.Id, j =>
                {
                    // The input timeout keeps running across resumes for the same question (no reset by unrelated replies).
                    if (j.AwaitingInputKind != awaitingKind || j.AwaitingSinceUnixMs == 0)
                        j.AwaitingSinceUnixMs = now;
                    j.Status = InvestigationJobStatus.AwaitingUserInput;
                    j.AwaitingInputKind = awaitingKind;
                    j.AwaitingInputPrompt = prompt;
                    j.StatusMessage = prompt;
                    j.PendingUserReply = null;
                    if (awaitingKind is AwaitingInputKinds.Credentials or AwaitingInputKinds.MfaCode)
                        j.EnvironmentId ??= ctx.SelectedEnvironmentId;
                }, ct);
                await _jobs.AppendEventAsync(job.Id, "input", prompt!, ct);
            }
            return InputDecision.Await(awaitingKind);
        }

        // Everything needed is available — feed it to the pipeline (once per distinct input set).
        var fingerprint = string.Join("|", values.OrderBy(v => v.Key).Select(v => v.Key == "mfaCode" ? "mfa" : $"{v.Key}={v.Value}"))
                          + (credentials is null ? "" : "|creds:" + Guid.NewGuid());
        if (fingerprint == bridge.LastSubmittedFingerprint && credentials is null)
            return InputDecision.Continue;
        bridge.LastSubmittedFingerprint = fingerprint;
        bridge.AwaitingSince = null;

        await _jobs.UpdateAsync(job.Id, j =>
        {
            j.Status = InvestigationJobStatus.Running;
            j.AwaitingInputKind = null;
            j.AwaitingInputPrompt = null;
            j.AwaitingSinceUnixMs = 0;
            j.PendingUserReply = null;
            j.StatusMessage = "Input received — continuing.";
        }, ct);

        if (credentials is not null)
            await _audit.WriteAsync(new AuditEntry
            {
                TenantId = job.TenantId, AccountId = job.InvokingAccountId, JobId = job.Id,
                Action = AuditActions.AuthenticationAttempt,
                Detail = $"Sign-in attempt to {values.GetValueOrDefault("environmentId") ?? ctx.SelectedEnvironmentId} with user-supplied credentials{(remember ? " (remember opted in)" : "")}."
            }, ct);

        await _orchestrator.SubmitMissingInfoAsync(job.Id, values, credentials, remember, ct);
        if (remember && credentials is not null)
            await _audit.WriteAsync(new AuditEntry
            {
                TenantId = job.TenantId, AccountId = job.InvokingAccountId, JobId = job.Id,
                Action = AuditActions.CredentialsSaved, Detail = "Credentials saved encrypted for this user and environment (opt-in)."
            }, ct);
        credentials = null;
        return InputDecision.Continue;
    }

    private string EnvironmentPrompt(RuntimeContext ctx, string? error)
    {
        var envs = _environments.ListForApplication(ctx.ApplicationId)
            .Where(e => _environments.Validate(e.Id).IsValid)
            .Select(e => $"{e.Id} ({e.Type}{(e.IsProduction ? ", read-only" : "")})")
            .ToList();
        var suggestion = ctx.SuggestedEnvironmentId is { } s && s != EnvironmentSelectionService.AdHocOptionId
            ? $" Suggested from the ticket: {s} — {ctx.SuggestedEnvironmentReason}"
            : $" {ctx.SuggestedEnvironmentReason}";
        return (error is null ? "" : error + " ") +
               (envs.Count == 0
                   ? "No environments are configured on the agent service. Ask an administrator to register them."
                   : $"Which environment should I investigate? Reply with one of: {string.Join(", ", envs)}.{suggestion}");
    }

    private string EvidenceRoot() => Path.IsPathRooted(_agentOptions.EvidenceDirectory)
        ? _agentOptions.EvidenceDirectory
        : Path.Combine(Directory.GetCurrentDirectory(), _agentOptions.EvidenceDirectory);

    private string SettingsLink(InvestigationJob job) =>
        $"{job.SiteUrl?.TrimEnd('/')}/jira/settings/personal/apps/{_forge.AppId}/{job.ForgeEnvironmentId ?? _forge.EnvironmentId}";

    private async Task<int> SyncProgressAsync(InvestigationJob job, AgentSession session, int alreadyLogged, CancellationToken ct)
    {
        var lines = session.Context.StatusLog.SafeSnapshot(); // consistent copy while the pipeline keeps logging
        if (lines.Count <= alreadyLogged)
            return alreadyLogged;
        foreach (var line in lines.Skip(alreadyLogged).TakeLast(20))
            await _jobs.AppendEventAsync(job.Id, "progress", line, ct);
        if (job.Status == InvestigationJobStatus.Running)
        {
            var last = _redactor.Redact(lines[^1]);
            await _jobs.UpdateAsync(job.Id, j => j.StatusMessage = $"{AgentStateDisplay.Label(session.State)}: {(last.Length > 300 ? last[..300] + "…" : last)}", ct);
        }
        return lines.Count;
    }

    private async Task FinishAsync(InvestigationJob job, AgentSession session, InvestigationJobStatus status, string message, CancellationToken ct)
    {
        var ctx = session.Context;
        string? reportJson = null;
        string? summary = null;
        string? evidenceRef = null;

        // A report is produced whenever the investigation got far enough to have something to say.
        if (ctx.JiraDetailsLoaded && status is InvestigationJobStatus.Completed or InvestigationJobStatus.Failed or InvestigationJobStatus.TimedOut)
        {
            var report = InvestigationReportBuilder.Build(ctx, _redactor, ctx.RepositoryDisplay);
            reportJson = report.ToJson();
            summary = InvestigationReportBuilder.ToMarkdown(report);
            if (status != InvestigationJobStatus.Completed)
                summary = $"**{status}:** {message}\n\n" + summary;

            try
            {
                evidenceRef = await _evidence.PublishAsync(job.Id, Path.Combine(EvidenceRoot(), job.Id.ToString("N")), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Evidence upload failed for job {JobId}: {Message}", job.Id, _redactor.Redact(ex.Message));
            }
        }

        await _jobs.UpdateAsync(job.Id, j =>
        {
            j.Status = status;
            j.StatusMessage = message;
            j.CompletedAt = DateTimeOffset.UtcNow;
            j.AwaitingInputKind = null;
            j.AwaitingInputPrompt = null;
            j.ReproductionOutcome = ctx.ReproductionOutcome.ToString();
            j.ResultSummary = summary ?? message;
            j.ReportJson = reportJson ?? j.ReportJson;
            j.EvidenceReference = evidenceRef ?? j.EvidenceReference;
            j.ErrorDetails = status == InvestigationJobStatus.Completed ? null : message;
            j.ContextSnapshotJson = null;
            j.AwaitingSinceUnixMs = 0;
        }, ct);
        await _jobs.AppendEventAsync(job.Id, "status", $"{status}: {message}", ct);
        await _audit.WriteAsync(new AuditEntry
        {
            TenantId = job.TenantId, AccountId = job.InvokingAccountId, JobId = job.Id,
            Action = status == InvestigationJobStatus.Completed ? AuditActions.InvestigationCompleted
                : status == InvestigationJobStatus.Cancelled ? AuditActions.InvestigationCancelled
                : AuditActions.InvestigationFailed,
            Detail = $"{status}: {message}",
            Success = status == InvestigationJobStatus.Completed
        }, ct);

        // Environment credentials, MFA codes and the user token are no longer needed. Keep the app token until published.
        await _secrets.DeleteAllAsync(job.Id, [JobSecretKinds.JiraSystemToken], ct);
        if (reportJson is not null)
        {
            try
            {
                if (await _publisher.PublishAsync(job.Id, ct))
                    await _secrets.DeleteAllAsync(job.Id, null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Publishing the report for job {JobId} failed; it will be retried: {Message}", job.Id, _redactor.Redact(ex.Message));
            }
        }
        else
        {
            await _secrets.DeleteAllAsync(job.Id, null, ct);
        }
    }

    private async Task RetryOrFailAsync(Guid jobId, string error, CancellationToken ct)
    {
        var job = await _jobs.UpdateAsync(jobId, j =>
        {
            if (j.RetryCount < j.MaxRetries)
            {
                j.RetryCount++;
                j.Status = InvestigationJobStatus.Queued;
                j.StatusMessage = $"Technical error — retrying (attempt {j.RetryCount + 1}).";
                j.NotBeforeUnixMs = DateTimeOffset.UtcNow.AddSeconds(30 * j.RetryCount).ToUnixTimeMilliseconds();
            }
            else
            {
                j.Status = InvestigationJobStatus.Failed;
                j.CompletedAt = DateTimeOffset.UtcNow;
                j.StatusMessage = "Investigation failed after retries.";
            }
            j.ErrorDetails = error;
        }, ct);
        if (job?.Status == InvestigationJobStatus.Failed)
            await _secrets.DeleteAllAsync(jobId, null, ct);
    }

    private static async Task SafeAwait(Task task)
    {
        try { await task; } catch { /* outcome is read from the session */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal); // git objects are read-only
                Directory.Delete(path, recursive: true);
            }
        }
        catch { /* best effort */ }
    }
}
