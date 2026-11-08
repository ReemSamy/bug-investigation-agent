using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Jira;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Infrastructure.Forge;

public static class JiraCloudUrls
{
    /// <summary>Atlassian API gateway base for a Jira Cloud site (OAuth / Forge tokens). Prefers the FIT's apiBaseUrl.</summary>
    public static Uri ApiBase(string cloudId, string? apiBaseUrl)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(apiBaseUrl) &&
                      Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var u) &&
                      u.Scheme == Uri.UriSchemeHttps &&
                      u.Host.EndsWith("atlassian.com", StringComparison.OrdinalIgnoreCase)
            ? apiBaseUrl
            : $"https://api.atlassian.com/ex/jira/{Uri.EscapeDataString(cloudId)}";
        return new Uri(baseUrl.TrimEnd('/') + "/");
    }
}

/// <summary>
/// Hosted mode: Jira reads for a job use the INVOKING USER's Forge token (x-forge-oauth-user), so the agent can only
/// read issues, comments and attachments that user may see. Falls back to the configured account in the standalone UI.
/// </summary>
public sealed class HostedJiraAccessProvider : IJiraAccessProvider
{
    private readonly IJobSecretStore _secrets;
    private readonly ConfiguredJiraAccessProvider _standalone;

    public HostedJiraAccessProvider(IJobSecretStore secrets, ConfiguredJiraAccessProvider standalone)
    {
        _secrets = secrets;
        _standalone = standalone;
    }

    public async Task<JiraAccessResult> GetReadAccessAsync(RuntimeContext context, CancellationToken cancellationToken = default)
    {
        var job = HostedJobScope.Current;
        if (job is null)
            return await _standalone.GetReadAccessAsync(context, cancellationToken);

        var token = await _secrets.GetAsync(job.JobId, JobSecretKinds.JiraUserToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return JiraAccessResult.Fail(
                "authorization of the invoking user has expired. Reply in the agent task (or invoke the agent again) so Jira sends a fresh authorization.");
        }

        return JiraAccessResult.Ok(new JiraAccess
        {
            BaseAddress = JiraCloudUrls.ApiBase(job.CloudId, job.ApiBaseUrl),
            Authorization = new AuthenticationHeaderValue("Bearer", token),
            Description = "the invoking Jira user (delegated)",
            IsUserDelegated = true,
            BrowseBaseUrl = job.SiteUrl
        });
    }
}

/// <summary>Saved-credential owner = Atlassian user on this site while a hosted job runs; local identity otherwise.</summary>
public sealed class HostedCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly ICurrentUserAccessor _fallback;

    public HostedCurrentUserAccessor(ICurrentUserAccessor fallback) => _fallback = fallback;

    public string UserId => HostedJobScope.Current?.OwnerId ?? _fallback.UserId;
}

public sealed class JiraUser
{
    public required string AccountId { get; init; }
    public string? DisplayName { get; init; }
}

public sealed class JiraIssueRef
{
    public required string Id { get; init; }
    public required string Key { get; init; }
    public string? Summary { get; init; }
}

/// <summary>Jira Cloud REST calls with Forge-issued bearer tokens. Never logs tokens or response bodies containing them.</summary>
public sealed class JiraCloudClient
{
    public const string ReportPropertyKey = "bug-investigation-agent-report";
    private readonly IHttpClientFactory _http;
    private readonly ILogger<JiraCloudClient> _logger;

    public JiraCloudClient(IHttpClientFactory http, ILogger<JiraCloudClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    private HttpClient Client(Uri baseAddress, string bearer)
    {
        var client = _http.CreateClient("jira-cloud");
        client.BaseAddress = baseAddress;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Identifies the user behind a user token (authoritative accountId).</summary>
    public async Task<JiraUser?> GetMyselfAsync(Uri apiBase, string userToken, CancellationToken cancellationToken = default)
    {
        using var resp = await Client(apiBase, userToken).GetAsync("rest/api/3/myself", cancellationToken);
        if (!resp.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        var id = doc.RootElement.TryGetProperty("accountId", out var a) ? a.GetString() : null;
        return id is null ? null : new JiraUser
        {
            AccountId = id,
            DisplayName = doc.RootElement.TryGetProperty("displayName", out var d) ? d.GetString() : null
        };
    }

    /// <summary>Reads an issue WITH THE USER'S TOKEN. Null when the user cannot see it (403/404) — permission enforced by Jira.</summary>
    public async Task<(JiraIssueRef? Issue, HttpStatusCode Status)> GetIssueAsUserAsync(Uri apiBase, string userToken, string issueIdOrKey, CancellationToken cancellationToken = default)
    {
        using var resp = await Client(apiBase, userToken)
            .GetAsync($"rest/api/3/issue/{Uri.EscapeDataString(issueIdOrKey)}?fields=summary,project,components", cancellationToken);
        if (!resp.IsSuccessStatusCode)
            return (null, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        return (new JiraIssueRef
        {
            Id = root.GetProperty("id").GetString()!,
            Key = root.GetProperty("key").GetString()!,
            Summary = root.TryGetProperty("fields", out var f) && f.TryGetProperty("summary", out var s) ? s.GetString() : null
        }, resp.StatusCode);
    }

    /// <summary>Finds a report comment previously published for this investigation (idempotency across retries).</summary>
    public async Task<string?> FindReportCommentAsync(Uri apiBase, string token, string issueId, Guid investigationId, CancellationToken cancellationToken = default)
    {
        var client = Client(apiBase, token);
        for (var startAt = 0; startAt < 1000; startAt += 100)
        {
            using var resp = await client.GetAsync(
                $"rest/api/3/issue/{Uri.EscapeDataString(issueId)}/comment?startAt={startAt}&maxResults=100&expand=properties", cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("comments", out var comments) || comments.GetArrayLength() == 0)
                return null;
            foreach (var c in comments.EnumerateArray())
            {
                if (!c.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var p in props.EnumerateArray())
                {
                    if (p.TryGetProperty("key", out var k) && k.GetString() == ReportPropertyKey &&
                        p.TryGetProperty("value", out var v) && v.TryGetProperty("investigationId", out var inv) &&
                        string.Equals(inv.GetString(), investigationId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                        return c.GetProperty("id").GetString();
                }
            }
            if (comments.GetArrayLength() < 100)
                return null;
        }
        return null;
    }

    public async Task<string?> AddReportCommentAsync(Uri apiBase, string token, string issueId, Guid investigationId, JsonObject adfBody, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["body"] = adfBody,
            ["properties"] = new JsonArray
            {
                new JsonObject
                {
                    ["key"] = ReportPropertyKey,
                    ["value"] = new JsonObject { ["investigationId"] = investigationId.ToString("N") }
                }
            }
        };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Client(apiBase, token).PostAsync($"rest/api/3/issue/{Uri.EscapeDataString(issueId)}/comment", content, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Publishing the investigation comment failed with HTTP {Status}.", (int)resp.StatusCode);
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.GetProperty("id").GetString();
    }

    public async Task<HashSet<string>> ListAttachmentNamesAsync(Uri apiBase, string token, string issueId, CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var resp = await Client(apiBase, token).GetAsync($"rest/api/3/issue/{Uri.EscapeDataString(issueId)}?fields=attachment", cancellationToken);
        if (!resp.IsSuccessStatusCode)
            return names;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("fields", out var f) && f.TryGetProperty("attachment", out var atts) && atts.ValueKind == JsonValueKind.Array)
            foreach (var a in atts.EnumerateArray())
                if (a.TryGetProperty("filename", out var n) && n.GetString() is { } name)
                    names.Add(name);
        return names;
    }

    public async Task<bool> AddAttachmentAsync(Uri apiBase, string token, string issueId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"rest/api/3/issue/{Uri.EscapeDataString(issueId)}/attachments") { Content = form };
        request.Headers.Add("X-Atlassian-Token", "no-check");
        using var resp = await Client(apiBase, token).SendAsync(request, cancellationToken);
        return resp.IsSuccessStatusCode;
    }
}

public sealed class ReportPublishingOptions
{
    /// <summary>Attach (masked) screenshots to the Jira issue. Never for Production environments.</summary>
    public bool AttachScreenshots { get; set; } = true;
    public int MaxAttachments { get; set; } = 8;
}

/// <summary>
/// Publishes the structured report to the original Jira issue as the app (system token — an explicitly authorized,
/// app-owned operation). Idempotent: an existing comment carrying this investigation's property is reused, and
/// attachments already present are not uploaded again.
/// </summary>
public sealed class JiraReportPublisher
{
    private readonly IInvestigationJobStore _jobs;
    private readonly IJobSecretStore _secrets;
    private readonly IEvidenceStore _evidence;
    private readonly IAuditLog _audit;
    private readonly JiraCloudClient _jira;
    private readonly ReportPublishingOptions _options;
    private readonly ILogger<JiraReportPublisher> _logger;

    public JiraReportPublisher(
        IInvestigationJobStore jobs, IJobSecretStore secrets, IEvidenceStore evidence, IAuditLog audit,
        JiraCloudClient jira, IOptions<ReportPublishingOptions> options, ILogger<JiraReportPublisher> logger)
    {
        _jobs = jobs;
        _secrets = secrets;
        _evidence = evidence;
        _audit = audit;
        _jira = jira;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Returns true when the report is (now or already) published.</summary>
    public async Task<bool> PublishAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetAsync(jobId, cancellationToken);
        if (job is null || job.ReportJson is null)
            return false;
        if (job.ReportPublishedAt is not null)
            return true;

        var report = InvestigationReport.FromJson(job.ReportJson);
        if (report is null)
            return false;

        var systemToken = await _secrets.GetAsync(job.Id, JobSecretKinds.JiraSystemToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(systemToken))
        {
            _logger.LogInformation("Report for job {JobId} waits for a fresh Jira app token (next Jira poll or reply).", job.Id);
            return false;
        }

        var apiBase = JiraCloudUrls.ApiBase(job.CloudId, job.ApiBaseUrl);

        // 1) Idempotency: a retry after a crash must not post a second comment.
        var commentId = job.ReportCommentId
                        ?? await _jira.FindReportCommentAsync(apiBase, systemToken, job.IssueId, job.Id, cancellationToken);

        var attached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (commentId is null && _options.AttachScreenshots && !report.ReadOnlyEnvironment)
        {
            var existing = await _jira.ListAttachmentNamesAsync(apiBase, systemToken, job.IssueId, cancellationToken);
            foreach (var ev in report.Evidence.Where(e => e.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Take(_options.MaxAttachments))
            {
                var name = $"bia-{job.Id.ToString("N")[..8]}-{ev.FileName}";
                if (existing.Contains(name))
                {
                    attached[ev.FileName] = name;
                    continue;
                }
                await using var stream = await _evidence.OpenReadAsync(job.Id, ev.FileName, cancellationToken);
                if (stream is not null && await _jira.AddAttachmentAsync(apiBase, systemToken, job.IssueId, name, stream, cancellationToken))
                    attached[ev.FileName] = name;
            }
        }

        commentId ??= await _jira.AddReportCommentAsync(apiBase, systemToken, job.IssueId, job.Id,
            InvestigationReportBuilder.ToAdf(report, attached), cancellationToken);
        if (commentId is null)
            return false;

        await _jobs.UpdateAsync(job.Id, j =>
        {
            j.ReportCommentId = commentId;
            j.ReportPublishedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        await _audit.WriteAsync(new AuditEntry
        {
            TenantId = job.TenantId, AccountId = job.InvokingAccountId, JobId = job.Id,
            Action = AuditActions.ReportPublished,
            Detail = $"Report comment {commentId} on {job.IssueKey}; {attached.Count} screenshot(s) attached."
        }, cancellationToken);
        return true;
    }
}
