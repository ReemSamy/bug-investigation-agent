using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Workflow;

namespace BugInvestigationAgent.Core.Context;

public interface IMissingInfoResolver
{
    Task ResolveAsync(RuntimeContext context, ICredentialVault vault, CancellationToken cancellationToken = default);
}

public sealed class MissingInfoResolver : IMissingInfoResolver
{
    private readonly IWorkspaceDefaults _workspaceDefaults;
    private readonly IJiraConnectionSettings _jira;

    public MissingInfoResolver(IWorkspaceDefaults workspaceDefaults, IJiraConnectionSettings jira)
    {
        _workspaceDefaults = workspaceDefaults;
        _jira = jira;
    }

    public MissingInfoResolver()
        : this(new EmptyWorkspaceDefaults(), new EmptyJiraSettings())
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

        if (!IsLocalWorkspace(context.WorkspacePath))
        {
            missing.Add(new MissingField
            {
                Key = "workspacePath",
                Label = "Local workspace path (once)",
                Reason = "Path to the code on this machine. Save a default in Sign in / Account to avoid being asked again.",
                InputType = "path"
            });
        }

        context.EnvironmentUrl = EnvironmentUrlParser.NormalizeUrl(context.EnvironmentUrl);
        if (!EnvironmentUrlParser.IsLikelyPortalEnvironmentUrl(context.EnvironmentUrl))
        {
            // ADO/PR/Jira links are ticket metadata — never treat them as the portal to open.
            if (!string.IsNullOrWhiteSpace(context.EnvironmentUrl) ||
                string.Equals(context.EnvironmentName, "dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.EnvironmentName, "visualstudio.com", StringComparison.OrdinalIgnoreCase))
            {
                context.EnvironmentUrl = null;
                if (string.Equals(context.EnvironmentName, "dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(context.EnvironmentName, "visualstudio.com", StringComparison.OrdinalIgnoreCase))
                    context.EnvironmentName = null;
            }
        }

        var needsUserIdentity = string.IsNullOrWhiteSpace(context.Username) && string.IsNullOrWhiteSpace(context.Role);
        var match = await vault.FindMatchAsync(
            context.SystemName,
            context.EnvironmentName,
            context.Username,
            context.Role,
            cancellationToken);

        if (match is not null && context.CredentialProfileId is null)
        {
            context.CredentialProfileId = match.Id;
            if (string.IsNullOrWhiteSpace(context.Username))
                context.Username = match.Username;
        }

        var needsCredentialsGate = !context.UserConfirmedReadyToReproduce
            || context.CredentialProfileId is null;
        var needsEnvironmentUrl = !EnvironmentUrlParser.IsLikelyPortalEnvironmentUrl(context.EnvironmentUrl);

        // Ask for portal URL when missing/invalid, and also while confirming credentials so the user can override a wrong auto-detect.
        if (needsEnvironmentUrl || needsCredentialsGate)
        {
            missing.Add(new MissingField
            {
                Key = "environmentUrl",
                Label = "Environment URL (portal to test)",
                Reason = needsEnvironmentUrl
                    ? "Provide the KMS/portal environment URL to open in the browser (for example https://….azurewebsites.net). " +
                      "Do not use Azure DevOps, Jira, or pull-request links — those are not the application under test."
                    : "Confirm this is the application environment the agent should open (not Azure DevOps / Jira / PR links).",
                InputType = "url"
            });
        }

        if (needsCredentialsGate)
        {
            missing.Add(new MissingField
            {
                Key = "credentials",
                Label = needsUserIdentity ? "Environment username & password" : "Password for environment login",
                Reason = BuildCredentialReason(context, match),
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

        context.MissingFields = missing;
        context.MissingInfoReason = missing.Count == 0
            ? null
            : string.IsNullOrWhiteSpace(context.ReproductionNotes)
                ? "Ticket loaded from Jira. Enter the environment login below, then click Continue."
                : context.ReproductionNotes;
    }

    private static string BuildCredentialReason(RuntimeContext context, CredentialProfile? match)
    {
        if (!string.IsNullOrWhiteSpace(context.ReproductionNotes) &&
            context.ReproductionOutcome is ReproductionOutcome.Error
                or ReproductionOutcome.Blocked
                or ReproductionOutcome.EnvironmentUnavailable)
        {
            return $"{context.ReproductionNotes} Enter/update the portal password for '{context.Username ?? context.Role ?? "user"}' and click Continue to retry.";
        }

        if (match is not null || context.CredentialProfileId is not null)
        {
            return $"Saved credentials found for '{context.Username ?? match?.Username ?? "user"}'. " +
                   "Confirm by clicking Continue (or enter a new password to replace them).";
        }

        return string.IsNullOrWhiteSpace(context.Username) && string.IsNullOrWhiteSpace(context.Role)
            ? "Enter the username/password used to log into the environment URL."
            : $"Enter the password for user '{context.Username ?? context.Role}' to log into the environment.";
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
