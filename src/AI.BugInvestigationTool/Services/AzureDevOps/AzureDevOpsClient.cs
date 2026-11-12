using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services.Jira;

namespace AI.BugInvestigationTool.Services.AzureDevOps;

/// <summary>
/// Azure DevOps REST API (read-only for code; the only write is a PR comment thread on explicit approval).
/// Targeted calls only: code search + single-file retrieval, never full repository downloads.
/// </summary>
public sealed class AzureDevOpsClient
{
    private const string Api = "api-version=7.1";
    private readonly HttpClient _http;
    private readonly string _org;
    private readonly string _project;
    private readonly AuthenticationHeaderValue _auth;
    private readonly Uri _devBase;
    private readonly Uri _searchBase;

    public AzureDevOpsClient(HttpClient http, string organization, string project, string pat,
        string devBase = "https://dev.azure.com/", string searchBase = "https://almsearch.dev.azure.com/")
    {
        _http = http;
        _org = organization;
        _project = project;
        _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat)));
        _devBase = new Uri(devBase);
        _searchBase = new Uri(searchBase);
    }

    public string Project => _project;

    private string P(string path) => $"{Uri.EscapeDataString(_org)}/{Uri.EscapeDataString(_project)}/_apis/{path}";
    private static string R(string repo) => Uri.EscapeDataString(repo);

    public async Task<List<RepositoryInfo>> ListRepositoriesAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, new Uri(_devBase, $"{P("git/repositories")}?{Api}"), null, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray()
            .Where(r => !(r.TryGetProperty("isDisabled", out var d) && d.ValueKind == JsonValueKind.True))
            .Select(r => new RepositoryInfo
            {
                Id = JiraClient.Str(r, "id"),
                Name = JiraClient.Str(r, "name"),
                DefaultBranch = JiraClient.Str(r, "defaultBranch").Replace("refs/heads/", ""),
            }).ToList();
    }

    public async Task<List<CodeSearchHit>> SearchCodeAsync(string text, string? repository, int top, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["searchText"] = text,
            ["$top"] = Math.Clamp(top, 1, 50),
            ["$skip"] = 0,
            ["includeFacets"] = false,
        };
        if (!string.IsNullOrWhiteSpace(repository))
            body["filters"] = new JsonObject { ["Repository"] = new JsonArray(repository) };

        using var doc = await SendAsync(HttpMethod.Post, new Uri(_searchBase, $"{P("search/codesearchresults")}?{Api}"), body, ct);
        return doc.RootElement.TryGetProperty("results", out var results)
            ? results.EnumerateArray().Select(r => new CodeSearchHit
            {
                Repository = JiraClient.Str(JiraClient.Prop(r, "repository"), "name"),
                Path = JiraClient.Str(r, "path"),
                FileName = JiraClient.Str(r, "fileName"),
            }).ToList()
            : [];
    }

    /// <summary>Gets file text at a branch or commit. Returns null when the file does not exist.</summary>
    public async Task<string?> GetFileAsync(string repository, string path, string? version, bool versionIsCommit, CancellationToken ct)
    {
        var url = $"{P($"git/repositories/{R(repository)}/items")}?path={Uri.EscapeDataString(path)}&includeContent=true&{Api}";
        if (!string.IsNullOrWhiteSpace(version))
            url += $"&versionDescriptor.version={Uri.EscapeDataString(version)}&versionDescriptor.versionType={(versionIsCommit ? "commit" : "branch")}";
        try
        {
            using var doc = await SendAsync(HttpMethod.Get, new Uri(_devBase, url), null, ct);
            return doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
        }
        catch (ExternalServiceException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<List<string>> GetCommitsAsync(string repository, string? path, int top, CancellationToken ct)
    {
        var url = $"{P($"git/repositories/{R(repository)}/commits")}?$top={Math.Clamp(top, 1, 30)}&{Api}";
        if (!string.IsNullOrWhiteSpace(path)) url += $"&searchCriteria.itemPath={Uri.EscapeDataString(path)}";
        using var doc = await SendAsync(HttpMethod.Get, new Uri(_devBase, url), null, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(c =>
            $"{JiraClient.Str(c, "commitId")[..Math.Min(10, JiraClient.Str(c, "commitId").Length)]} {JiraClient.Str(JiraClient.Prop(c, "author"), "date")[..Math.Min(10, JiraClient.Str(JiraClient.Prop(c, "author"), "date").Length)]} {JiraClient.Str(JiraClient.Prop(c, "author"), "name")}: {JiraClient.Str(c, "comment").Split('\n')[0]}").ToList();
    }

    /// <summary>Loads PR metadata, changed files with computed diffs, and linked work items.</summary>
    public async Task<PullRequestData> GetPullRequestAsync(string repository, int id, int maxFiles, int maxDiffLines, CancellationToken ct)
    {
        using var prDoc = await SendAsync(HttpMethod.Get, new Uri(_devBase, $"{P($"git/repositories/{R(repository)}/pullrequests/{id}")}?{Api}"), null, ct);
        var pr = prDoc.RootElement;
        var data = new PullRequestData
        {
            Id = id,
            Title = JiraClient.Str(pr, "title"),
            Description = JiraClient.Str(pr, "description"),
            Author = JiraClient.Str(JiraClient.Prop(pr, "createdBy"), "displayName"),
            Status = JiraClient.Str(pr, "status"),
            SourceBranch = JiraClient.Str(pr, "sourceRefName").Replace("refs/heads/", ""),
            TargetBranch = JiraClient.Str(pr, "targetRefName").Replace("refs/heads/", ""),
            SourceCommit = JiraClient.Str(JiraClient.Prop(pr, "lastMergeSourceCommit"), "commitId"),
            BaseCommit = JiraClient.Str(JiraClient.Prop(pr, "lastMergeTargetCommit"), "commitId"),
            RepositoryId = JiraClient.Str(JiraClient.Prop(pr, "repository"), "id"),
        };

        // Latest iteration vs. the common ancestor = the "Files" tab of the PR.
        using var itDoc = await SendAsync(HttpMethod.Get, new Uri(_devBase, $"{P($"git/repositories/{R(repository)}/pullrequests/{id}/iterations")}?{Api}"), null, ct);
        var iterations = itDoc.RootElement.GetProperty("value").EnumerateArray().ToList();
        if (iterations.Count > 0)
        {
            var last = iterations[^1];
            var lastId = last.GetProperty("id").GetInt32();
            var common = JiraClient.Str(JiraClient.Prop(last, "commonRefCommit"), "commitId");
            var source = JiraClient.Str(JiraClient.Prop(last, "sourceRefCommit"), "commitId");
            if (common.Length > 0) data.BaseCommit = common;
            if (source.Length > 0) data.SourceCommit = source;

            using var chDoc = await SendAsync(HttpMethod.Get, new Uri(_devBase,
                $"{P($"git/repositories/{R(repository)}/pullrequests/{id}/iterations/{lastId}/changes")}?$top=2000&$compareTo=0&{Api}"), null, ct);
            var entries = chDoc.RootElement.TryGetProperty("changeEntries", out var ce) ? ce.EnumerateArray().ToList() : [];

            foreach (var entry in entries)
            {
                var item = JiraClient.Prop(entry, "item");
                if (JiraClient.Str(item, "gitObjectType") is "tree") continue;
                var path = JiraClient.Str(item, "path");
                if (path.Length == 0) continue;
                var changeType = JiraClient.Str(entry, "changeType");
                var file = new ChangedFile { Path = path, ChangeType = changeType };
                data.Files.Add(file);

                if (data.Files.Count > maxFiles) { file.SkippedReason = $"over the {maxFiles}-file limit"; continue; }
                if (DiffBuilder.IsLikelyBinary(path)) { file.SkippedReason = "binary file"; continue; }

                var isDelete = changeType.Contains("delete", StringComparison.OrdinalIgnoreCase);
                var isAdd = changeType.Contains("add", StringComparison.OrdinalIgnoreCase);
                var originalPath = JiraClient.Str(entry, "originalPath") is { Length: > 0 } op ? op : path;
                var oldText = isAdd ? "" : await GetFileAsync(repository, originalPath, data.BaseCommit, true, ct) ?? "";
                var newText = isDelete ? "" : await GetFileAsync(repository, path, data.SourceCommit, true, ct) ?? "";
                var diff = DiffBuilder.Build(path, oldText, newText, maxDiffLines);
                file.Diff = diff.Text;
                file.Additions = diff.Additions;
                file.Deletions = diff.Deletions;
                file.ChangedRanges = diff.ChangedRanges;
                file.SkippedReason = diff.SkippedReason;
            }
        }

        try
        {
            using var wiDoc = await SendAsync(HttpMethod.Get, new Uri(_devBase, $"{P($"git/repositories/{R(repository)}/pullrequests/{id}/workitems")}?{Api}"), null, ct);
            data.LinkedWorkItems = wiDoc.RootElement.GetProperty("value").EnumerateArray().Select(w => JiraClient.Str(w, "id")).ToList();
        }
        catch (ExternalServiceException) { /* work item links are optional */ }

        data.LinkedJiraKeys = UrlParser.FindJiraKeys(data.Title, data.SourceBranch, data.Description);
        return data;
    }

    /// <summary>Posts one general comment thread. Only called after explicit user approval.</summary>
    public async Task<int> PostPullRequestCommentAsync(string repository, int id, string markdown, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["comments"] = new JsonArray(new JsonObject { ["parentCommentId"] = 0, ["content"] = markdown, ["commentType"] = 1 }),
            ["status"] = 1,
        };
        using var doc = await SendAsync(HttpMethod.Post, new Uri(_devBase, $"{P($"git/repositories/{R(repository)}/pullrequests/{id}/threads")}?{Api}"), body, ct);
        return doc.RootElement.TryGetProperty("id", out var tid) ? tid.GetInt32() : 0;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, Uri url, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = _auth;
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new ExternalServiceException("Azure DevOps", $"request failed: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ExternalServiceException("Azure DevOps", "request timed out."); }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            // ADO answers an invalid PAT with a 203 + HTML sign-in page.
            if ((int)resp.StatusCode == 203 || text.TrimStart().StartsWith('<'))
                throw new ExternalServiceException("Azure DevOps", "authentication failed — check the PAT (needs Code: Read).", 401);
            if (!resp.IsSuccessStatusCode)
            {
                var hint = (int)resp.StatusCode switch
                {
                    401 => " Check the PAT.",
                    403 => " The PAT lacks the required scope (Code: Read; Code: Read & Write only for PR comments).",
                    404 => " Not found — check organization, project, repository and ID.",
                    _ => "",
                };
                throw new ExternalServiceException("Azure DevOps", $"HTTP {(int)resp.StatusCode} for {url.AbsolutePath}.{hint} {JiraClient.Truncate(text, 300)}", (int)resp.StatusCode);
            }
            return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        }
    }
}
