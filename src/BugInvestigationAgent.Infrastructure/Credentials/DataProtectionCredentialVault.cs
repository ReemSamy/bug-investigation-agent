using System.Text.Json;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Credentials;

public sealed class DataProtectionCredentialVault : ICredentialVault
{
    private readonly AgentDbContext _db;
    private readonly IDataProtector _protector;

    public DataProtectionCredentialVault(AgentDbContext db, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector("BugInvestigationAgent.Credentials.v1");
    }

    public async Task<IReadOnlyList<CredentialProfile>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.CredentialProfiles.AsNoTracking().OrderBy(x => x.Label).ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    public async Task<CredentialProfile?> GetProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.CredentialProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return row is null ? null : Map(row);
    }

    public async Task<CredentialProfile?> FindMatchAsync(
        string? systemName,
        string? environmentName,
        string? username,
        string? role,
        CancellationToken cancellationToken = default)
    {
        var query = _db.CredentialProfiles.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(username))
            query = query.Where(x => x.Username == username);
        else if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(x => x.Role == role || x.Username == role);

        if (!string.IsNullOrWhiteSpace(systemName))
            query = query.Where(x => x.SystemName == null || x.SystemName == systemName);
        if (!string.IsNullOrWhiteSpace(environmentName))
            query = query.Where(x => x.EnvironmentName == null || x.EnvironmentName == environmentName);

        // SQLite cannot ORDER BY DateTimeOffset in SQL — order on the client.
        var rows = await query.ToListAsync(cancellationToken);
        var row = rows.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        return row is null ? null : Map(row);
    }

    public async Task<CredentialProfile> SaveProfileAsync(CredentialProfile profile, CredentialSecret secret, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(secret);
        var encrypted = _protector.Protect(payload);
        var now = DateTimeOffset.UtcNow;

        var existing = await _db.CredentialProfiles.FirstOrDefaultAsync(x => x.Id == profile.Id, cancellationToken);
        if (existing is null)
        {
            existing = new CredentialProfileEntity { Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id, CreatedAt = now };
            _db.CredentialProfiles.Add(existing);
        }

        existing.Label = string.IsNullOrWhiteSpace(profile.Label)
            ? $"{profile.SystemName} + {profile.Username ?? profile.Role}"
            : profile.Label;
        existing.SystemName = profile.SystemName;
        existing.EnvironmentName = profile.EnvironmentName;
        existing.Username = profile.Username ?? secret.Username;
        existing.Role = profile.Role;
        existing.AuthType = profile.AuthType;
        existing.EncryptedSecret = encrypted;
        existing.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        return Map(existing);
    }

    public async Task<CredentialSecret?> ResolveSecretAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var row = await _db.CredentialProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == profileId, cancellationToken);
        if (row is null)
            return null;

        var json = _protector.Unprotect(row.EncryptedSecret);
        return JsonSerializer.Deserialize<CredentialSecret>(json);
    }

    public async Task DeleteProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.CredentialProfiles.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null)
            return;
        _db.CredentialProfiles.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static CredentialProfile Map(CredentialProfileEntity e) => new()
    {
        Id = e.Id,
        Label = e.Label,
        SystemName = e.SystemName,
        EnvironmentName = e.EnvironmentName,
        Username = e.Username,
        Role = e.Role,
        AuthType = e.AuthType,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt
    };
}
