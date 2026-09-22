namespace BugInvestigationAgent.Core.Configuration;

/// <summary>
/// Effective connection settings for Jira (appsettings + local saved settings).
/// </summary>
public interface IJiraConnectionSettings
{
    bool IsConfigured { get; }
    /// <summary>True after a successful /myself login test for the saved account.</summary>
    bool IsSignedIn { get; }
    string? SignedInDisplayName { get; }
    string? SignedInEmail { get; }
    JiraOptions GetEffective();
}

public interface IWorkspaceDefaults
{
    string? DefaultWorkspacePath { get; }
}
