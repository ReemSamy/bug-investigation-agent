using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Environments;

namespace BugInvestigationAgent.Core.Tests;

internal static class TestEnvironments
{
    /// <summary>Registry with fake, non-routable hosts (.test TLD) — never real environments.</summary>
    public static EnvironmentRegistryOptions Options(bool allowAdHoc = true) => new()
    {
        AllowAdHocUrl = allowAdHoc,
        Environments = new Dictionary<string, EnvironmentDefinitionOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["KMSRC"] = new() { DisplayName = "KMSRC", ApplicationId = "KMS", BaseUrl = "https://kmsrc.example.test", Type = EnvironmentType.ReleaseCandidate, Aliases = { "KMS RC" } },
            ["KMSQA"] = new() { DisplayName = "KMSQA", ApplicationId = "KMS", BaseUrl = "https://kmsqa.example.test", Type = EnvironmentType.QA, Aliases = { "KMS QA" } },
            ["KMSDEV"] = new() { DisplayName = "KMSDEV", ApplicationId = "KMS", BaseUrl = "", Type = EnvironmentType.Development },
            ["KMSOLD"] = new() { DisplayName = "KMSOLD", ApplicationId = "KMS", BaseUrl = "https://kmsold.example.test", Type = EnvironmentType.QA, Enabled = false },
            ["KMSPROD"] = new() { DisplayName = "KMS Production", ApplicationId = "KMS", BaseUrl = "https://kms.example.test", Type = EnvironmentType.Production },
            ["HRQA"] = new() { DisplayName = "HR QA", ApplicationId = "HR", BaseUrl = "https://hrqa.example.test", Type = EnvironmentType.QA }
        }
    };

    public static EnvironmentSelectionService Selection(bool allowAdHoc = true) =>
        new(new ConfigurationEnvironmentRegistry(Options(allowAdHoc)));
}

internal sealed class FixedWorkspace : IWorkspaceDefaults
{
    public string? DefaultWorkspacePath { get; } = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
}

internal sealed class SignedInJira : IJiraConnectionSettings
{
    public bool IsConfigured => true;
    public bool IsSignedIn => true;
    public string? SignedInDisplayName => "Tester";
    public string? SignedInEmail => "tester@example.test";
    public JiraOptions GetEffective() => new() { BaseUrl = "https://jira.example.test", Email = "tester@example.test", ApiToken = "x" };
}
