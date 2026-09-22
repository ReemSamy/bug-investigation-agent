using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BugInvestigationAgent.Core.Configuration;

namespace BugInvestigationAgent.Infrastructure.Settings;

public sealed class JiraConnectionTester
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalSettingsService _local;

    public JiraConnectionTester(IHttpClientFactory httpClientFactory, LocalSettingsService local)
    {
        _httpClientFactory = httpClientFactory;
        _local = local;
    }

    public async Task<JiraConnectionTestResult> SignInAsync(string? sampleIssueKey = null, CancellationToken cancellationToken = default)
    {
        if (!_local.IsConfigured)
            return JiraConnectionTestResult.Fail("Enter your Atlassian site, email, and API token first, then click Sign in.");

        var options = _local.GetEffective();
        var baseUrl = JiraUrlNormalizer.NormalizeBaseUrl(options.BaseUrl);
        var client = _httpClientFactory.CreateClient("jira-test");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Email}:{options.ApiToken}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var myselfResp = await client.GetAsync("rest/api/3/myself", cancellationToken);
        var myselfBody = await myselfResp.Content.ReadAsStringAsync(cancellationToken);
        if (!myselfResp.IsSuccessStatusCode)
        {
            _local.ClearSignedInAccount();
            return JiraConnectionTestResult.Fail(
                $"Could not sign in (HTTP {(int)myselfResp.StatusCode}). " +
                "Use your Atlassian account email and an API token created at id.atlassian.com (Security → API tokens) for that same email — not your normal password.");
        }

        if (myselfBody.TrimStart().StartsWith('<'))
        {
            _local.ClearSignedInAccount();
            return JiraConnectionTestResult.Fail("Jira returned HTML for /myself — Base URL must be the site root (https://ehsq.atlassian.net).");
        }

        string? displayName;
        string? accountEmail;
        try
        {
            using var doc = JsonDocument.Parse(myselfBody);
            displayName = doc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            accountEmail = doc.RootElement.TryGetProperty("emailAddress", out var em) ? em.GetString() : options.Email;
        }
        catch
        {
            _local.ClearSignedInAccount();
            return JiraConnectionTestResult.Fail("Signed-in response was not valid JSON.");
        }

        var key = JiraUrlNormalizer.ExtractIssueKey(sampleIssueKey);
        string? summary = null;
        if (!string.IsNullOrWhiteSpace(key))
        {
            using var issueResp = await client.GetAsync($"rest/api/3/issue/{key}?fields=summary,key", cancellationToken);
            var issueBody = await issueResp.Content.ReadAsStringAsync(cancellationToken);
            if (!issueResp.IsSuccessStatusCode)
            {
                // Still treat myself success as signed in, but warn about issue access.
                _local.SetSignedInAccount(displayName, accountEmail);
                return JiraConnectionTestResult.Fail(
                    $"Signed in as {displayName} ({accountEmail}), but issue {key} returned HTTP {(int)issueResp.StatusCode}. " +
                    "Your account session works for login; this account cannot read that issue via API (permissions) or the key is wrong.");
            }

            try
            {
                using var doc = JsonDocument.Parse(issueBody);
                if (doc.RootElement.TryGetProperty("fields", out var fields) && fields.TryGetProperty("summary", out var s))
                    summary = s.GetString();
            }
            catch { /* ignore */ }
        }

        _local.SetSignedInAccount(displayName, accountEmail);
        return JiraConnectionTestResult.Ok(
            $"Signed in as {displayName} ({accountEmail})." +
            (string.IsNullOrWhiteSpace(key) ? "" : $" Verified access to {key}" + (string.IsNullOrWhiteSpace(summary) ? "." : $": {summary}")));
    }

    // Keep old name for any callers
    public Task<JiraConnectionTestResult> TestAsync(string? sampleIssueKey = null, CancellationToken cancellationToken = default) =>
        SignInAsync(sampleIssueKey, cancellationToken);
}

public sealed class JiraConnectionTestResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = "";

    public static JiraConnectionTestResult Ok(string message) => new() { Succeeded = true, Message = message };
    public static JiraConnectionTestResult Fail(string message) => new() { Succeeded = false, Message = message };
}
