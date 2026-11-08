namespace BugInvestigationAgent.Core.Credentials;

/// <summary>
/// Metadata for a credential profile. Secrets live only in the vault.
/// </summary>
public sealed class CredentialProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public string? SystemName { get; set; }
    public string? EnvironmentName { get; set; }
    /// <summary>Registry environment ID these credentials belong to. Credentials are never shared across environments.</summary>
    public string? EnvironmentId { get; set; }
    /// <summary>Owner (see <see cref="ICurrentUserAccessor"/>). Set by the vault; profiles are only visible to their owner.</summary>
    public string? OwnerId { get; set; }
    /// <summary>True for profiles saved before per-user/per-environment isolation; never used for automatic login.</summary>
    public bool IsLegacyUnscoped => string.IsNullOrWhiteSpace(OwnerId) || string.IsNullOrWhiteSpace(EnvironmentId);
    public string? Username { get; set; }
    public string? Role { get; set; }
    public string AuthType { get; set; } = "password";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CredentialSecret
{
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string? Token { get; init; }

    /// <summary>Never print secrets, even by accident (string interpolation, debugger display, logs).</summary>
    public override string ToString() => $"CredentialSecret(Username={Username}, Password=[REDACTED])";
}

/// <summary>
/// Encrypted credential storage. Implementations scope every operation to the current user
/// (<see cref="ICurrentUserAccessor"/>): one user's saved credentials are never returned to another.
/// </summary>
public interface ICredentialVault
{
    Task<IReadOnlyList<CredentialProfile>> ListProfilesAsync(CancellationToken cancellationToken = default);
    /// <summary>Saved profile for the current user and the given environment (optionally a specific username).</summary>
    Task<CredentialProfile?> FindForEnvironmentAsync(string environmentId, string? username = null, CancellationToken cancellationToken = default);
    Task<CredentialProfile?> GetProfileAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CredentialProfile?> FindMatchAsync(string? systemName, string? environmentName, string? username, string? role, CancellationToken cancellationToken = default);
    Task<CredentialProfile> SaveProfileAsync(CredentialProfile profile, CredentialSecret secret, CancellationToken cancellationToken = default);
    Task<CredentialSecret?> ResolveSecretAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(Guid id, CancellationToken cancellationToken = default);
}
