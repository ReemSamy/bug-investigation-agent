using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Permissions;

namespace BugInvestigationAgent.Core.Skills;

public interface ISkill
{
    string SkillId { get; }
    string DisplayName { get; }
    AgentPermission RequiredPermissions { get; }
    bool CanExecute(RuntimeContext context, IPermissionGate permissions);
    Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default);
}

public sealed class SkillExecutionRequest
{
    public required RuntimeContext Context { get; init; }
    public required IPermissionGate Permissions { get; init; }
    public IReadOnlyDictionary<string, string>? Inputs { get; init; }
    public IProgress<string>? Progress { get; init; }
}

public sealed class SkillResult
{
    public bool Succeeded { get; init; }
    public string? Message { get; init; }
    public IReadOnlyDictionary<string, string>? Outputs { get; init; }

    public static SkillResult Ok(string? message = null, IReadOnlyDictionary<string, string>? outputs = null) =>
        new() { Succeeded = true, Message = message, Outputs = outputs };

    public static SkillResult Fail(string message) =>
        new() { Succeeded = false, Message = message };
}

public static class SkillIds
{
    public const string Jira = "jira";
    public const string EnvironmentDiscovery = "environment-discovery";
    public const string Credential = "credential";
    public const string BrowserAutomation = "browser-automation";
    public const string Reproduction = "reproduction";
    public const string CodeInvestigation = "code-investigation";
    public const string CodeModification = "code-modification";
    public const string Validation = "validation";
    public const string EvidenceReporting = "evidence-reporting";
    public const string Git = "git";
    public const string GitHub = "github";
    public const string PullRequest = "pull-request";
    public const string CiCd = "ci-cd";
    public const string Deployment = "deployment";
}
