using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Jira;
using Microsoft.Extensions.Logging;

namespace BugInvestigationAgent.Skills.Jira;

public sealed class JiraIssueSearchClient : IJiraIssueSearchClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IJiraAccessProvider _access;
    private readonly ILogger<JiraIssueSearchClient> _logger;

    public JiraIssueSearchClient(
        IHttpClientFactory httpClientFactory,
        IJiraAccessProvider access,
        ILogger<JiraIssueSearchClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _access = access;
        _logger = logger;
    }

    public Task<IReadOnlyList<JiraSearchHit>> SearchAsync(string jql, int maxResults, CancellationToken cancellationToken = default) =>
        SearchAsync(new RuntimeContext(), jql, maxResults, cancellationToken);

    /// <summary>Searches with the investigation's Jira access (invoking user's token in hosted mode — results respect their permissions).</summary>
    public async Task<IReadOnlyList<JiraSearchHit>> SearchAsync(RuntimeContext context, string jql, int maxResults, CancellationToken cancellationToken = default)
    {
        var accessResult = await _access.GetReadAccessAsync(context, cancellationToken);
        if (accessResult.Access is null)
            return Array.Empty<JiraSearchHit>();

        var client = _httpClientFactory.CreateClient("jira");
        client.BaseAddress = accessResult.Access.BaseAddress;
        client.DefaultRequestHeaders.Authorization = accessResult.Access.Authorization;
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Prefer classic search; fall back to /search/jql if needed.
        var encoded = Uri.EscapeDataString(jql);
        var path = $"rest/api/3/search?jql={encoded}&maxResults={Math.Clamp(maxResults, 1, 50)}&fields=summary,status,description";
        using var resp = await client.GetAsync(path, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Jira search failed HTTP {Code}: {Body}", (int)resp.StatusCode, Truncate(body, 300));
            // Try POST search/jql (newer Cloud endpoint)
            var payload = JsonSerializer.Serialize(new
            {
                jql,
                maxResults = Math.Clamp(maxResults, 1, 50),
                fields = new[] { "summary", "status", "description" }
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp2 = await client.PostAsync("rest/api/3/search/jql", content, cancellationToken);
            body = await resp2.Content.ReadAsStringAsync(cancellationToken);
            if (!resp2.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jira search/jql failed HTTP {Code}", (int)resp2.StatusCode);
                return Array.Empty<JiraSearchHit>();
            }
        }

        return ParseHits(body, (accessResult.Access.BrowseBaseUrl ?? accessResult.Access.BaseAddress.ToString()).TrimEnd('/'));
    }

    private static IReadOnlyList<JiraSearchHit> ParseHits(string json, string baseUrl)
    {
        var hits = new List<JiraSearchHit>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("issues", out var issues))
            return hits;

        foreach (var issue in issues.EnumerateArray())
        {
            var key = issue.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(key))
                continue;

            string? summary = null;
            string? status = null;
            string? description = null;
            if (issue.TryGetProperty("fields", out var fields))
            {
                summary = fields.TryGetProperty("summary", out var s) ? s.GetString() : null;
                if (fields.TryGetProperty("status", out var st) && st.TryGetProperty("name", out var sn))
                    status = sn.GetString();
                description = ExtractText(fields.TryGetProperty("description", out var d) ? d : default);
            }

            var hit = new JiraSearchHit
            {
                Key = key,
                Summary = summary,
                Status = status,
                Description = description,
                Url = $"{baseUrl}/browse/{key}",
                ReproductionSteps = JiraSkill.ExtractSteps(description)
            };
            hits.Add(hit);
        }

        return hits;
    }

    private static string? ExtractText(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Undefined => null,
            JsonValueKind.Null => null,
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Array => string.Join("\n", el.EnumerateArray().Select(ExtractText).Where(s => !string.IsNullOrWhiteSpace(s))),
            JsonValueKind.Object when el.TryGetProperty("content", out var content) => ExtractText(content),
            JsonValueKind.Object when el.TryGetProperty("text", out var text) => text.GetString(),
            JsonValueKind.Object => el.ToString(),
            _ => el.ToString()
        };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
