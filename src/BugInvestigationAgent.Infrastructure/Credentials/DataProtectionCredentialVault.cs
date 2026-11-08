using System.Text.Json;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Credentials;

/// <summary>
/// Credential vault backed by SQLite + ASP.NET Core Data Protection (AES-256-CBC + HMACSHA256 payloads;
/// key ring protected with DPAPI on Windows). Every operation is scoped to the current user, and each secret is
/// encrypted with a purpose bound to (owner, environment) — a ciphertext copied into another user's or another
/// environment's row cannot be decrypted. Secrets are never returned by list/get operations.
/// </summary>
public sealed class DataProtectionCredentialVault : ICredentialVault
{
    private const string PurposeV2 = "BugInvestigationAgent.Credentials.v2";

    private readonly AgentDbContext _db;
    private readonly IDataProtectionProvider _provider;
    private readonly ICurrentUserAccessor _currentUser;

    public DataProtectionCredentialVault(AgentDbContext db, IDataProtectionProvider dataProtectionProvider, ICurrentUserAccessor currentUser)
    {
        _db = db;
        _provider = dataProtectionProvider;
        _currentUser = currentUser;
    }

    private string Owner => _currentUser.UserId;

    public async Task<IReadOnlyList<CredentialProfile>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        var owner = Owner;
        // Own profiles + legacy unscoped rows (metadata only, so they can be reviewed and deleted; never resolvable).
        var rows = await _db.CredentialProfiles.AsNoTracking()
            .Where(x => x.OwnerId == owner || x.OwnerId == null)
            .OrderBy(x => x.Label)
            .ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    public async Task<CredentialProfile?> GetProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var owner = Owner;
        var row = await _db.CredentialProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && (x.OwnerId == owner || x.OwnerId == null), cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task<CredentialProfile?> FindForEnvironmentAsync(string environmentId, string? username = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            return null;
        var owner = Owner;
        var env = NormalizeEnvironmentId(environmentId);
        var query = _db.CredentialProfiles.AsNoTracking().Where(x => x.OwnerId == owner && x.EnvironmentId == env);
        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(x => x.Username == username);

        // SQLite cannot ORDER BY DateTimeOffset in SQL — order on the client.
        var rows = await query.ToListAsync(cancellationToken);
        var row = rows.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        return row is null ? null : Map(row);
    }

    public async Task<CredentialProfile?> FindMatchAsync(
        string? systemName,
        string? environmentName,
        string? username,
        string? role,
        CancellationToken cancellationToken = default)
    {
        var owner = Owner;
        var query = _db.CredentialProfiles.AsNoTracking().Where(x => x.OwnerId == owner && x.EnvironmentId != null);

        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(x => x.Username == username);
        else if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(x => x.Role == role || x.Username == role);

        if (!string.IsNullOrWhiteSpace(systemName))
            query = query.Where(x => x.SystemName == null || x.SystemName == systemName);
        // Environment must match exactly — credentials are never interchangeable between environments.
        if (!string.IsNullOrWhiteSpace(environmentName))
            query = query.Where(x => x.EnvironmentName == environmentName || x.EnvironmentId == environmentName);
        else
            return null;

        var rows = await query.ToListAsync(cancellationToken);
        var row = rows.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        return row is null ? null : Map(row);
    }

    public async Task<CredentialProfile> SaveProfileAsync(CredentialProfile profile, CredentialSecret secret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profile.EnvironmentId))
            throw new InvalidOperationException("Credentials must be saved for a specific environment.");

        var owner = Owner;
        var env = NormalizeEnvironmentId(profile.EnvironmentId);
        var now = DateTimeOffset.UtcNow;

        var existing = await _db.CredentialProfiles.FirstOrDefaultAsync(x => x.Id == profile.Id, cancellationToken);
        if (existing is not null && existing.OwnerId != owner)
        {
            // Never overwrite (or take over) another user's or a legacy profile — create a new one instead.
            existing = null;
            profile.Id = Guid.NewGuid();
        }

        if (existing is null)
        {
            existing = new CredentialProfileEntity { Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id, CreatedAt = now };
            _db.CredentialProfiles.Add(existing);
        }

        existing.OwnerId = owner;
        existing.EnvironmentId = env;
        existing.Label = string.IsNullOrWhiteSpace(profile.Label)
            ? $"{profile.EnvironmentName ?? env} + {profile.Username ?? secret.Username}"
            : profile.Label;
        existing.SystemName = profile.SystemName;
        existing.EnvironmentName = profile.EnvironmentName ?? env;
        existing.Username = profile.Username ?? secret.Username;
        existing.Role = profile.Role;
        existing.AuthType = profile.AuthType;
        existing.EncryptedSecret = Protector(owner, env).Protect(JsonSerializer.Serialize(secret));
        existing.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        return Map(existing);
    }

    public async Task<CredentialSecret?> ResolveSecretAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var owner = Owner;
        var row = await _db.CredentialProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == profileId && x.OwnerId == owner, cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.EnvironmentId))
            return null;

        try
        {
            var json = Protector(owner, row.EnvironmentId).Unprotect(row.EncryptedSecret);
            return JsonSerializer.Deserialize<CredentialSecret>(json);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Wrong owner/environment purpose or rotated/lost key ring — treat as missing, never leak details.
            return null;
        }
    }

    public async Task DeleteProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var owner = Owner;
        var row = await _db.CredentialProfiles
            .FirstOrDefaultAsync(x => x.Id == id && (x.OwnerId == owner || x.OwnerId == null), cancellationToken);
        if (row is null)
            return;
        _db.CredentialProfiles.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private IDataProtector Protector(string owner, string environmentId) =>
        _provider.CreateProtector(PurposeV2, owner, NormalizeEnvironmentId(environmentId));

    private static string NormalizeEnvironmentId(string environmentId) => environmentId.Trim().ToUpperInvariant();

    private static CredentialProfile Map(CredentialProfileEntity e) => new()
    {
        Id = e.Id,
        Label = e.Label,
        SystemName = e.SystemName,
        EnvironmentName = e.EnvironmentName,
        EnvironmentId = e.EnvironmentId,
        OwnerId = e.OwnerId,
        Username = e.Username,
        Role = e.Role,
        AuthType = e.AuthType,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt
    };
}
