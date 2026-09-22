using System.Text.RegularExpressions;

namespace BugInvestigationAgent.Core.Context;

public static class EnvironmentUrlParser
{
    public static string? FindFirstUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var m = Regex.Match(text, @"https?://[^\s\)\]\>""']+", RegexOptions.IgnoreCase);
        if (m.Success)
            return NormalizeUrl(m.Value.TrimEnd('.', ',', ';'));

        m = Regex.Match(
            text,
            @"(?i)\b((?:[a-z0-9-]+\.)+(?:azurewebsites\.net|cloudapp\.net|localhost|local|dev|test|com|net|org|io)(?:/[^\s\)\]\>""']*)?)");
        if (m.Success)
            return NormalizeUrl(m.Groups[1].Value.TrimEnd('.', ',', ';'));

        m = Regex.Match(text, @"(?i)\b(?:environment|url|env)\s*[:=]\s*([^\s\n]+)");
        if (m.Success && m.Groups[1].Value.Contains('.', StringComparison.Ordinal))
            return NormalizeUrl(m.Groups[1].Value.TrimEnd('.', ',', ';'));

        return null;
    }

    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim().TrimEnd('/');
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return v;
        if (v.Contains('.') || v.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            return "https://" + v;
        return null;
    }
}
