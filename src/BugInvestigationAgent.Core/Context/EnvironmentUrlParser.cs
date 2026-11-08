using System.Text.RegularExpressions;

namespace BugInvestigationAgent.Core.Context;

/// <summary>
/// Finds portal/application environment URLs. Rejects Jira/ADO/GitHub/PR links —
/// those are ticket metadata, not where Playwright should log in.
/// </summary>
public static class EnvironmentUrlParser
{
    private static readonly string[] RejectedHostFragments =
    [
        "dev.azure.com",
        "visualstudio.com",
        "atlassian.net",
        "atlassian.com",
        "jira.com",
        "github.com",
        "githubusercontent.com",
        "gitlab.com",
        "bitbucket.org",
        "microsoft.com",
        "office.com",
        "live.com",
        "login.microsoftonline.com"
    ];

    private static readonly string[] RejectedPathFragments =
    [
        "/_git/",
        "/pullrequest/",
        "/pull/",
        "/browse/",
        "/issues/",
        "/wiki/"
    ];

    public static string? FindFirstUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (var candidate in EnumerateRawUrls(text))
        {
            var normalized = NormalizeUrl(candidate);
            if (IsLikelyPortalEnvironmentUrl(normalized))
                return normalized;
        }

        var labeled = Regex.Match(text, @"(?i)\b(?:environment\s*url|env(?:ironment)?\s*url|portal\s*url|url)\s*[:=]\s*([^\s\n]+)");
        if (labeled.Success)
        {
            var normalized = NormalizeUrl(labeled.Groups[1].Value.TrimEnd('.', ',', ';'));
            if (IsLikelyPortalEnvironmentUrl(normalized))
                return normalized;
        }

        return null;
    }

    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim().TrimEnd('/');
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return IsLikelyPortalEnvironmentUrl(v) ? v : null;
        if (v.Contains('.') || v.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            var withScheme = "https://" + v;
            return IsLikelyPortalEnvironmentUrl(withScheme) ? withScheme : null;
        }

        return null;
    }

    /// <summary>
    /// True for application hosts (e.g. *.azurewebsites.net, custom portal domains).
    /// False for issue trackers, source control, and identity-provider home pages alone.
    /// </summary>
    public static bool IsLikelyPortalEnvironmentUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
            return false;

        foreach (var bad in RejectedHostFragments)
        {
            if (host.Equals(bad, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + bad, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var pathAndQuery = uri.PathAndQuery;
        foreach (var badPath in RejectedPathFragments)
        {
            if (pathAndQuery.Contains(badPath, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Bare identity / marketing roots without an app host are not environments.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".cloudapp.net", StringComparison.OrdinalIgnoreCase))
            return true;

        // Custom domains: require a real hostname (not a tracker) — accept if not rejected above.
        return host.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds a pull-request / merge-request URL in ticket text (ADO, GitHub, etc.).
    /// These are fix references — never portal login targets.
    /// </summary>
    public static string? FindFixPullRequestUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (Match m in Regex.Matches(text, @"https?://[^\s\)\]\>""']+", RegexOptions.IgnoreCase))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ')');
            if (IsFixPullRequestUrl(url))
                return url;
        }

        return null;
    }

    public static bool IsFixPullRequestUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;

        var path = uri.PathAndQuery;
        return path.Contains("/pullrequest/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/pull/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/merge_requests/", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(path, @"/pulls?/\d+", RegexOptions.IgnoreCase);
    }

    private static IEnumerable<string> EnumerateRawUrls(string text)
    {
        foreach (Match m in Regex.Matches(text, @"https?://[^\s\)\]\>""']+", RegexOptions.IgnoreCase))
            yield return m.Value.TrimEnd('.', ',', ';');

        foreach (Match m in Regex.Matches(
                     text,
                     @"(?i)\b((?:[a-z0-9-]+\.)+(?:azurewebsites\.net|cloudapp\.net|localhost)(?:/[^\s\)\]\>""']*)?)"))
            yield return m.Groups[1].Value.TrimEnd('.', ',', ';');
    }
}
