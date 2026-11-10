namespace BugInvestigationAgent.Core.Permissions;

/// <summary>
/// Initial rollout: the agent investigates and reports only. Everything that could change source code, run build/test
/// commands in the target repository, touch Git, or deploy is removed at runtime in every host (local app, API,
/// worker), whatever the configuration or UI says. Lifting this needs a code change and review.
/// </summary>
public static class ReadOnlyRollout
{
    public const AgentPermission Forbidden =
        AgentPermission.ModifyCode | AgentPermission.RunValidation | AgentPermission.GitCommit | AgentPermission.GitPush |
        AgentPermission.CreatePullRequest | AgentPermission.TriggerCi | AgentPermission.Deploy;

    public static IPermissionGate Mask(IPermissionGate configured) => new PermissionGate(configured.Allowed & ~Forbidden);
}
