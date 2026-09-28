using System.Text.RegularExpressions;

namespace BugInvestigationAgent.Core.Security;

/// <summary>
/// Redacts secrets from text before LLM prompts, logs, or reports.
/// Credentials must never enter agent runtime context.
/// </summary>
public interface ISecretRedactor
{
    string Redact(string? text);
    void RegisterSecret(string? secret);
    void ClearSessionSecrets();
}

public sealed class SecretRedactor : ISecretRedactor
{
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private static readonly Regex CredentialPlaceholder = new(@"\{\{credential:[^}]+\}\}", RegexOptions.Compiled);

    public void RegisterSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 3)
            return;
        _secrets.Add(secret);
    }

    public void ClearSessionSecrets() => _secrets.Clear();

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var result = text;
        foreach (var secret in _secrets.OrderByDescending(s => s.Length))
            result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);

        // Keep placeholders as-is (they are not secrets) but normalize accidental leaks of password= patterns
        result = Regex.Replace(result, @"(?i)(password|passwd|pwd|token|api[_-]?key|secret)\s*[:=]\s*\S+", "$1=[REDACTED]");
        return result;
    }
}
