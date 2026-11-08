using System.Text.RegularExpressions;

namespace BugInvestigationAgent.Core.Security;

/// <summary>
/// Redacts secrets from text before LLM prompts, logs, or reports.
/// Credentials must never enter agent runtime context.
/// Secrets are tracked per investigation so concurrent investigations never clear each other's redaction set.
/// </summary>
public interface ISecretRedactor
{
    string Redact(string? text);
    /// <summary>Registers a secret not tied to a specific investigation.</summary>
    void RegisterSecret(string? secret);
    /// <summary>Registers a secret for one investigation (removed by <see cref="ClearSessionSecrets(Guid)"/>).</summary>
    void RegisterSecret(string? secret, Guid investigationId);
    /// <summary>Forgets only the given investigation's secrets.</summary>
    void ClearSessionSecrets(Guid investigationId);
    /// <summary>Forgets secrets registered without an investigation.</summary>
    void ClearSessionSecrets();
}

public sealed class SecretRedactor : ISecretRedactor
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HashSet<string>> _secrets = new();

    public void RegisterSecret(string? secret) => RegisterSecret(secret, Guid.Empty);

    public void RegisterSecret(string? secret, Guid investigationId)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 3)
            return;
        lock (_gate)
        {
            if (!_secrets.TryGetValue(investigationId, out var set))
                _secrets[investigationId] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(secret);
        }
    }

    public void ClearSessionSecrets(Guid investigationId)
    {
        lock (_gate)
            _secrets.Remove(investigationId);
    }

    public void ClearSessionSecrets() => ClearSessionSecrets(Guid.Empty);

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        string[] all;
        lock (_gate)
            all = _secrets.Values.SelectMany(s => s).Distinct().OrderByDescending(s => s.Length).ToArray();

        // Redacting every known secret (any investigation) is always safe — it can only hide more.
        var result = text;
        foreach (var secret in all)
            result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);

        result = Regex.Replace(result, @"(?i)(password|passwd|pwd|token|api[_-]?key|secret)\s*[:=]\s*\S+", "$1=[REDACTED]");
        result = Regex.Replace(result, @"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-\._~\+/]{12,}=*", "$1 [REDACTED]");
        return result;
    }
}
