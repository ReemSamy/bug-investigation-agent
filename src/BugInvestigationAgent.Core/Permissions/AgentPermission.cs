namespace BugInvestigationAgent.Core.Permissions;

[Flags]
public enum AgentPermission
{
    None = 0,
    ReadJira = 1 << 0,
    UseBrowser = 1 << 1,
    ReadWorkspace = 1 << 2,
    ModifyCode = 1 << 3,
    RunValidation = 1 << 4,
    ManageCredentials = 1 << 5,
    ManageMemory = 1 << 6,
    // V1 OFF by default — future skills
    GitCommit = 1 << 10,
    GitPush = 1 << 11,
    CreatePullRequest = 1 << 12,
    TriggerCi = 1 << 13,
    Deploy = 1 << 14,
    /// <summary>Create ONE new local branch (no checkout, commit, push or file change) for a still-reproducible bug.</summary>
    CreateLocalBranch = 1 << 15,
}

public interface IPermissionGate
{
    bool IsAllowed(AgentPermission permission);
    AgentPermission Allowed { get; }
}

public sealed class PermissionGate : IPermissionGate
{
    public PermissionGate(AgentPermission allowed)
    {
        Allowed = allowed;
    }

    public AgentPermission Allowed { get; }

    public bool IsAllowed(AgentPermission permission) =>
        permission == AgentPermission.None || Allowed.HasFlag(permission);
}
