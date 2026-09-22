using System.Text.RegularExpressions;

namespace BugInvestigationAgent.Core.Configuration;

public static class JiraUrlNormalizer
{
    /// <summary>
    /// Accepts site root or any browse/board URL and returns API base, e.g. https://ehsq.atlassian.net
    /// </summary>
    public static string NormalizeBaseUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        var raw = input.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return raw.TrimEnd('/');

        // Always use scheme + host (+ non-default port). Strip /jira/software/..., /browse/..., etc.
        var builder = new UriBuilder(uri.Scheme, uri.Host);
        if (!uri.IsDefaultPort)
            builder.Port = uri.Port;

        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    /// <summary>
    /// Extracts PROJ-123 from URLs/keys, including "VKMS 4180" / "VKMS_4180".
    /// </summary>
    public static string? ExtractIssueKey(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var text = input.Trim();
        var match = Regex.Match(text, @"\b([A-Z][A-Z0-9]+)[\s_\-]+(\d+)\b", RegexOptions.IgnoreCase);
        if (match.Success)
            return $"{match.Groups[1].Value.ToUpperInvariant()}-{match.Groups[2].Value}";

        match = Regex.Match(text, @"\b([A-Z][A-Z0-9]+-\d+)\b");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : text;
    }
}
