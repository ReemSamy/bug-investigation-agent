using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Skills;

namespace BugInvestigationAgent.Skills.Credentials;

public sealed class CredentialSkill : ISkill
{
    private readonly ICredentialVault _vault;

    public CredentialSkill(ICredentialVault vault) => _vault = vault;

    public string SkillId => SkillIds.Credential;
    public string DisplayName => "Credential";
    public AgentPermission RequiredPermissions => AgentPermission.ManageCredentials;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        if (ctx.CredentialProfileId is Guid id)
        {
            var profile = await _vault.GetProfileAsync(id, cancellationToken);
            if (profile is null)
                return SkillResult.Fail("Selected credential profile was not found.");
            if (profile.IsLegacyUnscoped ||
                !string.Equals(profile.EnvironmentId, ctx.SelectedEnvironmentId, StringComparison.OrdinalIgnoreCase))
                return SkillResult.Fail("The selected credential profile belongs to a different environment.");
            request.Progress?.Report($"Using credential profile '{profile.Label}' (secrets stay in vault).");
            return SkillResult.Ok("Credential profile resolved.");
        }

        // Credentials are environment-specific: only profiles saved for the selected environment are considered.
        if (string.IsNullOrWhiteSpace(ctx.SelectedEnvironmentId))
            return SkillResult.Fail("Select an environment before looking up saved credentials.");

        var match = await _vault.FindForEnvironmentAsync(ctx.SelectedEnvironmentId!, ctx.Username, cancellationToken);
        if (match is null)
            return SkillResult.Fail("No saved credentials for the selected environment.");

        ctx.CredentialProfileId = match.Id;
        request.Progress?.Report($"Matched credential profile '{match.Label}'.");
        return SkillResult.Ok("Credential profile matched.");
    }
}
