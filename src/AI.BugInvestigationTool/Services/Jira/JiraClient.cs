using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AI.BugInvestigationTool.Models;

namespace AI.BugInvestigationTool.Services.Jira;

public sealed class ExternalServiceException(string service, string message, int? statusCode = null) : Exception($"{service}: {message}")
{
    public string Service { get; } = service;
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// Jira Cloud REST API v3. Read operations are used during investigation; write operations
/// (comment, attachment, transition) are only called from the approval path.
/// </summary>
public sealed class JiraClient
{
    private const int PageSize = 100;
    private const int MaxPages = 50;
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly AuthenticationHeaderValue _auth;

    public JiraClient(HttpClient http, string baseUrl, string email, string apiToken)
    {
        _http = http;
        _base = new Uri(baseUrl.TrimEnd('/') + "/");
        _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}")));
    }

    public Uri BaseUri => _base;

    public async Task<JiraIssue> GetIssueAsync(string key, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"rest/api/3/issue/{Uri.EscapeDataString(key)}?expand=names&fields=*all", ct);
        var root = doc.RootElement;
        var fields = root.GetProperty("fields");
        var names = root.TryGetProperty("names", out var n) ? n : default;

        var issue = new JiraIssue
        {
            Id = Str(root, "id"),
            Key = Str(root, "key"),
            Url = new Uri(_base, $"browse/{Str(root, "key")}").ToString(),
            Summary = Str(fields, "summary"),
            Description = Adf.ToText(Prop(fields, "description")),
            Environment = Adf.ToText(Prop(fields, "environment")),
            Priority = Str(Prop(fields, "priority"), "name"),
            Status = Str(Prop(fields, "status"), "name"),
            IssueType = Str(Prop(fields, "issuetype"), "name"),
            AffectedVersions = Names(Prop(fields, "versions")),
            FixVersions = Names(Prop(fields, "fixVersions")),
            Components = Names(Prop(fields, "components")),
            Labels = Prop(fields, "labels") is { ValueKind: JsonValueKind.Array } l ? l.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
            Assignee = NullIfEmpty(Str(Prop(fields, "assignee"), "displayName")),
            Reporter = NullIfEmpty(Str(Prop(fields, "reporter"), "displayName")),
        };

        if (Prop(fields, "parent") is { ValueKind: JsonValueKind.Object } parent)
            issue.Parent = $"{Str(parent, "key")} {Str(Prop(parent, "fields"), "summary")}".Trim();

        if (Prop(fields, "subtasks") is { ValueKind: JsonValueKind.Array } subs)
            issue.Subtasks = subs.EnumerateArray().Select(s => $"{Str(s, "key")} {Str(Prop(s, "fields"), "summary")} [{Str(Prop(Prop(s, "fields"), "status"), "name")}]").ToList();

        if (Prop(fields, "issuelinks") is { ValueKind: JsonValueKind.Array } links)
        {
            foreach (var link in links.EnumerateArray())
            {
                var type = Prop(link, "type");
                if (Prop(link, "outwardIssue") is { ValueKind: JsonValueKind.Object } o)
                    issue.LinkedIssues.Add($"{Str(type, "outward")} {Str(o, "key")} {Str(Prop(o, "fields"), "summary")}");
                if (Prop(link, "inwardIssue") is { ValueKind: JsonValueKind.Object } i)
                    issue.LinkedIssues.Add($"{Str(type, "inward")} {Str(i, "key")} {Str(Prop(i, "fields"), "summary")}");
            }
        }

        if (Prop(fields, "attachment") is { ValueKind: JsonValueKind.Array } atts)
        {
            issue.Attachments = atts.EnumerateArray().Select(a => new JiraAttachment
            {
                Id = Str(a, "id"),
                FileName = Str(a, "filename"),
                MimeType = Str(a, "mimeType"),
                Size = Prop(a, "size") is { ValueKind: JsonValueKind.Number } sz ? sz.GetInt64() : 0,
                ContentUrl = Str(a, "content"),
            }).ToList();
        }

        // Custom fields: resolve display names through the "names" map; never assume field IDs.
        foreach (var f in fields.EnumerateObject())
        {
            if (!f.Name.StartsWith("customfield_", StringComparison.Ordinal) || f.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            var display = names.ValueKind == JsonValueKind.Object && names.TryGetProperty(f.Name, out var dn) ? dn.GetString() ?? f.Name : f.Name;
            if (display.Equals("Sprint", StringComparison.OrdinalIgnoreCase))
            {
                issue.Sprints = f.Value.ValueKind == JsonValueKind.Array
                    ? f.Value.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.Object ? $"{Str(s, "name")} ({Str(s, "state")})" : s.ToString()).ToList()
                    : [FlattenValue(f.Value)];
                continue;
            }
            var text = FlattenValue(f.Value);
            if (!string.IsNullOrWhiteSpace(text)) issue.CustomFields[display] = Truncate(text, 2000);
        }

        issue.Comments = (await GetPagedAsync($"rest/api/3/issue/{Uri.EscapeDataString(key)}/comment?orderBy=created", "comments", ct))
            .Select(c => new JiraComment
            {
                Author = Str(Prop(c, "author"), "displayName"),
                Created = DateTimeOffset.TryParse(Str(c, "created"), out var d) ? d : null,
                Body = Adf.ToText(Prop(c, "body")),
            }).ToList();

        issue.History = (await GetPagedAsync($"rest/api/3/issue/{Uri.EscapeDataString(key)}/changelog", "values", ct))
            .SelectMany(h => (Prop(h, "items") is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .Select(i => $"{Str(h, "created")[..Math.Min(16, Str(h, "created").Length)]} {Str(Prop(h, "author"), "displayName")}: {Str(i, "field")} '{Str(i, "fromString")}' → '{Str(i, "toString")}'"))
            .ToList();

        return issue;
    }

    /// <summary>Generic startAt/maxResults pagination used by comments, changelog and search.</summary>
    internal async Task<List<JsonElement>> GetPagedAsync(string path, string arrayProperty, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var startAt = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            var sep = path.Contains('?') ? '&' : '?';
            using var doc = await GetJsonAsync($"{path}{sep}startAt={startAt}&maxResults={PageSize}", ct);
            var root = doc.RootElement;
            var items = root.TryGetProperty(arrayProperty, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(e => e.Clone()).ToList()
                : [];
            all.AddRange(items);
            startAt += items.Count;

            var isLast = root.TryGetProperty("isLast", out var il) && il.ValueKind == JsonValueKind.True;
            var total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : (int?)null;
            if (items.Count == 0 || isLast || (total is not null && startAt >= total)) break;
            if (total is null && !root.TryGetProperty("isLast", out _)) break; // no paging metadata → single page
        }
        return all;
    }

    /// <summary>Downloads an attachment if it is on the Jira host itself (never follow arbitrary URLs).</summary>
    public async Task<byte[]?> DownloadAttachmentAsync(JiraAttachment attachment, int maxBytes, CancellationToken ct)
    {
        if (attachment.Size > maxBytes) return null;
        if (!Uri.TryCreate(attachment.ContentUrl, UriKind.Absolute, out var url) || !url.Host.Equals(_base.Host, StringComparison.OrdinalIgnoreCase))
            return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = _auth;
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length > maxBytes ? null : bytes;
    }

    public async Task<List<string>> SearchAsync(string jql, int max, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&fields=summary,status,issuetype&maxResults={Math.Clamp(max, 1, 50)}", ct);
        return doc.RootElement.TryGetProperty("issues", out var issues)
            ? issues.EnumerateArray().Select(i => $"{Str(i, "key")} [{Str(Prop(Prop(i, "fields"), "status"), "name")}] {Str(Prop(i, "fields"), "summary")}").ToList()
            : [];
    }

    public async Task<string> AddCommentAsync(string key, JsonNode adfBody, CancellationToken ct)
    {
        var payload = new JsonObject { ["body"] = adfBody };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, $"rest/api/3/issue/{Uri.EscapeDataString(key)}/comment"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var doc = await SendJsonAsync(req, ct);
        return Str(doc.RootElement, "id");
    }

    public async Task<List<string>> AddAttachmentsAsync(string key, IEnumerable<(string FileName, byte[] Content)> files, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, content) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(part, "file", name);
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, $"rest/api/3/issue/{Uri.EscapeDataString(key)}/attachments")) { Content = form };
        req.Headers.Add("X-Atlassian-Token", "no-check");
        using var doc = await SendJsonAsync(req, ct);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(a => Str(a, "filename")).ToList() : [];
    }

    public async Task<List<JiraTransition>> GetTransitionsAsync(string key, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions", ct);
        return doc.RootElement.GetProperty("transitions").EnumerateArray()
            .Select(t => new JiraTransition { Id = Str(t, "id"), Name = Str(t, "name"), ToStatus = Str(Prop(t, "to"), "name") })
            .ToList();
    }

    public async Task TransitionAsync(string key, string transitionId, CancellationToken ct)
    {
        var payload = new JsonObject { ["transition"] = new JsonObject { ["id"] = transitionId } };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, $"rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var _ = await SendJsonAsync(req, ct);
    }

    private Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct) =>
        SendJsonAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(_base, path)), ct);

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        {
            req.Headers.Authorization = _auth;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (HttpRequestException ex) { throw new ExternalServiceException("Jira", $"request failed: {ex.Message}"); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ExternalServiceException("Jira", "request timed out."); }

            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var hint = (int)resp.StatusCode switch
                    {
                        401 => " Check the Jira email and API token.",
                        403 => " The Jira account lacks permission for this operation.",
                        404 => " The issue does not exist or the Jira account cannot see it.",
                        _ => "",
                    };
                    throw new ExternalServiceException("Jira", $"HTTP {(int)resp.StatusCode} for {req.RequestUri!.AbsolutePath}.{hint} {Truncate(body, 300)}", (int)resp.StatusCode);
                }
                if (body.Length == 0) return JsonDocument.Parse("{}");
                if (body.TrimStart().StartsWith('<'))
                    throw new ExternalServiceException("Jira", "returned HTML instead of JSON — check the Jira base URL.");
                return JsonDocument.Parse(body);
            }
        }
    }

    internal static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    internal static string Str(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" :
        Prop(e, name) is { ValueKind: JsonValueKind.Number } num ? num.ToString() : "";

    private static List<string> Names(JsonElement arr) =>
        arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(x => Str(x, "name")).Where(x => x.Length > 0).ToList() : [];

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Best-effort text for arbitrary custom field shapes (option, user, ADF, arrays, scalars).</summary>
    internal static string FlattenValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(FlattenValue).Where(s => s.Length > 0)),
        JsonValueKind.Object when v.TryGetProperty("type", out var t) && t.GetString() == "doc" => Adf.ToText(v),
        JsonValueKind.Object => new[] { "value", "name", "displayName", "key" }
            .Select(p => Str(v, p)).FirstOrDefault(s => s.Length > 0) ?? "",
        _ => "",
    };
}

/// <summary>Atlassian Document Format ⇄ text helpers.</summary>
public static class Adf
{
    public static string ToText(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String) return node.GetString() ?? "";
        if (node.ValueKind != JsonValueKind.Object) return "";
        var sb = new StringBuilder();
        Walk(node, sb, 0);
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    private static void Walk(JsonElement node, StringBuilder sb, int listDepth)
    {
        var type = JiraClient.Str(node, "type");
        switch (type)
        {
            case "text": sb.Append(JiraClient.Str(node, "text")); return;
            case "hardBreak": sb.Append('\n'); return;
            case "mention": sb.Append(JiraClient.Str(JiraClient.Prop(node, "attrs"), "text")); return;
            case "inlineCard": sb.Append(JiraClient.Str(JiraClient.Prop(node, "attrs"), "url")); return;
            case "emoji": sb.Append(JiraClient.Str(JiraClient.Prop(node, "attrs"), "text")); return;
            case "media": sb.Append("[media]"); return;
        }

        if (type == "listItem") sb.Append(new string(' ', Math.Max(0, listDepth - 1) * 2)).Append("- ");
        if (type == "heading") sb.Append('\n');
        if (type == "codeBlock") sb.Append("```\n");

        if (JiraClient.Prop(node, "content") is { ValueKind: JsonValueKind.Array } content)
        {
            var nextDepth = type is "bulletList" or "orderedList" ? listDepth + 1 : listDepth;
            foreach (var child in content.EnumerateArray()) Walk(child, sb, nextDepth);
        }

        if (type == "codeBlock") sb.Append("\n```");
        if (type is "paragraph" or "heading" or "codeBlock" or "blockquote" or "tableRow" or "rule") sb.Append('\n');
        if (type is "tableCell" or "tableHeader") sb.Append(" | ");
    }
}
