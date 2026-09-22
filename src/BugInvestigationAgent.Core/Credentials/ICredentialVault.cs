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
}

public interface ICredentialVault
{
    Task<IReadOnlyList<CredentialProfile>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<CredentialProfile?> GetProfileAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CredentialProfile?> FindMatchAsync(string? systemName, string? environmentName, string? username, string? role, CancellationToken cancellationToken = default);
    Task<CredentialProfile> SaveProfileAsync(CredentialProfile profile, CredentialSecret secret, CancellationToken cancellationToken = default);
    Task<CredentialSecret?> ResolveSecretAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(Guid id, CancellationToken cancellationToken = default);
}
