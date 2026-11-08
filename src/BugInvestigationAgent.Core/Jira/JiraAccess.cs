using System.Net.Http.Headers;
using System.Text;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;

namespace BugInvestigationAgent.Core.Jira;

/// <summary>How to call the Jira REST API for one investigation.</summary>
public sealed class JiraAccess
{
    /// <summary>Base address ending with '/', e.g. https://site.atlassian.net/ or https://api.atlassian.com/ex/jira/{cloudId}/.</summary>
    public required Uri BaseAddress { get; init; }
    public required AuthenticationHeaderValue Authorization { get; init; }
    /// <summary>Safe description for progress messages (never contains tokens).</summary>
    public required string Description { get; init; }
    /// <summary>True when calls run with the invoking user's own Jira permissions (Forge user token).</summary>
    public bool IsUserDelegated { get; init; }
    /// <summary>Site URL for human browse links (https://site.atlassian.net). Defaults to the base address.</summary>
    public string? BrowseBaseUrl { get; init; }
}

public sealed class JiraAccessResult
{
    public JiraAccess? Access { get; init; }
    public string? Error { get; init; }
    public static JiraAccessResult Ok(JiraAccess access) => new() { Access = access };
    public static JiraAccessResult Fail(string error) => new() { Error = error };
}

/// <summary>
/// Supplies Jira read access for an investigation. Standalone mode: the locally configured API token.
/// Hosted (Forge) mode: the invoking user's Forge user token, so Jira enforces that user's permissions.
/// </summary>
public interface IJiraAccessProvider
{
    Task<JiraAccessResult> GetReadAccessAsync(RuntimeContext context, CancellationToken cancellationToken = default);
}

/// <summary>Standalone/admin UI: basic auth with the Jira account configured under Sign in / Account.</summary>
public sealed class ConfiguredJiraAccessProvider : IJiraAccessProvider
{
    private readonly IJiraConnectionSettings _jira;

    public ConfiguredJiraAccessProvider(IJiraConnectionSettings jira) => _jira = jira;

    public Task<JiraAccessResult> GetReadAccessAsync(RuntimeContext context, CancellationToken cancellationToken = default)
    {
        if (!_jira.IsConfigured)
            return Task.FromResult(JiraAccessResult.Fail(
                "Jira is not configured. Open Settings once, enter Base URL / Email / API token, then analyze the ticket again."));

        var options = _jira.GetEffective();
        var baseUrl = JiraUrlNormalizer.NormalizeBaseUrl(options.BaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Task.FromResult(JiraAccessResult.Fail("Jira Base URL is empty. Use only the site root, e.g. https://ehsq.atlassian.net."));

        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Email}:{options.ApiToken}"));
        return Task.FromResult(JiraAccessResult.Ok(new JiraAccess
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Authorization = new AuthenticationHeaderValue("Basic", auth),
            Description = $"{baseUrl} as {options.Email}"
        }));
    }
}
