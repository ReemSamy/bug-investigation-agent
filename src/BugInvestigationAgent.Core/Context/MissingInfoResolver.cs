using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Workflow;

namespace BugInvestigationAgent.Core.Context;

public interface IMissingInfoResolver
{
    Task ResolveAsync(RuntimeContext context, ICredentialVault vault, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides what the user must provide before reproduction: workspace, the environment to investigate,
/// environment-specific credentials, manual authentication steps (MFA) and optional reproduction steps.
/// </summary>
public sealed class MissingInfoResolver : IMissingInfoResolver
{
    public const string EnvironmentFieldKey = "environment";
    public const string CredentialsFieldKey = "credentials";
    public const string AuthenticationActionFieldKey = "authenticationAction";

    private readonly IWorkspaceDefaults _workspaceDefaults;
    private readonly IJiraConnectionSettings _jira;
    private readonly IEnvironmentSelectionService _environments;
    private readonly ISessionCredentialStore _sessionCredentials;

    public MissingInfoResolver(
        IWorkspaceDefaults workspaceDefaults,
        IJiraConnectionSettings jira,
        IEnvironmentSelectionService environments,
        ISessionCredentialStore sessionCredentials)
    {
        _workspaceDefaults = workspaceDefaults;
        _jira = jira;
        _environments = environments;
        _sessionCredentials = sessionCredentials;
    }

    /// <summary>Test/default constructor: no registered environments, manual URLs allowed.</summary>
    public MissingInfoResolver()
        : this(new EmptyWorkspaceDefaults(), new EmptyJiraSettings(),
            new EnvironmentSelectionService(new ConfigurationEnvironmentRegistry(new EnvironmentRegistryOptions())),
            new InMemorySessionCredentialStore())
    {
    }

    public async Task ResolveAsync(RuntimeContext context, ICredentialVault vault, CancellationToken cancellationToken = default)
    {
        var missing = new List<MissingField>();

        if (!_jira.IsConfigured)
        {
            missing.Add(SettingsField(
                "Jira is not connected yet. Open Sign in / Account, save Base URL + email + API token, then click Analyze ticket again."));
            context.MissingFields = missing;
            context.MissingInfoReason = "Jira is not connected.";
            return;
        }

        // Configured but fetch failed / never loaded — do NOT pretend the ticket was read.
        if (!context.JiraDetailsLoaded)
        {
            missing.Add(SettingsField(
                "Jira API did not return this issue (often 404). That usually means the API token/email is wrong or belongs to a different Atlassian account. " +
                "Recreate the token at id.atlassian.com for the exact same email, Sign in again, then Analyze again."));
            context.MissingFields = missing;
            context.MissingInfoReason =
                "Ticket was NOT loaded from Jira. Fix API credentials, then click Analyze ticket again — do not skip ahead.";
            return;
        }

        if (!IsLocalWorkspace(context.WorkspacePath) && IsLocalWorkspace(_workspaceDefaults.DefaultWorkspacePath))
            context.WorkspacePath = _workspaceDefaults.DefaultWorkspacePath;

        var repositoryUnavailable = context.CustomFields.ContainsKey("repositoryMappingMissing");
        if (!IsLocalWorkspace(context.WorkspacePath) && !repositoryUnavailable)
        {
            missing.Add(new MissingField
            {
                Key = "workspacePath",
                Label = "Local workspace path (once)",
                Reason = "Path to the code on this machine. Save a default in Sign in / Account to avoid being asked again.",
                InputType = "path"
            });
        }

        var selected = _environments.ResolveSelected(context);
        if (selected is not null)
        {
            // Registry (or validated ad-hoc) URL is authoritative for the selected environment.
            context.EnvironmentUrl = selected.BaseUrl;
        }
        else
        {
            // ADO/PR/Jira links are ticket metadata — never treat them as the portal to open.
            context.EnvironmentUrl = EnvironmentUrlParser.NormalizeUrl(context.EnvironmentUrl);
            if (!EnvironmentUrlParser.IsLikelyPortalEnvironmentUrl(context.EnvironmentUrl))
            {
                context.EnvironmentUrl = null;
                if (string.Equals(context.EnvironmentName, "dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(context.EnvironmentName, "visualstudio.com", StringComparison.OrdinalIgnoreCase))
                    context.EnvironmentName = null;
            }
        }
        var manualAuthPending = context.AuthenticationStatus is AuthenticationStatus.MfaRequired
            or AuthenticationStatus.ManualInterventionRequired;

        if (manualAuthPending && selected is not null)
        {
            // Browser is still open on the MFA / manual step — only ask the user to complete it (or start over).
            if (!context.AuthenticationRecheckRequested)
            {
                missing.Add(new MissingField
                {
                    Key = AuthenticationActionFieldKey,
                    Label = $"Complete sign-in to {selected.DisplayName}",
                    Reason = context.AuthenticationMessage
                             ?? "The environment needs an additional sign-in step. Complete it, then re-check.",
                    InputType = "auth-manual"
                });
            }

            Finish(context, missing);
            return;
        }

        // ── Environment ─────────────────────────────────────────────────────
        var needsEnvironment = selected is null
                               || !context.EnvironmentSelectionConfirmed
                               || !context.UserConfirmedReadyToReproduce;
        if (needsEnvironment)
        {
            missing.Add(new MissingField
            {
                Key = EnvironmentFieldKey,
                Label = "Environment to investigate",
                Reason = BuildEnvironmentReason(context, selected),
                InputType = "environment"
            });
        }

        // ── Credentials (environment-specific) ─────────────────────────────
        var credentialsReady = false;
        if (selected is not null)
        {
            credentialsReady = !selected.Login.RequiresLogin
                               || _sessionCredentials.Has(context.InvestigationId, selected.Id);

            if (!credentialsReady && context.CredentialProfileId is Guid profileId)
            {
                var profile = await vault.GetProfileAsync(profileId, cancellationToken);
                if (profile is not null && !profile.IsLegacyUnscoped
                    && string.Equals(profile.EnvironmentId, selected.Id, StringComparison.OrdinalIgnoreCase))
                    credentialsReady = true;
                else
                    context.CredentialProfileId = null; // never use credentials saved for another environment
            }
        }

        var needsCredentials = selected is null
            || (selected.Login.RequiresLogin && (!credentialsReady || !context.UserConfirmedReadyToReproduce));
        if (needsCredentials)
        {
            CredentialProfile? saved = null;
            if (selected is not null)
                saved = await vault.FindForEnvironmentAsync(selected.Id, null, cancellationToken);

            missing.Add(new MissingField
            {
                Key = CredentialsFieldKey,
                Label = selected is null
                    ? "Environment username & password"
                    : $"Credentials for {selected.DisplayName}",
                Reason = BuildCredentialReason(context, selected, saved),
                IsSecret = true,
                InputType = "credentials"
            });
        }

        if (context.ReproductionSteps.Count == 0 && context.ReproductionOutcome == ReproductionOutcome.NotStarted)
        {
            missing.Add(new MissingField
            {
                Key = "reproductionSteps",
                Label = "Reproduction steps (optional but recommended)",
                Reason = "Jira did not include steps. Paste short steps so the agent can follow them in the browser.",
                InputType = "textarea"
            });
        }

        Finish(context, missing);
    }

    private static void Finish(RuntimeContext context, List<MissingField> missing)
    {
        context.MissingFields = missing;
        if (missing.Count == 0)
        {
            context.MissingInfoReason = null;
            return;
        }

        context.MissingInfoReason =
            !string.IsNullOrWhiteSpace(context.EnvironmentSelectionError) ? context.EnvironmentSelectionError
            : !string.IsNullOrWhiteSpace(context.AuthenticationMessage)
              && context.AuthenticationStatus is not AuthenticationStatus.NotStarted and not AuthenticationStatus.Succeeded
                ? context.AuthenticationMessage
            : !string.IsNullOrWhiteSpace(context.ReproductionNotes) ? context.ReproductionNotes
            : "Ticket loaded from Jira. Select the environment to investigate and enter its login, then click Continue.";
    }

    private string BuildEnvironmentReason(RuntimeContext context, AppEnvironment? selected)
    {
        if (!string.IsNullOrWhiteSpace(context.EnvironmentSelectionError))
            return context.EnvironmentSelectionError!;

        if (selected is not null)
            return $"Confirm {selected.DisplayName} ({selected.SafeBaseUrl}) or choose another environment.";

        var reason = context.SuggestedEnvironmentReason
                     ?? "Select the environment on which the bug should be investigated.";
        if (!string.IsNullOrWhiteSpace(context.FixPullRequestUrl))
            reason += $" Note: the ticket links a fix PR ({context.FixPullRequestUrl}) — that is not an environment; pick where the fix may be deployed.";
        return reason;
    }

    private static string BuildCredentialReason(RuntimeContext context, AppEnvironment? selected, CredentialProfile? saved)
    {
        var envName = selected?.DisplayName ?? "the selected environment";

        if ((context.AuthenticationStatus is AuthenticationStatus.InvalidCredentials
                or AuthenticationStatus.SessionExpired
                or AuthenticationStatus.Failed
                or AuthenticationStatus.CredentialsRequired)
            && !string.IsNullOrWhiteSpace(context.AuthenticationMessage))
        {
            return $"{context.AuthenticationMessage} Re-enter the {envName} credentials (or choose another environment) and click Continue.";
        }

        if (!string.IsNullOrWhiteSpace(context.ReproductionNotes) &&
            context.ReproductionOutcome is ReproductionOutcome.Error
                or ReproductionOutcome.Blocked
                or ReproductionOutcome.EnvironmentUnavailable)
        {
            return $"{context.ReproductionNotes} Enter the {envName} login again and click Continue to retry.";
        }

        if (saved is not null)
            return $"Saved credentials found for '{saved.Username}' on {envName}. Use them, or enter different credentials for this investigation.";

        if (selected is null)
            return "Select an environment first. Credentials are requested per environment and are never reused for another environment.";

        return string.IsNullOrWhiteSpace(context.Username)
            ? $"Enter the username and password used to sign in to {envName}. They are used for this investigation only unless you choose to remember them."
            : $"Enter the password for '{context.Username}' on {envName}. Used for this investigation only unless you choose to remember them.";
    }

    private static MissingField SettingsField(string reason) => new()
    {
        Key = "jiraSettings",
        Label = "Fix Jira API access (Sign in / Account)",
        Reason = reason,
        InputType = "settings-link"
    };

    public static bool IsLocalWorkspace(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var p = path.Trim();
        if (p.Contains("://", StringComparison.Ordinal) ||
            p.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("github.com", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("azurewebsites.net", StringComparison.OrdinalIgnoreCase))
            return false;
        return Directory.Exists(p);
    }

    private sealed class EmptyWorkspaceDefaults : IWorkspaceDefaults
    {
        public string? DefaultWorkspacePath => null;
    }

    private sealed class EmptyJiraSettings : IJiraConnectionSettings
    {
        public bool IsConfigured => true;
        public bool IsSignedIn => true;
        public string? SignedInDisplayName => "Test";
        public string? SignedInEmail => "test@example.com";
        public JiraOptions GetEffective() => new();
    }
}
