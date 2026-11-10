using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Investigations;
using BugInvestigationAgent.Core.Jira;

namespace BugInvestigationAgent.Infrastructure.Investigations;

/// <summary>
/// Jira REST access for investigation records, legacy batches and comment publication, using the app's
/// configured Jira connection (the signed-in user's own permissions). Every call is a read (GET, plus the POST that
/// JQL search requires); nothing here can add comments or change status, priority, resolution or any other field.
/// </summary>
public sealed class JiraIssueGateway : IJiraIssueGateway
{
    private const string SnapshotFields = "summary,description,status,resolution,priority,created,updated,labels";
    private readonly IJiraAccessProvider _access;
    private readonly IHttpClientFactory _httpClientFactory;

    public JiraIssueGateway(IJiraAccessProvider access, IHttpClientFactory httpClientFactory)
    {
        _access = access;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<JiraIssueSnapshot?> GetSnapshotAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?fields={SnapshotFields}", null, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(resp, "read issue " + issueKey, cancellationToken);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        return ParseSnapshot(doc.RootElement);
    }

    public async Task<(string Jql, IReadOnlyList<string> Keys)> GetFilterIssuesAsync(string filterId, CancellationToken cancellationToken = default)
    {
        string jql;
        using (var resp = await SendAsync(HttpMethod.Get, $"rest/api/3/filter/{Uri.EscapeDataString(filterId)}", null, cancellationToken))
        {
            await EnsureSuccessAsync(resp, "read filter " + filterId, cancellationToken);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            jql = doc.RootElement.GetProperty("jql").GetString() ?? throw new InvalidOperationException($"Filter {filterId} has no JQL.");
        }

        var keys = new List<string>();
        string? pageToken = null;
        for (var page = 0; page < 100; page++) // hard stop: 100 pages x 100 issues
        {
            var body = new JsonObject { ["jql"] = jql, ["fields"] = new JsonArray("summary"), ["maxResults"] = 100 };
            if (pageToken is not null)
                body["nextPageToken"] = pageToken;
            using var resp = await SendAsync(HttpMethod.Post, "rest/api/3/search/jql", body.ToJsonString(), cancellationToken);
            await EnsureSuccessAsync(resp, "search filter " + filterId, cancellationToken);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            foreach (var issue in doc.RootElement.GetProperty("issues").EnumerateArray())
            {
                var key = issue.GetProperty("key").GetString();
                if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    keys.Add(key);
            }
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrEmpty(pageToken) || (doc.RootElement.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.True))
                break;
        }
        return (jql, keys);
    }

    public static JiraIssueSnapshot ParseSnapshot(JsonElement issue)
    {
        var key = issue.GetProperty("key").GetString() ?? "";
        var f = issue.GetProperty("fields");
        string? Name(string field) =>
            f.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("name", out var n) ? n.GetString() : null;
        string? Str(string field) => f.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        DateTimeOffset? Date(string field) => DateTimeOffset.TryParse(Str(field), out var d) ? d : null;

        var summary = Str("summary");
        var description = f.TryGetProperty("description", out var dsc) && dsc.ValueKind != JsonValueKind.Null ? dsc.GetRawText() : "";
        var status = Name("status");
        var resolution = Name("resolution");
        var labels = f.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : new List<string>();
        return new JiraIssueSnapshot(key, summary, status, resolution, Name("priority"), Date("created"), Date("updated"), labels,
            Fingerprint(summary, description, status, resolution));
    }

    /// <summary>Material-change fingerprint: comments, watchers and the "updated" timestamp do not count as material.</summary>
    public static string Fingerprint(string? summary, string? descriptionJson, string? status, string? resolution) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{summary}\u001f{descriptionJson}\u001f{status}\u001f{resolution}")));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? jsonBody, CancellationToken cancellationToken)
    {
        var result = await _access.GetReadAccessAsync(new RuntimeContext(), cancellationToken);
        var access = result.Access ?? throw new InvalidOperationException("Jira " + (result.Error ?? "access is not available."));
        var client = _httpClientFactory.CreateClient("jira");
        using var request = new HttpRequestMessage(method, new Uri(access.BaseAddress, path));
        request.Headers.Authorization = access.Authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string what, CancellationToken cancellationToken)
    {
        if (resp.IsSuccessStatusCode)
            return;
        // Jira error bodies carry messages, never our credentials; keep them short.
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        throw new JiraRequestException((int)resp.StatusCode, $"Jira refused to {what}: HTTP {(int)resp.StatusCode} {(body.Length > 300 ? body[..300] : body)}");
    }
}

public sealed class JiraRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
