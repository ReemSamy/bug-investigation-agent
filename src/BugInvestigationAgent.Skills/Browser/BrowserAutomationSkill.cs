using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Skills.Browser.Authentication;
using BugInvestigationAgent.Skills.Evidence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Browser;

/// <summary>
/// Opens the selected environment in an isolated browser session and signs in with the environment-specific
/// credentials. Sets <see cref="RuntimeContext.AuthenticationStatus"/>; the orchestrator only starts reproduction
/// when it is <see cref="AuthenticationStatus.Succeeded"/>.
/// Inputs: mode=login (default) | recheck (re-evaluate the open page after a manual/MFA step).
/// </summary>
public sealed class BrowserAutomationSkill : ISkill
{
    private readonly ICredentialVault _vault;
    private readonly ISessionCredentialStore _sessionCredentials;
    private readonly IEnvironmentSelectionService _environments;
    private readonly ISecretRedactor _redactor;
    private readonly AgentOptions _options;
    private readonly IPlaywrightSessionHub _sessions;
    private readonly LoginFlow _loginFlow;
    private readonly ILogger<BrowserAutomationSkill> _logger;

    public BrowserAutomationSkill(
        ICredentialVault vault,
        ISessionCredentialStore sessionCredentials,
        IEnvironmentSelectionService environments,
        ISecretRedactor redactor,
        IOptions<AgentOptions> options,
        IPlaywrightSessionHub sessions,
        LoginFlow loginFlow,
        ILogger<BrowserAutomationSkill> logger)
    {
        _vault = vault;
        _sessionCredentials = sessionCredentials;
        _environments = environments;
        _redactor = redactor;
        _options = options.Value;
        _sessions = sessions;
        _loginFlow = loginFlow;
        _logger = logger;
    }

    public string SkillId => SkillIds.BrowserAutomation;
    public string DisplayName => "Browser Automation";
    public AgentPermission RequiredPermissions => AgentPermission.UseBrowser;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        var recheck = request.Inputs is not null
                      && request.Inputs.TryGetValue("mode", out var mode)
                      && string.Equals(mode, "recheck", StringComparison.OrdinalIgnoreCase);

        // 1) Environment must be selected, enabled and have a URL — otherwise never open a browser.
        var validation = string.IsNullOrWhiteSpace(ctx.SelectedEnvironmentId)
            ? EnvironmentValidationResult.Fail("No environment selected. Select the environment to investigate.")
            : ctx.SelectedEnvironmentId!.StartsWith(AppEnvironment.AdHocPrefix, StringComparison.OrdinalIgnoreCase)
                ? _environments.Validate(EnvironmentSelectionService.AdHocOptionId, ctx.EnvironmentUrl)
                : _environments.Validate(ctx.SelectedEnvironmentId);
        if (!validation.IsValid || validation.Environment is null)
        {
            return Stop(ctx, ReproductionOutcome.Blocked, AuthenticationStatus.NotStarted,
                validation.Error ?? "The selected environment is not available.");
        }

        var env = validation.Environment;
        ctx.EnvironmentUrl = env.BaseUrl;

        if (recheck && _sessions.TryGetPage(ctx.InvestigationId, out var openPage))
            return await RecheckAsync(ctx, env, openPage, request.Progress, cancellationToken);

        // 2) Environment-specific credentials: typed for this investigation (default), or an opted-in saved profile.
        CredentialSecret? secret = null;
        if (env.Login.RequiresLogin)
        {
            secret = _sessionCredentials.Take(ctx.InvestigationId, env.Id);
            if (secret is null && ctx.CredentialProfileId is Guid profileId)
            {
                var profile = await _vault.GetProfileAsync(profileId, cancellationToken);
                if (profile is not null && !profile.IsLegacyUnscoped &&
                    string.Equals(profile.EnvironmentId, env.Id, StringComparison.OrdinalIgnoreCase))
                    secret = await _vault.ResolveSecretAsync(profileId, cancellationToken);
                if (secret is null)
                    ctx.CredentialProfileId = null;
            }

            if (secret is null)
            {
                return Stop(ctx, ReproductionOutcome.Blocked, AuthenticationStatus.CredentialsRequired,
                    $"Credentials for {env.DisplayName} are required before the agent can sign in.");
            }

            _redactor.RegisterSecret(secret.Password, ctx.InvestigationId);
            _redactor.RegisterSecret(secret.Token, ctx.InvestigationId);
        }

        try
        {
            ctx.AuthenticationStatus = AuthenticationStatus.InProgress;
            ctx.AuthenticationMessage = $"Signing in to {env.DisplayName}…";

            // 3) Fresh, isolated browser context for this investigation + environment.
            var page = await _sessions.StartAsync(ctx.InvestigationId, cancellationToken);
            // Network-level read-only enforcement (before the first navigation).
            await ReadOnlyBrowserGuard.InstallAsync(page, ctx, env, request.Progress);
            page.SetDefaultTimeout(_options.Browser.ActionTimeoutMs);
            page.SetDefaultNavigationTimeout(_options.Browser.NavigationTimeoutMs);

            var target = BuildStartUrl(env);
            request.Progress?.Report($"Opening {env.DisplayName} ({env.Type}) at {env.SafeBaseUrl} in an isolated browser session.");
            var response = await page.GotoAsync(target, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.Browser.NavigationTimeoutMs
            });
            if (response is null || (int)response.Status >= 500)
            {
                await _sessions.CloseAsync(ctx.InvestigationId);
                return Stop(ctx, ReproductionOutcome.EnvironmentUnavailable, AuthenticationStatus.Failed,
                    $"{env.DisplayName} returned HTTP {(response?.Status)?.ToString() ?? "no response"} — the environment appears to be unavailable.");
            }

            await BrowserWaits.WaitForPageReadyAsync(page, _options.Browser);

            // Evidence 1: the selected environment before anything is typed (no credentials on screen).
            await EvidenceScreenshots.CaptureAsync(page, _options, ctx, "01-environment",
                $"Selected environment: {env.DisplayName} ({env.Type}) — {env.SafeBaseUrl}" +
                (env.IsProduction ? " — PRODUCTION (read-only investigation)" : ""),
                EvidenceCategories.Environment, env.Login);

            var loginPage = new PlaywrightLoginPage(page, _options.Browser);
            var result = secret is null
                ? await _loginFlow.LoginAsync(loginPage, env.Login, new CredentialSecret { Username = "" }, request.Progress, cancellationToken)
                : await _loginFlow.LoginAsync(loginPage, env.Login, secret, request.Progress, cancellationToken);
            secret = null; // drop the reference as soon as the form was submitted

            return await ApplyResultAsync(ctx, env, page, result, request.Progress);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            await _sessions.CloseAsync(ctx.InvestigationId);
            _logger.LogWarning("Playwright browser executable is missing.");
            return Stop(ctx, ReproductionOutcome.Error, AuthenticationStatus.Failed,
                "Playwright browsers not installed. Run: pwsh bin/Debug/net8.0/playwright.ps1 install chromium");
        }
        catch (OperationCanceledException)
        {
            await _sessions.CloseAsync(ctx.InvestigationId);
            throw;
        }
        catch (Exception ex)
        {
            await _sessions.CloseAsync(ctx.InvestigationId);
            var safe = SummarizeBrowserError(_redactor.Redact(ex.Message));
            // Never log the raw exception: Playwright call logs can echo typed values.
            _logger.LogWarning("Browser sign-in failed for environment {EnvironmentId}: {Message}", env.Id, safe);
            return Stop(ctx, ReproductionOutcome.Error, AuthenticationStatus.Failed, $"Sign-in to {env.DisplayName} failed: {safe}");
        }
        finally
        {
            secret = null;
        }
    }

    private async Task<SkillResult> RecheckAsync(RuntimeContext ctx, AppEnvironment env, IPage page, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var loginPage = new PlaywrightLoginPage(page, _options.Browser);
            var mfa = _sessionCredentials.Take(ctx.InvestigationId, BugInvestigationAgent.Core.Orchestration.AgentOrchestrator.MfaCredentialKey(env.Id));
            AuthenticationResult result;
            if (!string.IsNullOrEmpty(mfa?.Password))
            {
                progress?.Report($"Submitting the verification code on {env.DisplayName}.");
                result = await _loginFlow.SubmitMfaCodeAsync(loginPage, env.Login, mfa.Password!, cancellationToken);
            }
            else
            {
                progress?.Report($"Re-checking sign-in state on {env.DisplayName}.");
                result = await _loginFlow.RecheckAsync(loginPage, env.Login, cancellationToken);
            }
            return await ApplyResultAsync(ctx, env, page, result, progress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _sessions.CloseAsync(ctx.InvestigationId);
            var safe = SummarizeBrowserError(_redactor.Redact(ex.Message));
            _logger.LogWarning("Sign-in re-check failed for environment {EnvironmentId}: {Message}", env.Id, safe);
            return Stop(ctx, ReproductionOutcome.Error, AuthenticationStatus.Failed, $"Re-checking sign-in failed: {safe}");
        }
    }

    private async Task<SkillResult> ApplyResultAsync(RuntimeContext ctx, AppEnvironment env, IPage page, AuthenticationResult result, IProgress<string>? progress)
    {
        ctx.AuthenticationStatus = result.Status;
        ctx.AuthenticationMessage = _redactor.Redact(result.Message);
        progress?.Report($"Sign-in result for {env.DisplayName}: {AuthenticationStatusDisplay.Label(result.Status)}. {ctx.AuthenticationMessage}");

        switch (result.Status)
        {
            case AuthenticationStatus.Succeeded:
                await BrowserWaits.WaitForPageReadyAsync(page, _options.Browser);
                progress?.Report("Waiting for home content to finish loading…");
                await BrowserWaits.WaitForSpinnersAsync(page, _options.Browser);
                // Evidence 2: application after a verified, successful sign-in.
                await EvidenceScreenshots.CaptureAsync(page, _options, ctx, "02-after-login",
                    $"Signed in to {env.DisplayName} — application home before navigating into the ticket feature",
                    EvidenceCategories.PostLogin, env.Login);
                (ctx.DeployedVersion, ctx.DeployedVersionSource) =
                    await DeployedVersionDetector.DetectAsync(page, env, Math.Min(_options.Browser.ActionTimeoutMs, 20_000));
                progress?.Report(ctx.DeployedVersion is null
                    ? $"KMS version on {env.DisplayName}: unknown — {ctx.DeployedVersionSource}"
                    : $"KMS version on {env.DisplayName}: {ctx.DeployedVersion} (from {ctx.DeployedVersionSource}).");
                ctx.ReproductionOutcome = ReproductionOutcome.NotStarted;
                ctx.ReproductionNotes = null;
                progress?.Report("Browser session ready (kept open for reproduction).");
                return SkillResult.Ok($"Signed in to {env.DisplayName}.");

            case AuthenticationStatus.MfaRequired:
            case AuthenticationStatus.ManualInterventionRequired:
                // Keep the browser open so the user can finish the step (headed mode) and the agent can re-check.
                // No screenshot: verification pages often show personal details (phone, e-mail).
                ctx.ReproductionOutcome = ReproductionOutcome.Blocked;
                ctx.ReproductionNotes = ctx.AuthenticationMessage;
                if (_options.Browser.Headless)
                    ctx.AuthenticationMessage += " The browser is currently headless, so this step cannot be completed interactively.";
                return SkillResult.Ok("Sign-in paused — user action required.");

            case AuthenticationStatus.InvalidCredentials:
                // Evidence: the error message shown by the login page (password field masked).
                try
                {
                    await EvidenceScreenshots.CaptureAsync(page, _options, ctx, "02-login-rejected",
                        $"Sign-in to {env.DisplayName} was rejected — login error message (password field masked)",
                        EvidenceCategories.Authentication, env.Login);
                }
                catch (PlaywrightException) { /* evidence is best-effort */ }
                await _sessions.CloseAsync(ctx.InvestigationId);
                ctx.ReproductionOutcome = ReproductionOutcome.Blocked;
                ctx.ReproductionNotes = ctx.AuthenticationMessage;
                return SkillResult.Ok("Sign-in rejected.");

            default:
                await _sessions.CloseAsync(ctx.InvestigationId);
                ctx.ReproductionOutcome = ReproductionOutcome.Blocked;
                ctx.ReproductionNotes = ctx.AuthenticationMessage;
                return SkillResult.Ok("Sign-in did not succeed.");
        }
    }

    private static SkillResult Stop(RuntimeContext ctx, ReproductionOutcome outcome, AuthenticationStatus status, string message)
    {
        ctx.ReproductionOutcome = outcome;
        ctx.ReproductionNotes = message;
        ctx.AuthenticationStatus = status;
        ctx.AuthenticationMessage = message;
        return SkillResult.Ok(message);
    }

    public static string BuildStartUrl(AppEnvironment env)
    {
        var baseUrl = env.BaseUrl!;
        if (string.IsNullOrWhiteSpace(env.Login.LoginPath))
            return baseUrl;
        return baseUrl.TrimEnd('/') + "/" + env.Login.LoginPath!.TrimStart('/');
    }

    internal static string SummarizeBrowserError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Browser automation failed.";

        var first = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith('-'))
            ?? message.Split('\n')[0].Trim();

        if (message.Contains("not visible", StringComparison.OrdinalIgnoreCase))
            return "Login failed: targeted a hidden form field. Check the environment's login selectors.";

        return first.Length > 240 ? first[..240] + "…" : first;
    }
}
