using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Skills;

namespace BugInvestigationAgent.Skills.Future;

internal abstract class DisabledSkillBase : ISkill
{
    public abstract string SkillId { get; }
    public abstract string DisplayName { get; }
    public abstract AgentPermission RequiredPermissions { get; }

    public bool CanExecute(Core.Context.RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions);

    public Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(SkillResult.Fail($"{DisplayName} is disabled in V1. Enable the corresponding permission to use a future implementation."));
}

internal sealed class GitSkill : DisabledSkillBase
{
    public override string SkillId => SkillIds.Git;
    public override string DisplayName => "Git";
    public override AgentPermission RequiredPermissions => AgentPermission.GitCommit;
}

internal sealed class GitHubSkill : DisabledSkillBase
{
    public override string SkillId => SkillIds.GitHub;
    public override string DisplayName => "GitHub";
    public override AgentPermission RequiredPermissions => AgentPermission.CreatePullRequest;
}

internal sealed class PullRequestSkill : DisabledSkillBase
{
    public override string SkillId => SkillIds.PullRequest;
    public override string DisplayName => "Pull Request";
    public override AgentPermission RequiredPermissions => AgentPermission.CreatePullRequest;
}

internal sealed class CiCdSkill : DisabledSkillBase
{
    public override string SkillId => SkillIds.CiCd;
    public override string DisplayName => "CI/CD";
    public override AgentPermission RequiredPermissions => AgentPermission.TriggerCi;
}

internal sealed class DeploymentSkill : DisabledSkillBase
{
    public override string SkillId => SkillIds.Deployment;
    public override string DisplayName => "Deployment";
    public override AgentPermission RequiredPermissions => AgentPermission.Deploy;
}
