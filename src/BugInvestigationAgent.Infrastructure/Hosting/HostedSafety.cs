using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Permissions;
using Microsoft.Extensions.Configuration;

namespace BugInvestigationAgent.Infrastructure.Hosting;

/// <summary>
/// Hosted (team) identity: the ONLY user identity is the verified Atlassian account of the job being executed.
/// There is no fallback to the Windows/OS account — outside a job the vault refuses to operate (fail closed).
/// </summary>
public sealed class HostedOnlyCurrentUserAccessor : ICurrentUserAccessor
{
    public string UserId => HostedJobScope.Current?.OwnerId
        ?? throw new InvalidOperationException("No verified Jira user is associated with this operation (hosted mode never falls back to the OS account).");
}

/// <summary>Initial team rollout: everything that could change code, repositories or deployments is off, whatever the configuration says.</summary>
public static class HostedPermissions
{
    // Hosted workers have no developer clone: no local branches there either.
    public const AgentPermission ForbiddenInRollout = ReadOnlyRollout.Forbidden | AgentPermission.CreateLocalBranch;

    public static IPermissionGate Mask(IPermissionGate configured) =>
        new PermissionGate(ReadOnlyRollout.Mask(configured).Allowed & ~AgentPermission.CreateLocalBranch);
}

/// <summary>
/// Refuses to start a hosted service in a non-development environment with an insecure or incomplete configuration.
/// </summary>
public static class HostedConfigurationValidator
{
    public static IReadOnlyList<string> Validate(IConfiguration configuration, bool isDevelopment, bool isApi)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(configuration["Forge:AppId"]))
            errors.Add("Forge:AppId is required (Forge Invocation Token audience).");
        if (isDevelopment)
            return errors;

        if (!(configuration.GetSection("Forge:AllowedCloudIds").Get<string[]>() is { Length: > 0 }))
            errors.Add("Forge:AllowedCloudIds must list your Jira site cloud id(s) outside Development.");
        if (!string.Equals(configuration["Hosting:DatabaseProvider"], "PostgreSQL", StringComparison.OrdinalIgnoreCase))
            errors.Add("Hosting:DatabaseProvider must be PostgreSQL outside Development (SQLite is single-machine only).");
        if (string.IsNullOrWhiteSpace(configuration["Hosting:BlobServiceUri"]) || string.IsNullOrWhiteSpace(configuration["Hosting:DataProtectionKeyId"]))
            errors.Add("Hosting:BlobServiceUri and Hosting:DataProtectionKeyId are required so API and workers share a Key Vault–protected key ring.");
        if (isApi && !(configuration["Forge:PublicBaseUrl"] ?? "").StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            errors.Add("Forge:PublicBaseUrl must be the public https URL of the API.");
        if (string.IsNullOrWhiteSpace(configuration["Hosting:ServiceBusNamespace"]))
            errors.Add("Hosting:ServiceBusNamespace is required outside Development (durable queue with dead-lettering).");
        return errors;
    }

    public static void ThrowIfInvalid(IConfiguration configuration, bool isDevelopment, bool isApi)
    {
        var errors = Validate(configuration, isDevelopment, isApi);
        if (errors.Count > 0)
            throw new InvalidOperationException("Insecure or incomplete hosted configuration:\n - " + string.Join("\n - ", errors));
    }
}
