using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Browser;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Memory;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Rules;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Sessions;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Core.Orchestration;

public interface IAgentOrchestrator
{
    /// <summary>Starts the pipeline in the background (standalone UI).</summary>
    Task StartAsync(Guid sessionId, string jiraTicketOrUrl, string? workspacePath, CancellationToken cancellationToken = default);
    /// <summary>Runs the pipeline and completes when the investigation ends (hosted workers).</summary>
    Task RunAsync(Guid sessionId, string jiraTicketOrUrl, string? workspacePath, CancellationToken cancellationToken = default);
    /// <summary>Cancels one investigation only.</summary>
    Task CancelAsync(Guid sessionId);
    Task SubmitMissingInfoAsync(Guid sessionId, IDictionary<string, string> values, CredentialSecret? newCredentials, bool saveCredentialProfile, CancellationToken cancellationToken = default);
    Task ApproveFixAsync(Guid sessionId, bool approve, CancellationToken cancellationToken = default);
    Task SaveLessonAsync(Guid sessionId, string lesson, MemoryScope scope, IEnumerable<string>? tags, string? validatedBy, CancellationToken cancellationToken = default);
}

public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentSessionStore _sessions;
    private readonly ISecretRedactor _redactor;
    private readonly IMissingInfoResolver _missingInfoResolver;
    private readonly IPermissionGate _permissions;
    private readonly IWorkspaceDefaults _workspaceDefaults;
    private readonly IBrowserSessionCloser _browserSessions;
    private readonly IEnvironmentSelectionService _environments;
    private readonly ISessionCredentialStore _sessionCredentials;
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly object _runGate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _runs = new();

    public AgentOrchestrator(
        IServiceScopeFactory scopeFactory,
        IAgentSessionStore sessions,
        ISecretRedactor redactor,
        IMissingInfoResolver missingInfoResolver,
        IPermissionGate permissions,
        IWorkspaceDefaults workspaceDefaults,
        IBrowserSessionCloser browserSessions,
        IEnvironmentSelectionService environments,
        ISessionCredentialStore sessionCredentials,
        ILogger<AgentOrchestrator> logger)
    {
        _environments = environments;
        _sessionCredentials = sessionCredentials;
        _scopeFactory = scopeFactory;
        _sessions = sessions;
        _redactor = redactor;
        _missingInfoResolver = missingInfoResolver;
        _permissions = permissions;
        _workspaceDefaults = workspaceDefaults;
        _browserSessions = browserSessions;
        _logger = logger;
    }

    public Task StartAsync(Guid sessionId, string jiraTicketOrUrl, string? workspacePath, CancellationToken cancellationToken = default)
    {
        var (session, token) = PrepareRun(sessionId, jiraTicketOrUrl, workspacePath, cancellationToken);
        _ = Task.Run(() => ExecuteRunAsync(session, token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task RunAsync(Guid sessionId, string jiraTicketOrUrl, string? workspacePath, CancellationToken cancellationToken = default)
    {
        var (session, token) = PrepareRun(sessionId, jiraTicketOrUrl, workspacePath, cancellationToken);
        await ExecuteRunAsync(session, token);
    }

    public Task CancelAsync(Guid sessionId)
    {
        CancellationTokenSource? cts;
        lock (_runGate)
            _runs.TryGetValue(sessionId, out cts);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        var session = _sessions.Get(sessionId);
        session?.MissingInfoWaiter?.TrySetResult(false);
        session?.ApprovalWaiter?.TrySetResult(false);
        return Task.CompletedTask;
    }

    private (AgentSession Session, CancellationToken Token) PrepareRun(Guid sessionId, string jiraTicketOrUrl, string? workspacePath, CancellationToken cancellationToken)
    {
        var session = RequireSession(sessionId);
        var path = string.IsNullOrWhiteSpace(workspacePath) ? _workspaceDefaults.DefaultWorkspacePath : workspacePath;
        session.Context.WorkspacePath = string.IsNullOrWhiteSpace(path) ? session.Context.WorkspacePath : path;
        session.Context.JiraTicketUrl = jiraTicketOrUrl.Contains("://", StringComparison.Ordinal) ? jiraTicketOrUrl : null;
        session.Context.JiraTicketKey = JiraUrlNormalizer.ExtractIssueKey(jiraTicketOrUrl);
        // Hard reset per Analyze — never inherit prior ticket investigation state.
        session.Context.ReproductionSteps.Clear();
        session.Context.ReproductionOutcome = ReproductionOutcome.NotStarted;
        session.Context.ReproductionNotes = null;
        session.Context.Evidence.Clear();
        session.Context.RootCause = null;
        session.Context.ProposedFixSummary = null;
        session.Context.ProposedDiff = null;
        session.Context.ProposedFiles.Clear();
        session.Context.FinalStatusMessage = null;
        session.Context.Investigation.Clear();
        session.Context.CodeCorrelation.Clear();
        session.Context.CustomFields.Remove("stepsInferredFromTicket");
        session.Context.CustomFields.Remove("inferredForTicket");
        session.Context.CustomFields.Remove("stepsFromJira");
        session.Context.CustomFields.Remove("stepsFromHistoricalValidated");
        session.Context.CustomFields.Remove("historicalStepsSource");
        session.Context.CustomFields.Remove("distinctScreenshotCount");
        session.Context.CustomFields.Remove("failedNavSteps");
        session.Context.CustomFields.Remove("investigationBlockedNoLlm");
        session.Context.UserConfirmedReadyToReproduce = false;
        ResetEnvironmentAndAuthentication(session.Context);
        _sessionCredentials.Clear(session.Id);
        session.Context.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Starting fresh investigation for {session.Context.JiraTicketKey}.");
        _redactor.ClearSessionSecrets(session.Id);

        // One cancellation source PER investigation: starting or cancelling one never affects another user's run.
        CancellationTokenSource runCts;
        lock (_runGate)
        {
            if (_runs.Remove(sessionId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }
            runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runs[sessionId] = runCts;
        }

        return (session, runCts.Token);
    }

    private async Task ExecuteRunAsync(AgentSession session, CancellationToken token)
    {
        var sessionId = session.Id;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await RunPipelineAsync(scope.ServiceProvider, session, token);
        }
        catch (OperationCanceledException)
        {
            session.Context.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Investigation run was cancelled.");
            session.NotifyChanged();
        }
        finally
        {
            await _browserSessions.CloseAsync(sessionId);
            _sessionCredentials.Clear(sessionId);
            _redactor.ClearSessionSecrets(sessionId);
            lock (_runGate)
            {
                if (_runs.Remove(sessionId, out var cts))
                    cts.Dispose();
            }
        }
    }

    public async Task SubmitMissingInfoAsync(
        Guid sessionId,
        IDictionary<string, string> values,
        CredentialSecret? newCredentials,
        bool saveCredentialProfile,
        CancellationToken cancellationToken = default)
    {
        var session = RequireSession(sessionId);
        var ctx = session.Context;
        var hadTicketPaste = values.Keys.Any(k => k.Equals("ticketText", StringComparison.OrdinalIgnoreCase));
        ApplyUserValues(ctx, values);

        using var scope = _scopeFactory.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();

        if (hadTicketPaste)
        {
            session.SetState(AgentState.ExtractingContext, "Re-analyzing pasted Jira ticket content.");
            await ExecuteSkillAsync(scope.ServiceProvider, SkillIds.EnvironmentDiscovery, session, cancellationToken);
        }

        // ── Manual authentication step (MFA etc.) ──────────────────────────
        var authAction = Value(values, "authAction");
        if (string.Equals(authAction, "recheck", StringComparison.OrdinalIgnoreCase)
            && ctx.AuthenticationStatus is AuthenticationStatus.MfaRequired or AuthenticationStatus.ManualInterventionRequired)
        {
            // Optional one-time verification code: typed into the open MFA page by the browser skill, never logged.
            var mfaCode = Value(values, "mfaCode");
            if (mfaCode is not null && ctx.SelectedEnvironmentId is { } mfaEnv)
            {
                _redactor.RegisterSecret(mfaCode, session.Id);
                _sessionCredentials.Set(session.Id, MfaCredentialKey(mfaEnv), new CredentialSecret { Username = "", Password = mfaCode });
            }

            ctx.AuthenticationRecheckRequested = true;
            await _missingInfoResolver.ResolveAsync(ctx, vault, cancellationToken);
            ctx.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} User reports the additional sign-in step is complete — re-checking.");
            session.NotifyChanged();
            session.MissingInfoWaiter?.TrySetResult(true);
            return;
        }

        if (string.Equals(authAction, "restart", StringComparison.OrdinalIgnoreCase))
        {
            await _browserSessions.CloseAsync(session.Id);
            ctx.AuthenticationStatus = AuthenticationStatus.NotStarted;
            ctx.AuthenticationMessage = null;
            ctx.AuthenticationRecheckRequested = false;
            ctx.EnvironmentSelectionConfirmed = false;
            ctx.UserConfirmedReadyToReproduce = false;
            await _missingInfoResolver.ResolveAsync(ctx, vault, cancellationToken);
            session.SetState(AgentState.AwaitingMissingInfo, "Choose the environment and enter its credentials again.");
            return;
        }

        // ── Environment selection ───────────────────────────────────────────
        var environmentId = Value(values, "environmentId");
        if (environmentId is not null)
        {
            var previousEnv = ctx.SelectedEnvironmentId;
            var selection = _environments.Select(ctx, environmentId, Value(values, "environmentUrl"));
            if (!selection.IsValid)
            {
                ctx.UserConfirmedReadyToReproduce = false;
                await _missingInfoResolver.ResolveAsync(ctx, vault, cancellationToken);
                session.SetState(AgentState.AwaitingMissingInfo, selection.Error);
                return;
            }

            if (!string.Equals(previousEnv, ctx.SelectedEnvironmentId, StringComparison.OrdinalIgnoreCase))
            {
                // Different environment → never reuse the previous browser session or its credentials.
                await _browserSessions.CloseAsync(session.Id);
                _sessionCredentials.Clear(session.Id);
            }

            ctx.StatusLog.Add(
                $"{DateTimeOffset.UtcNow:HH:mm:ss} Environment selected: {selection.Environment!.DisplayName} ({selection.Environment.Type}) — {selection.Environment.SafeBaseUrl}" +
                (selection.Environment.IsProduction ? " — PRODUCTION: read-only investigation." : "."));
        }

        if (ctx.IsReadOnlyEnvironment)
            ctx.StateChangingActionsConfirmed = string.Equals(Value(values, "allowStateChangingActions"), "true", StringComparison.OrdinalIgnoreCase);

        var environment = _environments.ResolveSelected(ctx);

        // ── Credentials (environment-specific) ─────────────────────────────
        if (newCredentials is not null)
        {
            if (environment is null)
            {
                ctx.UserConfirmedReadyToReproduce = false;
                ctx.EnvironmentSelectionError ??= "Select a valid environment before entering credentials.";
                await _missingInfoResolver.ResolveAsync(ctx, vault, cancellationToken);
                session.SetState(AgentState.AwaitingMissingInfo, ctx.EnvironmentSelectionError);
                return;
            }

            if (string.IsNullOrWhiteSpace(newCredentials.Username) ||
                (string.IsNullOrEmpty(newCredentials.Password) && string.IsNullOrEmpty(newCredentials.Token)))
            {
                ctx.UserConfirmedReadyToReproduce = false;
                await _missingInfoResolver.ResolveAsync(ctx, vault, cancellationToken);
                session.SetState(AgentState.AwaitingMissingInfo, $"Enter both the username and password for {environment.DisplayName}.");
                return;
            }

            _redactor.RegisterSecret(newCredentials.Password, session.Id);
            _redactor.RegisterSecret(newCredentials.Token, session.Id);
            _sessionCredentials.Set(session.Id, environment.Id, newCredentials);
            ctx.CredentialProfileId = null;
            ctx.Username = newCredentials.Username;
            ctx.AuthenticationStatus = AuthenticationStatus.NotStarted;
            ctx.AuthenticationMessage = null;

            // Persist ONLY on explicit opt-in. Default: credentials live in memory for this investigation only.
            if (saveCredentialProfile)
            {
                var existing = await vault.FindForEnvironmentAsync(environment.Id, newCredentials.Username, cancellationToken);
                var saved = await vault.SaveProfileAsync(new CredentialProfile
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    Label = $"{environment.DisplayName} + {newCredentials.Username}",
                    SystemName = environment.ApplicationId ?? ctx.SystemName,
                    EnvironmentName = environment.DisplayName,
                    EnvironmentId = environment.Id,
                    Username = newCredentials.Username,
                    Role = ctx.Role,
                    AuthType = string.IsNullOrWhiteSpace(newCredentials.Token) ? "password" : "token"
                }, newCredentials, cancellationToken);
                ctx.CredentialProfileId = saved.Id;
                ctx.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Credentials for {environment.DisplayName} saved encrypted (user opted in).");
            }
        }
        else if (ctx.CredentialProfileId is Guid chosenProfile)
        {
            // Saved profile chosen in the UI: it must belong to the current user AND the selected environment.
            var profile = await vault.GetProfileAsync(chosenProfile, cancellationToken);
            if (profile is null || profile.IsLegacyUnscoped || environment is null ||
                !string.Equals(profile.EnvironmentId, environment.Id, StringComparison.OrdinalIgnoreCase))
            {
                ctx.CredentialProfileId = null;
                ctx.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Ignored a saved credential profile that does not belong to the selected environment.");
            }
            else
            {
                ctx.Username = profile.Username;
                ctx.AuthenticationStatus = AuthenticationStatus.NotStarted;
                ctx.AuthenticationMessage = null;
            }
        }

        // User explicitly clicked Continue — allow reproduce once the resolver is satisfied.
        ctx.UserConfirmedReadyToReproduce = true;
        if (ctx.ReproductionOutcome is ReproductionOutcome.Error
            or ReproductionOutcome.Blocked
            or ReproductionOutcome.EnvironmentUnavailable)
        {
            ctx.ReproductionOutcome = ReproductionOutcome.NotStarted;
            ctx.ReproductionNotes = null;
        }

        await _missingInfoResolver.ResolveAsync(ctx, vault, cancellationToken);

        // Optional steps: blank textarea should not block continue.
        ctx.MissingFields = ctx.MissingFields
            .Where(f => f.Key != "reproductionSteps")
            .ToList();
        if (ctx.MissingFields.Count == 0)
            ctx.MissingInfoReason = null;

        session.NotifyChanged();

        if (!ctx.JiraDetailsLoaded)
        {
            session.SetState(AgentState.Failed,
                "Cannot continue: Jira ticket was not loaded. Fix API credentials and Analyze again.");
            return;
        }

        if (ctx.MissingFields.Count == 0)
        {
            session.MissingInfoWaiter?.TrySetResult(true);
            // Pipeline is already awaiting MissingInfoWaiter; do not start a second continue here
            // unless we are recovering from Blocked without an active waiter.
            if (session.State is AgentState.Blocked)
            {
                session.MissingInfoWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.MissingInfoWaiter.TrySetResult(true);
                _ = Task.Run(async () =>
                {
                    using var continueScope = _scopeFactory.CreateScope();
                    await ContinueAfterContextReadyAsync(continueScope.ServiceProvider, session, CancellationToken.None);
                }, CancellationToken.None);
            }
        }
        else
        {
            ctx.UserConfirmedReadyToReproduce = false;
            session.SetState(AgentState.AwaitingMissingInfo, ctx.MissingInfoReason ?? "Still missing required fields.");
        }
    }

    public Task ApproveFixAsync(Guid sessionId, bool approve, CancellationToken cancellationToken = default)
    {
        var session = RequireSession(sessionId);
        session.Context.FixApproved = approve;
        session.ApprovalWaiter?.TrySetResult(approve);
        session.NotifyChanged();
        return Task.CompletedTask;
    }

    public async Task SaveLessonAsync(
        Guid sessionId,
        string lesson,
        MemoryScope scope,
        IEnumerable<string>? tags,
        string? validatedBy,
        CancellationToken cancellationToken = default)
    {
        if (!_permissions.IsAllowed(AgentPermission.ManageMemory))
            throw new InvalidOperationException("ManageMemory permission is disabled.");

        var session = RequireSession(sessionId);
        using var diScope = _scopeFactory.CreateScope();
        var memoryStore = diScope.ServiceProvider.GetRequiredService<IEngineeringMemoryStore>();
        var sanitized = _redactor.Redact(lesson);
        var entity = new EngineeringLesson
        {
            Scope = scope,
            ScopeKey = session.Context.WorkspacePath,
            Lesson = sanitized,
            Tags = tags?.ToList() ?? new List<string>(),
            ValidatedBy = validatedBy ?? "user"
        };
        var saved = await memoryStore.SaveValidatedLessonAsync(entity, cancellationToken);
        session.Context.AppliedMemoryLessonIds.Add(saved.Id.ToString());
        session.Context.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Saved engineering lesson {saved.Id}");
        session.NotifyChanged();
    }

    private async Task RunPipelineAsync(IServiceProvider sp, AgentSession session, CancellationToken cancellationToken)
    {
        try
        {
            var vault = sp.GetRequiredService<ICredentialVault>();

            session.SetState(AgentState.ReadingJira, "Fetching ticket details from Jira.");
            try
            {
                await ExecuteSkillAsync(sp, SkillIds.Jira, session, cancellationToken);
            }
            catch (Exception jiraEx) when (jiraEx.Message.Contains("Jira", StringComparison.OrdinalIgnoreCase))
            {
                session.LastError = _redactor.Redact(jiraEx.Message);
                session.Context.JiraDetailsLoaded = false;
                await _missingInfoResolver.ResolveAsync(session.Context, vault, cancellationToken);
                session.SetState(AgentState.Failed,
                    "Stopped: Jira ticket was not loaded. Fix Settings (email + API token), use Test connection, then click Analyze ticket again.");
                return;
            }

            session.SetState(AgentState.UnderstandingTicket, "LLM structuring investigation intent from the ticket.");
            await ExecuteSkillAsync(sp, SkillIds.TicketUnderstanding, session, cancellationToken);

            session.SetState(AgentState.SearchingHistory, "Searching Jira for similar historical bugs.");
            await ExecuteSkillAsync(sp, SkillIds.HistoricalJiraSearch, session, cancellationToken);
            await ExecuteSkillAsync(sp, SkillIds.HistoricalReproductionReuse, session, cancellationToken);

            session.SetState(AgentState.RetrievingDocs, "Retrieving relevant Visium KMS documentation.");
            await ExecuteSkillAsync(sp, SkillIds.KmsDocumentation, session, cancellationToken);

            session.SetState(AgentState.ExtractingContext, "Inferring environment, user/role, and URLs from the ticket.");
            await ExecuteSkillAsync(sp, SkillIds.EnvironmentDiscovery, session, cancellationToken);

            // Hosted mode: check out the mapped repository (read-only) so code investigation needs no local workspace.
            if (IsRegistered(sp, SkillIds.SourceRepository))
            {
                session.SetState(AgentState.ExtractingContext, "Locating the source repository for this ticket.");
                await ExecuteSkillAsync(sp, SkillIds.SourceRepository, session, cancellationToken);
            }
            await _missingInfoResolver.ResolveAsync(session.Context, vault, cancellationToken);
            session.Context.StatusLog.Add(
                $"{DateTimeOffset.UtcNow:HH:mm:ss} Missing info: {session.Context.MissingFields.Count} field(s) — {session.Context.MissingInfoReason ?? "ready"}.");
            session.NotifyChanged();

            if (session.Context.MissingFields.Count > 0)
            {
                session.MissingInfoWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.SetState(AgentState.AwaitingMissingInfo, session.Context.MissingInfoReason ?? "Waiting for missing information.");
                var filled = await session.MissingInfoWaiter.Task.ConfigureAwait(false);
                if (!filled)
                {
                    session.SetState(AgentState.Blocked, "Missing information was not provided.");
                    return;
                }
            }

            await ContinueAfterContextReadyAsync(sp, session, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            session.MissingInfoWaiter?.TrySetResult(false);
            session.Context.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Run canceled (a newer ticket Analyze was started).");
            session.NotifyChanged();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Pipeline failed for session {SessionId}: {Message}", session.Id, _redactor.Redact(ex.Message));
            session.LastError = _redactor.Redact(ex.Message);
            session.SetState(AgentState.Failed, session.LastError);
        }
    }

    private async Task ContinueAfterContextReadyAsync(IServiceProvider sp, AgentSession session, CancellationToken cancellationToken)
    {
        try
        {
            var vault = sp.GetRequiredService<ICredentialVault>();
            var memoryStore = sp.GetRequiredService<IEngineeringMemoryStore>();

            session.SetState(AgentState.ReadyToReproduce, "Context resolved. Signing in to the selected environment before reproduction.");

            // Hard gate: reproduction never starts before a verified, successful sign-in.
            if (!await AuthenticateAsync(sp, session, vault, cancellationToken))
                return;

            session.SetState(AgentState.Reproducing, "Following Jira reproduction steps (or steps inferred from the ticket summary).");
            await ExecuteSkillAsync(sp, SkillIds.Reproduction, session, cancellationToken);

            switch (session.Context.ReproductionOutcome)
            {
                case ReproductionOutcome.Blocked:
                case ReproductionOutcome.EnvironmentUnavailable:
                case ReproductionOutcome.Error:
                    await _browserSessions.CloseAsync(session.Id);
                    _sessionCredentials.Clear(session.Id);
                    session.Context.UserConfirmedReadyToReproduce = false;
                    // Browser closed → the next attempt must sign in again.
                    session.Context.AuthenticationStatus = AuthenticationStatus.NotStarted;
                    session.Context.AuthenticationRecheckRequested = false;
                    session.Context.ReproductionNotes = TruncateNotes(session.Context.ReproductionNotes);
                    await _missingInfoResolver.ResolveAsync(session.Context, vault, cancellationToken);
                    if (session.Context.MissingFields.Count == 0)
                    {
                        session.Context.MissingFields =
                        [
                            new MissingField
                            {
                                Key = MissingInfoResolver.EnvironmentFieldKey,
                                Label = "Environment to investigate",
                                Reason = "Confirm the environment (or choose another), then re-enter the login.",
                                InputType = "environment"
                            },
                            new MissingField
                            {
                                Key = MissingInfoResolver.CredentialsFieldKey,
                                Label = "Credentials for the selected environment",
                                Reason = session.Context.ReproductionNotes
                                         ?? "Previous login/reproduce attempt failed. Re-enter credentials and click Continue.",
                                IsSecret = true,
                                InputType = "credentials"
                            }
                        ];
                        session.Context.MissingInfoReason = session.Context.ReproductionNotes
                            ?? "Previous attempt failed — confirm the environment, re-enter the login and Continue.";
                    }

                    session.MissingInfoWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    session.SetState(
                        AgentState.AwaitingMissingInfo,
                        session.Context.MissingInfoReason
                        ?? "Reproduction blocked — update environment login and click Continue to retry.");
                    var retry = await session.MissingInfoWaiter.Task.ConfigureAwait(false);
                    if (!retry)
                    {
                        session.SetState(AgentState.Blocked, "Stopped after blocked reproduction.");
                        return;
                    }

                    session.Context.ReproductionOutcome = ReproductionOutcome.NotStarted;
                    session.Context.ReproductionNotes = null;
                    await ContinueAfterContextReadyAsync(sp, session, CancellationToken.None);
                    return;
                case ReproductionOutcome.NotReproduced:
                case ReproductionOutcome.Ambiguous:
                    await ExecuteSkillAsync(sp, SkillIds.EvidenceReporting, session, cancellationToken);
                    session.Context.FinalStatusMessage = session.Context.ReproductionOutcome == ReproductionOutcome.NotReproduced
                        ? $"Could not reproduce {session.Context.JiraTicketKey} on {session.Context.SelectedEnvironmentDisplayName ?? "the selected environment"} — the reported issue was not found. Review Evidence."
                        : $"Could not confirm reproduction for {session.Context.JiraTicketKey} on {session.Context.SelectedEnvironmentDisplayName ?? "the selected environment"}. Review Evidence for that ticket’s feature path, add clearer Jira steps if needed, then Analyze again.";
                    session.SetState(AgentState.Completed, session.Context.FinalStatusMessage);
                    return;
                case ReproductionOutcome.Reproduced:
                    break;
                default:
                    session.SetState(AgentState.Failed, "Unexpected reproduction outcome.");
                    return;
            }

            if (!MissingInfoResolver.IsLocalWorkspace(session.Context.WorkspacePath))
            {
                await ExecuteSkillAsync(sp, SkillIds.EvidenceReporting, session, cancellationToken);
                session.Context.FinalStatusMessage =
                    $"Reproduced {session.Context.JiraTicketKey}, but no source repository is mapped/available, so no code investigation was done.";
                session.SetState(AgentState.Completed, session.Context.FinalStatusMessage);
                return;
            }

            session.SetState(AgentState.Investigating, "Locating root cause in the workspace.");
            var tags = new List<string>();
            if (!string.IsNullOrWhiteSpace(session.Context.SystemName))
                tags.Add(session.Context.SystemName!);
            var lessons = await memoryStore.QueryRelevantAsync(session.Context.WorkspacePath, tags, cancellationToken);
            foreach (var lesson in lessons.Take(10))
                session.Context.AppliedMemoryLessonIds.Add(lesson.Id.ToString());

            await ExecuteSkillAsync(sp, SkillIds.CodeInvestigation, session, cancellationToken);

            if (session.Context.CustomFields.ContainsKey("investigationBlockedNoLlm"))
            {
                await ExecuteSkillAsync(sp, SkillIds.EvidenceReporting, session, cancellationToken);
                session.Context.FinalStatusMessage =
                    "Reproduction finished, but no code fix was proposed because no LLM API key is configured. " +
                    "Add OpenAI/Anthropic in Sign in / Account, then Analyze again. Review Evidence screenshots meanwhile.";
                session.SetState(AgentState.Completed, session.Context.FinalStatusMessage);
                return;
            }

            if (!_permissions.IsAllowed(AgentPermission.ModifyCode))
            {
                // Read-only rollout: report the proposal for human review; never wait for an approval that cannot apply code.
                await ExecuteSkillAsync(sp, SkillIds.EvidenceReporting, session, cancellationToken);
                session.Context.FinalStatusMessage =
                    $"Investigation of {session.Context.JiraTicketKey} complete (read-only): fix proposal included for human review; no code was modified.";
                session.SetState(AgentState.Completed, session.Context.FinalStatusMessage);
                return;
            }

            session.SetState(AgentState.FixProposed, "Fix proposal ready.");
            session.SetState(AgentState.AwaitingApproval, "Waiting for human approval before modifying code.");
            session.ApprovalWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var approved = await session.ApprovalWaiter.Task.WaitAsync(cancellationToken);
            if (!approved)
            {
                await ExecuteSkillAsync(sp, SkillIds.EvidenceReporting, session, cancellationToken);
                session.Context.FinalStatusMessage = "Fix rejected by user.";
                session.SetState(AgentState.Completed, session.Context.FinalStatusMessage);
                return;
            }

            session.SetState(AgentState.ApplyingFix, "Applying approved minimal code changes.");
            await ExecuteSkillAsync(sp, SkillIds.CodeModification, session, new Dictionary<string, string> { ["apply"] = "true" }, cancellationToken);

            session.SetState(AgentState.Validating, "Running allowlisted validation commands.");
            await ExecuteSkillAsync(sp, SkillIds.Validation, session, cancellationToken);

            session.SetState(AgentState.Retesting, "Re-testing the original Jira scenario.");
            await ExecuteSkillAsync(sp, SkillIds.Reproduction, session, new Dictionary<string, string> { ["mode"] = "retest" }, cancellationToken);
            session.Context.RetestPassed = session.Context.ReproductionOutcome is ReproductionOutcome.NotReproduced;

            await ExecuteSkillAsync(sp, SkillIds.EvidenceReporting, session, cancellationToken);

            session.Context.FinalStatusMessage = session.Context.RetestPassed
                ? "LOCAL CHANGES VALIDATED → READY FOR HUMAN REVIEW"
                : "Local changes applied but re-test did not clearly confirm the fix.";
            session.SetState(AgentState.Completed, session.Context.FinalStatusMessage);
        }
        catch (OperationCanceledException)
        {
            session.MissingInfoWaiter?.TrySetResult(false);
            session.ApprovalWaiter?.TrySetResult(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Continuation failed for session {SessionId}: {Message}", session.Id, _redactor.Redact(ex.Message));
            session.LastError = _redactor.Redact(ex.Message);
            session.SetState(AgentState.Failed, session.LastError);
        }
        finally
        {
            await _browserSessions.CloseAsync(session.Id);
            _sessionCredentials.Clear(session.Id);
        }
    }

    /// <summary>
    /// Signs in to the selected environment through the browser skill and loops until the sign-in is verified,
    /// the user fixes credentials / picks another environment, or completes a manual (MFA) step.
    /// Returns false when the run stopped without a successful sign-in.
    /// </summary>
    private async Task<bool> AuthenticateAsync(IServiceProvider sp, AgentSession session, ICredentialVault vault, CancellationToken cancellationToken)
    {
        var ctx = session.Context;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var environment = _environments.ResolveSelected(ctx);
            var recheck = ctx.AuthenticationRecheckRequested
                          && ctx.AuthenticationStatus is AuthenticationStatus.MfaRequired or AuthenticationStatus.ManualInterventionRequired;
            ctx.AuthenticationRecheckRequested = false;

            session.SetState(
                AgentState.Authenticating,
                recheck
                    ? $"Re-checking sign-in to {environment?.DisplayName ?? "the environment"} after the manual step."
                    : $"Opening {environment?.DisplayName ?? "the environment"} ({environment?.SafeBaseUrl ?? "no URL"}) in an isolated browser and signing in.");

            await ExecuteSkillAsync(
                sp,
                SkillIds.BrowserAutomation,
                session,
                new Dictionary<string, string> { ["mode"] = recheck ? "recheck" : "login" },
                cancellationToken);

            if (ctx.AuthenticationStatus == AuthenticationStatus.Succeeded)
            {
                ctx.ReproductionOutcome = ReproductionOutcome.NotStarted;
                ctx.ReproductionNotes = null;
                ctx.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Signed in to {environment?.DisplayName}. {ctx.AuthenticationMessage}");
                session.SetState(AgentState.ConnectingEnvironment, "Sign-in verified. Continuing with reproduction.");
                return true;
            }

            var manualStep = ctx.AuthenticationStatus is AuthenticationStatus.MfaRequired or AuthenticationStatus.ManualInterventionRequired;
            if (!manualStep)
            {
                // Failed attempt: drop the browser session and any remaining in-memory secret.
                await _browserSessions.CloseAsync(session.Id);
                _sessionCredentials.Clear(session.Id);
                if (ctx.AuthenticationStatus is AuthenticationStatus.InvalidCredentials or AuthenticationStatus.SessionExpired)
                    ctx.CredentialProfileId = null; // saved credentials failed — ask instead of retrying them silently
            }

            ctx.UserConfirmedReadyToReproduce = false;
            await _missingInfoResolver.ResolveAsync(ctx, vault, cancellationToken);
            if (ctx.MissingFields.Count == 0)
            {
                // Defensive: never loop silently without asking the user.
                ctx.EnvironmentSelectionConfirmed = false;
                await _missingInfoResolver.ResolveAsync(ctx, vault, cancellationToken);
            }

            session.MissingInfoWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.SetState(
                AgentState.AwaitingMissingInfo,
                ctx.AuthenticationMessage ?? ctx.ReproductionNotes ?? "Sign-in did not succeed. Update the environment login and Continue.");

            var proceed = await session.MissingInfoWaiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!proceed)
            {
                session.SetState(AgentState.Blocked, "Stopped: sign-in to the environment did not complete.");
                return false;
            }
        }
    }

    private static bool IsRegistered(IServiceProvider sp, string skillId) =>
        sp.GetRequiredService<IEnumerable<ISkill>>().Any(s => string.Equals(s.SkillId, skillId, StringComparison.OrdinalIgnoreCase));

    private async Task ExecuteSkillAsync(IServiceProvider sp, string skillId, AgentSession session, CancellationToken cancellationToken) =>
        await ExecuteSkillAsync(sp, skillId, session, null, cancellationToken);

    private async Task ExecuteSkillAsync(
        IServiceProvider sp,
        string skillId,
        AgentSession session,
        IReadOnlyDictionary<string, string>? inputs,
        CancellationToken cancellationToken)
    {
        var skills = sp.GetRequiredService<IEnumerable<ISkill>>();
        var skill = skills.FirstOrDefault(s => string.Equals(s.SkillId, skillId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Skill '{skillId}' is not registered.");

        if (!skill.CanExecute(session.Context, _permissions))
            throw new InvalidOperationException($"Skill '{skillId}' cannot execute under current permissions/context.");

        var progress = new Progress<string>(msg =>
        {
            session.Context.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {skill.DisplayName}: {_redactor.Redact(msg)}");
            session.NotifyChanged();
        });

        var result = await skill.ExecuteAsync(new SkillExecutionRequest
        {
            Context = session.Context,
            Permissions = _permissions,
            Inputs = inputs,
            Progress = progress
        }, cancellationToken);

        if (!result.Succeeded)
            throw new InvalidOperationException(result.Message ?? $"Skill '{skillId}' failed.");

        session.NotifyChanged();
    }

    private static void ApplyUserValues(RuntimeContext context, IDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            switch (key.ToLowerInvariant())
            {
                case "workspacepath":
                    context.WorkspacePath = value.Trim();
                    break;
                // environmentId / environmentUrl / allowStateChangingActions / authAction are handled by
                // IEnvironmentSelectionService in SubmitMissingInfoAsync (validated selection, never raw assignment).
                case "username":
                    context.Username = value.Trim();
                    break;
                case "role":
                    context.Role = value.Trim();
                    break;
                case "systemname":
                    context.SystemName = value.Trim();
                    break;
                case "environmentname":
                    context.EnvironmentName = value.Trim();
                    break;
                case "tickettext":
                    context.Description = value;
                    if (string.IsNullOrWhiteSpace(context.Summary) ||
                        string.Equals(context.Summary, context.JiraTicketKey, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Summary = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .FirstOrDefault() ?? context.Summary;
                    }
                    context.JiraDetailsLoaded = true;
                    context.CustomFields["pastedTicket"] = "true";
                    break;
                case "credentialprofileid" when Guid.TryParse(value, out var id):
                    context.CredentialProfileId = id;
                    break;
                case "reproductionsteps":
                    context.ReproductionSteps = value
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    break;
            }
        }
    }

    /// <summary>Session-credential key under which a one-time MFA code is handed to the browser skill.</summary>
    public static string MfaCredentialKey(string environmentId) => "MFA:" + environmentId;

    private static string? Value(IDictionary<string, string> values, string key)
    {
        foreach (var (k, v) in values)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        return null;
    }

    private static void ResetEnvironmentAndAuthentication(RuntimeContext context)
    {
        context.SuggestedEnvironmentId = null;
        context.SuggestedEnvironmentReason = null;
        context.SuggestedAdHocUrl = null;
        context.SelectedEnvironmentId = null;
        context.SelectedEnvironmentDisplayName = null;
        context.SelectedEnvironmentType = null;
        context.EnvironmentSelectionConfirmed = false;
        context.EnvironmentSelectionError = null;
        context.IsReadOnlyEnvironment = false;
        context.StateChangingActionsConfirmed = false;
        context.SkippedStateChangingSteps.Clear();
        context.CredentialProfileId = null;
        context.AuthenticationStatus = AuthenticationStatus.NotStarted;
        context.AuthenticationMessage = null;
        context.AuthenticationRecheckRequested = false;
    }

    private static string? TruncateNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return notes;
        if (notes.Contains("not visible", StringComparison.OrdinalIgnoreCase))
            return "Login failed: filled a hidden Email field (forgot-password dialog). Retry Continue — login now uses the visible UserName/Password fields.";
        var first = notes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? notes;
        return first.Length <= 280 ? first : first[..280] + "…";
    }

    private AgentSession RequireSession(Guid sessionId) =>
        _sessions.Get(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} not found.");
}

public interface IAgentRuntimeResolver
{
    Abstractions.IAgentRuntime GetCurrent();
    IReadOnlyList<string> AvailableProviderIds { get; }
}

public sealed class AgentRuntimeResolver : IAgentRuntimeResolver
{
    private readonly IEnumerable<Abstractions.IAgentRuntime> _runtimes;
    private readonly AgentOptions _options;

    public AgentRuntimeResolver(IEnumerable<Abstractions.IAgentRuntime> runtimes, IOptions<AgentOptions> options)
    {
        _runtimes = runtimes;
        _options = options.Value;
    }

    public IReadOnlyList<string> AvailableProviderIds => _runtimes.Select(r => r.ProviderId).ToList();

    public Abstractions.IAgentRuntime GetCurrent()
    {
        var runtime = _runtimes.FirstOrDefault(r =>
            string.Equals(r.ProviderId, _options.ActiveProviderId, StringComparison.OrdinalIgnoreCase));
        return runtime ?? _runtimes.FirstOrDefault()
            ?? throw new InvalidOperationException("No AI runtime providers are registered.");
    }
}
