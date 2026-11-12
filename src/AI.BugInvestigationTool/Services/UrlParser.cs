using System.Text.RegularExpressions;
using AI.BugInvestigationTool.Models;

namespace AI.BugInvestigationTool.Services;

/// <summary>Turns a user-supplied URL into an <see cref="InvestigationTarget"/>. Rejects anything else.</summary>
public static partial class UrlParser
{
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]+-\d+$")] private static partial Regex JiraKeyRegex();
    [GeneratedRegex(@"\b([A-Z][A-Z0-9_]+-\d+)\b")] private static partial Regex JiraKeyInTextRegex();

    public static bool TryParse(string? input, out InvestigationTarget? target, out string error)
    {
        target = null;
        error = "";
        if (string.IsNullOrWhiteSpace(input)) { error = "A Jira ticket URL or Azure DevOps pull request URL is required."; return false; }

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "The URL must be an absolute https:// URL.";
            return false;
        }

        if (IsAzureDevOpsHost(uri.Host))
            return TryParsePullRequest(uri, out target, out error);

        return TryParseJira(uri, out target, out error);
    }

    public static InvestigationTarget Parse(string input) =>
        TryParse(input, out var t, out var e) ? t! : throw new ArgumentException(e);

    private static bool IsAzureDevOpsHost(string host) =>
        host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    // https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}
    // https://{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}
    private static bool TryParsePullRequest(Uri uri, out InvestigationTarget? target, out string error)
    {
        target = null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        string org;
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Count == 0) { error = "Azure DevOps URL has no organization."; return false; }
            org = segments[0];
            segments.RemoveAt(0);
        }
        else
        {
            org = uri.Host.Split('.')[0];
        }

        var git = segments.FindIndex(s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
        var pr = segments.FindIndex(s => s.Equals("pullrequest", StringComparison.OrdinalIgnoreCase));
        if (git != 1 || pr != 3 || segments.Count < 5 || !int.TryParse(segments[4], out var id) || id <= 0)
        {
            error = "Azure DevOps URL must look like https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}.";
            return false;
        }

        target = new InvestigationTarget(TargetKind.PullRequest, uri.ToString(),
            AdoOrganization: org, AdoProject: segments[0], AdoRepository: segments[2], PullRequestId: id);
        error = "";
        return true;
    }

    // https://site.atlassian.net/browse/KEY-1, .../issues/KEY-1, any page with ?selectedIssue=KEY-1
    private static bool TryParseJira(Uri uri, out InvestigationTarget? target, out string error)
    {
        target = null;
        string? key = null;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (query["selectedIssue"] is { } sel && JiraKeyRegex().IsMatch(sel.ToUpperInvariant())) key = sel.ToUpperInvariant();

        if (key is null)
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1 && key is null; i++)
            {
                if (segments[i] is "browse" or "issues" && JiraKeyRegex().IsMatch(segments[i + 1].ToUpperInvariant()))
                    key = segments[i + 1].ToUpperInvariant();
            }
        }

        if (key is null)
        {
            error = "Not a recognised Jira issue URL (expected https://{site}/browse/KEY-123) or Azure DevOps pull request URL.";
            return false;
        }

        target = new InvestigationTarget(TargetKind.JiraIssue, uri.ToString(),
            JiraBaseUrl: $"{uri.Scheme}://{uri.Authority}", JiraKey: key);
        error = "";
        return true;
    }

    /// <summary>Finds Jira keys mentioned in free text (PR titles, branches, descriptions).</summary>
    public static List<string> FindJiraKeys(params string?[] texts) =>
        texts.Where(t => !string.IsNullOrEmpty(t))
             .SelectMany(t => JiraKeyInTextRegex().Matches(t!.ToUpperInvariant()).Select(m => m.Groups[1].Value))
             .Where(k => !k.StartsWith("UTF-", StringComparison.Ordinal) && !k.StartsWith("ISO-", StringComparison.Ordinal))
             .Distinct()
             .ToList();
}
