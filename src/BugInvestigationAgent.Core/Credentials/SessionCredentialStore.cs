using System.Collections.Concurrent;

namespace BugInvestigationAgent.Core.Credentials;

/// <summary>
/// Identity of the person using the agent. Saved credentials are isolated per user id.
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>Stable, non-secret identifier of the current user (never empty).</summary>
    string UserId { get; }
}

/// <summary>
/// Holds credentials the user typed for ONE investigation and ONE environment, in memory only.
/// This is the default (per-investigation) credential mode: nothing is persisted unless the user opts in.
/// Entries are consumed by the login attempt and cleared when the investigation ends.
/// </summary>
public interface ISessionCredentialStore
{
    void Set(Guid investigationId, string environmentId, CredentialSecret secret);
    bool Has(Guid investigationId, string environmentId);
    /// <summary>Returns and removes the secret, so it is not kept in memory after the login attempt.</summary>
    CredentialSecret? Take(Guid investigationId, string environmentId);
    /// <summary>Drops every secret held for the investigation.</summary>
    void Clear(Guid investigationId);
}

public sealed class InMemorySessionCredentialStore : ISessionCredentialStore
{
    private readonly ConcurrentDictionary<(Guid, string), CredentialSecret> _secrets = new();

    public void Set(Guid investigationId, string environmentId, CredentialSecret secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        ArgumentNullException.ThrowIfNull(secret);
        _secrets[(investigationId, Key(environmentId))] = secret;
    }

    public bool Has(Guid investigationId, string environmentId) =>
        !string.IsNullOrWhiteSpace(environmentId) && _secrets.ContainsKey((investigationId, Key(environmentId)));

    public CredentialSecret? Take(Guid investigationId, string environmentId)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            return null;
        return _secrets.TryRemove((investigationId, Key(environmentId)), out var secret) ? secret : null;
    }

    public void Clear(Guid investigationId)
    {
        foreach (var key in _secrets.Keys.Where(k => k.Item1 == investigationId).ToList())
            _secrets.TryRemove(key, out _);
    }

    private static string Key(string environmentId) => environmentId.Trim().ToUpperInvariant();
}
