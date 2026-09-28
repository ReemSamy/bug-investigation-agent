using BugInvestigationAgent.Core.Browser;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
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
    Task StartAsync(Guid sessionId, string jiraTicketOrUrl, string? workspacePath, CancellationToken cancellationToken = default);
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
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly object _runGate = new();
    private CancellationTokenSource? _activeRunCts;
    private Guid? _activeRunSessionId;

    public AgentOrchestrator(
        IServiceScopeFactory scopeFactory,
        IAgentSessionStore sessions,
        ISecretRedactor redactor,
        IMissingInfoResolver missingInfoResolver,
        IPermissionGate permissions,
        IWorkspaceDefaults workspaceDefaults,
        IBrowserSessionCloser browserSessions,
        ILogger<AgentOrchestrator> logger)
    {
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
        session.Context.CustomFields.Remove("stepsInferredFromTicket");
        session.Context.CustomFields.Remove("inferredForTicket");
        session.Context.CustomFields.Remove("stepsFromJira");
        session.Context.CustomFields.Remove("stepsFromHistoricalValidated");
        session.Context.CustomFields.Remove("historicalStepsSource");
        session.Context.CustomFields.Remove("distinctScreenshotCount");
        session.Context.CustomFields.Remove("failedNavSteps");
        session.Context.CustomFields.Remove("investigationBlockedNoLlm");
        session.Context.UserConfirmedReadyToReproduce = false;
        session.Context.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Starting fresh investigation for {session.Context.JiraTicketKey}.");
        _redactor.ClearSessionSecrets();

        CancellationTokenSource runCts;
        lock (_runGate)
        {
            _activeRunCts?.Cancel();
            _activeRunCts?.Dispose();
            _activeRunCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runCts = _activeRunCts;
            _activeRunSessionId = sessionId;
        }

        var token = runCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await RunPipelineAsync(scope.ServiceProvider, session, token);
            }
            catch (OperationCanceledException)
            {
                session.Context.StatusLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} Previous/canceled run stopped so a new ticket can be investigated.");
                session.NotifyChanged();
            }
            finally
            {
                await _browserSessions.CloseAsync(sessionId);
                lock (_runGate)
                {
                    if (_activeRunSessionId == sessionId)
                    {
                        _activeRunCts = null;
                        _activeRunSessionId = null;
                    }
                }
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task SubmitMissingInfoAsync(
        Guid sessionId,
        IDictionary<string, string> values,
        CredentialSecret? newCredentials,
        bool saveCredentialProfile,
        CancellationToken cancellationToken = default)
    {
        var session = RequireSession(sessionId);
        var hadTicketPaste = values.Keys.Any(k => k.Equals("ticketText", StringComparison.OrdinalIgnoreCase));
        ApplyUserValues(session.Context, values);

        using var scope = _scopeFactory.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();

        if (hadTicketPaste)
        {
            session.SetState(AgentState.ExtractingContext, "Re-analyzing pasted Jira ticket content.");
            await ExecuteSkillAsync(scope.ServiceProvider, SkillIds.EnvironmentDiscovery, session, cancellationToken);
        }

        if (newCredentials is not null)
        {
            _redactor.RegisterSecret(newCredentials.Password);
            _redactor.RegisterSecret(newCredentials.Token);

            if (saveCredentialProfile || session.Context.CredentialProfileId is null)
            {
                var labelParts = new[]
                {
                    session.Context.SystemName,
                    session.Context.EnvironmentName,
                    session.Context.Username ?? session.Context.Role
                }.Where(s => !string.IsNullOrWhiteSpace(s));

                var profile = new CredentialProfile
                {
                    Label = string.Join(" + ", labelParts),
                    SystemName = session.Context.SystemName,
                    EnvironmentName = session.Context.EnvironmentName,
                    Username = newCredentials.Username,
                    Role = session.Context.Role,
                    AuthType = string.IsNullOrWhiteSpace(newCredentials.Token) ? "password" : "token"
                };

                if (string.IsNullOrWhiteSpace(session.Context.Username))
                    session.Context.Username = newCredentials.Username;

                var saved = await vault.SaveProfileAsync(profile, newCredentials, cancellationToken);
                session.Context.CredentialProfileId = saved.Id;
            }
        }

        // User explicitly clicked Continue — allow reproduce even when only confirming saved credentials.
        session.Context.UserConfirmedReadyToReproduce = true;
        if (session.Context.ReproductionOutcome is ReproductionOutcome.Error
            or ReproductionOutcome.Blocked
            or ReproductionOutcome.EnvironmentUnavailable)
        {
            session.Context.ReproductionOutcome = ReproductionOutcome.NotStarted;
            session.Context.ReproductionNotes = null;
        }

        await _missingInfoResolver.ResolveAsync(session.Context, vault, cancellationToken);

        // Optional steps: blank textarea should not block continue.
        session.Context.MissingFields = session.Context.MissingFields
            .Where(f => f.Key != "reproductionSteps")
            .ToList();
        if (session.Context.MissingFields.Count == 0)
            session.Context.MissingInfoReason = null;

        session.NotifyChanged();

        if (!session.Context.JiraDetailsLoaded)
        {
            session.SetState(AgentState.Failed,
                "Cannot continue: Jira ticket was not loaded. Fix API credentials and Analyze again.");
            return;
        }

        if (session.Context.CredentialProfileId is null && newCredentials is null
            && !values.ContainsKey("credentialProfileId"))
        {
            session.Context.UserConfirmedReadyToReproduce = false;
            await _missingInfoResolver.ResolveAsync(session.Context, vault, cancellationToken);
            session.SetState(AgentState.AwaitingMissingInfo, "Environment password is still required before reproduce.");
            return;
        }

        if (session.Context.MissingFields.Count == 0)
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
            session.Context.UserConfirmedReadyToReproduce = false;
            session.SetState(AgentState.AwaitingMissingInfo, session.Context.MissingInfoReason ?? "Still missing required fields.");
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
            _logger.LogError(ex, "Pipeline failed for session {SessionId}", session.Id);
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

            session.SetState(AgentState.ReadyToReproduce, "Context resolved. Starting reproduction.");
            session.SetState(AgentState.ConnectingEnvironment, "Opening environment with the ticket's user/role.");
            await ExecuteSkillAsync(sp, SkillIds.BrowserAutomation, session, cancellationToken);

            session.SetState(AgentState.Reproducing, "Following Jira reproduction steps (or steps inferred from the ticket summary).");
            await ExecuteSkillAsync(sp, SkillIds.Reproduction, session, cancellationToken);

            switch (session.Context.ReproductionOutcome)
            {
                case ReproductionOutcome.Blocked:
                case ReproductionOutcome.EnvironmentUnavailable:
                case ReproductionOutcome.Error:
                    await _browserSessions.CloseAsync(session.Id);
                    session.Context.UserConfirmedReadyToReproduce = false;
                    session.Context.ReproductionNotes = TruncateNotes(session.Context.ReproductionNotes);
                    await _missingInfoResolver.ResolveAsync(session.Context, vault, cancellationToken);
                    if (session.Context.MissingFields.Count == 0)
                    {
                        session.Context.MissingFields =
                        [
                            new MissingField
                            {
                                Key = "environmentUrl",
                                Label = "Environment URL (portal to test)",
                                Reason = "Confirm or enter the portal URL (not Azure DevOps / Jira / PR). Then re-enter login.",
                                InputType = "url"
                            },
                            new MissingField
                            {
                                Key = "credentials",
                                Label = "Password for environment login",
                                Reason = session.Context.ReproductionNotes
                                         ?? "Previous login/reproduce attempt failed. Re-enter credentials and click Continue.",
                                IsSecret = true,
                                InputType = "credentials"
                            }
                        ];
                        session.Context.MissingInfoReason = session.Context.ReproductionNotes
                            ?? "Previous attempt failed — update environment URL + login and Continue.";
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
                        ? $"Could not reproduce {session.Context.JiraTicketKey} — the reported issue was not found. Review Evidence."
                        : $"Could not confirm reproduction for {session.Context.JiraTicketKey}. Review Evidence for that ticket’s feature path, add clearer Jira steps if needed, then Analyze again.";
                    session.SetState(AgentState.Completed, session.Context.FinalStatusMessage);
                    return;
                case ReproductionOutcome.Reproduced:
                    break;
                default:
                    session.SetState(AgentState.Failed, "Unexpected reproduction outcome.");
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
                session.Context.FinalStatusMessage =
                    "Reproduction finished, but no code fix was proposed because no LLM API key is configured. " +
                    "Add OpenAI/Anthropic in Sign in / Account, then Analyze again. Review Evidence screenshots meanwhile.";
                session.SetState(AgentState.Completed, session.Context.FinalStatusMessage);
                return;
            }

            session.SetState(AgentState.FixProposed, "Fix proposal ready.");
            session.SetState(AgentState.AwaitingApproval, "Waiting for human approval before modifying code.");
            session.ApprovalWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var approved = await session.ApprovalWaiter.Task.WaitAsync(cancellationToken);
            if (!approved)
            {
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
            _logger.LogError(ex, "Continuation failed for session {SessionId}", session.Id);
            session.LastError = _redactor.Redact(ex.Message);
            session.SetState(AgentState.Failed, session.LastError);
        }
        finally
        {
            await _browserSessions.CloseAsync(session.Id);
        }
    }

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
                case "environmenturl":
                    context.EnvironmentUrl = EnvironmentUrlParser.NormalizeUrl(value);
                    if (EnvironmentUrlParser.IsLikelyPortalEnvironmentUrl(context.EnvironmentUrl) &&
                        Uri.TryCreate(context.EnvironmentUrl, UriKind.Absolute, out var envUri))
                    {
                        context.EnvironmentName = envUri.Host;
                    }
                    break;
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
