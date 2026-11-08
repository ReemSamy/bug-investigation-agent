using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Skills;
using Microsoft.Extensions.Logging;

namespace BugInvestigationAgent.Skills.Jira;

public sealed class JiraSkill : ISkill
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IJiraConnectionSettings _jira;
    private readonly AgentOptions _agentOptions;
    private readonly ILogger<JiraSkill> _logger;

    public JiraSkill(
        IHttpClientFactory httpClientFactory,
        IJiraConnectionSettings jira,
        Microsoft.Extensions.Options.IOptions<AgentOptions> agentOptions,
        ILogger<JiraSkill> logger)
    {
        _httpClientFactory = httpClientFactory;
        _jira = jira;
        _agentOptions = agentOptions.Value;
        _logger = logger;
    }

    public string SkillId => SkillIds.Jira;
    public string DisplayName => "Jira";
    public AgentPermission RequiredPermissions => AgentPermission.ReadJira;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var key = request.Context.JiraTicketKey;
        if (string.IsNullOrWhiteSpace(key))
            return SkillResult.Fail("Jira ticket key is required.");

        request.Progress?.Report($"Loading issue {key} from Jira.");

        if (!_jira.IsConfigured)
        {
            request.Context.JiraDetailsLoaded = false;
            return SkillResult.Fail(
                "Jira is not configured. Open Settings once, enter Base URL / Email / API token, then analyze the ticket again.");
        }

        var options = _jira.GetEffective();
        var baseUrl = JiraUrlNormalizer.NormalizeBaseUrl(options.BaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return SkillResult.Fail("Jira Base URL is empty. Use only the site root, e.g. https://ehsq.atlassian.net.");

        try
        {
            var client = _httpClientFactory.CreateClient("jira");
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Email}:{options.ApiToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Prove auth first — clearer than a bare issue 404.
            request.Progress?.Report($"GET {baseUrl}/rest/api/3/myself");
            using (var myself = await client.GetAsync("rest/api/3/myself", cancellationToken))
            {
                var myselfBody = await myself.Content.ReadAsStringAsync(cancellationToken);
                if (!myself.IsSuccessStatusCode)
                {
                    request.Context.JiraDetailsLoaded = false;
                    return SkillResult.Fail(
                        $"Jira login failed (HTTP {(int)myself.StatusCode}) for email '{options.Email}'. " +
                        "Create a new API token at id.atlassian.com for that exact email (not your Visium app password), paste it in Settings, Save, then Analyze again.");
                }
                if (myselfBody.TrimStart().StartsWith('<'))
                {
                    request.Context.JiraDetailsLoaded = false;
                    return SkillResult.Fail($"Jira returned HTML for /myself. Base URL must be {baseUrl} site root only.");
                }
            }

            request.Progress?.Report($"GET {baseUrl}/rest/api/3/issue/{key}");

            string json;
            using (var resp = await client.GetAsync($"rest/api/3/issue/{key}?expand=renderedFields,names", cancellationToken))
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    json = EnsureJson(body, baseUrl, (int)resp.StatusCode);
                }
                else
                {
                    using var resp2 = await client.GetAsync($"rest/api/2/issue/{key}?expand=renderedFields,names", cancellationToken);
                    var body2 = await resp2.Content.ReadAsStringAsync(cancellationToken);
                    if (!resp2.IsSuccessStatusCode)
                    {
                        request.Context.JiraDetailsLoaded = false;
                        var code = (int)resp2.StatusCode;
                        var hint = code == 404
                            ? $" Authenticated OK, but issue {key} returned 404. Your API user can log in but cannot see this issue (project permission), or the key is wrong. Browser access uses a cookie session — it is not the same as this API token user."
                            : code is 401 or 403
                                ? " Check email + API token."
                                : body2.TrimStart().StartsWith('<')
                                    ? " HTML response usually means wrong Base URL."
                                    : "";
                        return SkillResult.Fail($"Jira returned {code} for issue {key}.{hint}");
                    }
                    json = EnsureJson(body2, baseUrl, (int)resp2.StatusCode);
                }
            }

            ParseIssue(request.Context, json);
            request.Context.JiraDetailsLoaded = true;
            request.Progress?.Report(
                $"Loaded {key}: envUrl={(request.Context.EnvironmentUrl ?? "pending discovery")}, fixVersion={request.Context.FixVersion ?? "?"}, steps={request.Context.ReproductionSteps.Count}, attachments={request.Context.Attachments.Count}.");

            await DownloadImageAttachmentsAsync(client, request.Context, request.Progress, cancellationToken);

            try
            {
                using var commentsResp = await client.GetAsync($"rest/api/3/issue/{key}/comment", cancellationToken);
                if (commentsResp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await commentsResp.Content.ReadAsStringAsync(cancellationToken));
                    if (doc.RootElement.TryGetProperty("comments", out var comments))
                    {
                        foreach (var c in comments.EnumerateArray())
                        {
                            var body = ExtractText(c.TryGetProperty("body", out var b) ? b : default);
                            if (!string.IsNullOrWhiteSpace(body))
                                request.Context.Comments.Add(body!);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load comments for {Key}", key);
            }

            CaptureFixPullRequestReference(request.Context, request.Progress);

            return SkillResult.Ok($"Loaded Jira issue {key}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jira fetch failed");
            request.Context.JiraDetailsLoaded = false;
            return SkillResult.Fail($"Failed to fetch Jira issue: {ex.Message}");
        }
    }

    private static string EnsureJson(string body, string baseUrl, int statusCode)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('<') || trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Jira returned HTML instead of JSON (HTTP {statusCode}). " +
                $"Set Base URL to the site root only (e.g. https://ehsq.atlassian.net), not a board/browse page. Current effective base: {baseUrl}");
        }

        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            throw new InvalidOperationException(
                $"Jira returned a non-JSON body (HTTP {statusCode}). Check Base URL ({baseUrl}), email, and API token.");
        }

        return body;
    }

    private static void ParseIssue(RuntimeContext context, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        context.JiraTicketKey = root.TryGetProperty("key", out var k) ? k.GetString() : context.JiraTicketKey;
        if (!root.TryGetProperty("fields", out var fields))
            return;

        context.Summary = GetString(fields, "summary") ?? context.Summary;
        context.Description = ExtractText(fields.TryGetProperty("description", out var d) ? d : default) ?? context.Description;

        if (fields.TryGetProperty("fixVersions", out var versions) && versions.ValueKind == JsonValueKind.Array)
        {
            var names = versions.EnumerateArray().Select(v => GetString(v, "name")).Where(n => !string.IsNullOrWhiteSpace(n));
            context.FixVersion = string.Join(", ", names!);
        }

        if (fields.TryGetProperty("versions", out var affects) && affects.ValueKind == JsonValueKind.Array)
        {
            var names = affects.EnumerateArray().Select(v => GetString(v, "name")).Where(n => !string.IsNullOrWhiteSpace(n));
            var joined = string.Join(", ", names!);
            if (!string.IsNullOrWhiteSpace(joined))
                context.CustomFields["affectsVersions"] = joined;
        }

        if (fields.TryGetProperty("environment", out var env))
        {
            var envText = ExtractText(env);
            if (!string.IsNullOrWhiteSpace(envText))
            {
                context.CustomFields["environment"] = envText!;
                context.EnvironmentUrl ??= EnvironmentUrlParser.FindFirstUrl(envText);
                context.EnvironmentName ??= envText.Split('\n')[0].Trim();
            }
        }

        if (fields.TryGetProperty("attachment", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attachments.EnumerateArray())
            {
                context.Attachments.Add(new AttachmentInfo
                {
                    Id = GetString(a, "id") ?? Guid.NewGuid().ToString("N"),
                    Filename = GetString(a, "filename") ?? "attachment",
                    MimeType = GetString(a, "mimeType"),
                    Url = GetString(a, "content")
                });
            }
        }

        foreach (var prop in fields.EnumerateObject())
        {
            var name = prop.Name;
            if (name.StartsWith("customfield_", StringComparison.OrdinalIgnoreCase) ||
                name is "environment" or "components" or "labels" or "priority" or "versions")
            {
                var text = ExtractText(prop.Value);
                if (!string.IsNullOrWhiteSpace(text))
                    context.CustomFields[name] = text!;
            }
        }

        // Pull URL/user hints early from description + environment before EnvironmentDiscovery runs.
        var envField = context.CustomFields.TryGetValue("environment", out var ev) ? ev : null;
        var corpus = $"{context.Description}\n{envField}";
        context.EnvironmentUrl ??= EnvironmentUrlParser.FindFirstUrl(corpus);
        context.ReproductionSteps = ExtractSteps(context.Description);
        if (context.ReproductionSteps.Count > 0)
            context.CustomFields["stepsFromJira"] = "true";
        else
            context.CustomFields.Remove("stepsFromJira");
    }

    public static List<string> ExtractSteps(string? description)
    {
        var steps = new List<string>();
        if (string.IsNullOrWhiteSpace(description))
            return steps;

        var lines = description.Replace("\r\n", "\n").Split('\n');
        var inSteps = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (Regex.IsMatch(line, @"^(repro(duction)?\s*steps|steps\s*to\s*reproduce|how\s*to\s*reproduce|steps?\s*:)\b", RegexOptions.IgnoreCase))
            {
                inSteps = true;
                continue;
            }
            if (inSteps && Regex.IsMatch(line, @"^(expected|actual|environment|notes|additional|workaround)\b", RegexOptions.IgnoreCase))
                break;
            if (Regex.IsMatch(line, @"^(\d+[\.\)]\s+|[-*]\s+)"))
                steps.Add(Regex.Replace(line, @"^(\d+[\.\)]\s+|[-*]\s+)", ""));
            else if (inSteps && !string.IsNullOrWhiteSpace(line))
                steps.Add(line);
        }

        if (steps.Count == 0)
        {
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (Regex.IsMatch(line, @"^(\d+[\.\)]\s+)"))
                    steps.Add(Regex.Replace(line, @"^(\d+[\.\)]\s+)", ""));
            }
        }

        // If still empty, harvest explicit navigate/go-to lines from the description body.
        if (steps.Count == 0)
        {
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (Regex.IsMatch(line, @"(?i)\b(?:go to|navigate to|open)\s+(?:Modules|Administration|Admin)\b") ||
                    Regex.IsMatch(line, @"(?i)\b(?:Modules|Administration)\s*[>→/»]"))
                    steps.Add(line);
            }
        }

        return steps;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) ? p.GetString() : null;

    private static string? ExtractText(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Undefined => null,
            JsonValueKind.Null => null,
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => string.Join(", ", el.EnumerateArray().Select(ExtractText).Where(s => !string.IsNullOrWhiteSpace(s))),
            JsonValueKind.Object when el.TryGetProperty("content", out var content) => ExtractText(content),
            JsonValueKind.Object when el.TryGetProperty("text", out var text) => text.GetString(),
            JsonValueKind.Object when el.TryGetProperty("name", out var name) => name.GetString(),
            JsonValueKind.Object when el.TryGetProperty("value", out var value) => ExtractText(value),
            JsonValueKind.Object => el.ToString(),
            _ => el.ToString()
        };
    }

    private async Task DownloadImageAttachmentsAsync(
        HttpClient client,
        RuntimeContext context,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var images = context.Attachments
            .Where(a =>
                !string.IsNullOrWhiteSpace(a.Url) &&
                (a.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
                 Regex.IsMatch(a.Filename ?? "", @"\.(png|jpe?g|gif|webp|bmp)$", RegexOptions.IgnoreCase)))
            .Take(8)
            .ToList();
        if (images.Count == 0)
            return;

        var root = Path.IsPathRooted(_agentOptions.EvidenceDirectory)
            ? _agentOptions.EvidenceDirectory
            : Path.Combine(Directory.GetCurrentDirectory(), _agentOptions.EvidenceDirectory);
        var dir = Path.Combine(root, context.SessionId.ToString("N"), "jira-attachments");
        Directory.CreateDirectory(dir);

        var downloaded = 0;
        foreach (var att in images)
        {
            try
            {
                using var resp = await client.GetAsync(att.Url, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var safeName = string.Join("_", (att.Filename ?? "attachment.png").Split(Path.GetInvalidFileNameChars()));
                var local = Path.Combine(dir, $"{att.Id}_{safeName}");
                await File.WriteAllBytesAsync(local, await resp.Content.ReadAsByteArrayAsync(cancellationToken), cancellationToken);
                att.LocalPath = local;
                context.Evidence.Add(new EvidenceItem
                {
                    Kind = "jira-attachment",
                    Path = local,
                    Caption = $"Jira attachment reference: {att.Filename} — reproduce this UI in the environment"
                });
                downloaded++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to download Jira attachment {File}", att.Filename);
            }
        }

        if (downloaded > 0)
        {
            progress?.Report($"Downloaded {downloaded} Jira screenshot attachment(s) as reproduction references.");
            context.CustomFields["jiraAttachmentImages"] = downloaded.ToString();
        }
    }

    /// <summary>
    /// If the ticket/comments link an Azure DevOps or GitHub PR, record it as a fix reference —
    /// never as the portal EnvironmentUrl.
    /// </summary>
    internal static void CaptureFixPullRequestReference(RuntimeContext context, IProgress<string>? progress = null)
    {
        var corpus = string.Join('\n',
            new[] { context.Description, context.Summary }
                .Concat(context.Comments)
                .Concat(context.CustomFields.Values)
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        var pr = EnvironmentUrlParser.FindFixPullRequestUrl(corpus);
        if (string.IsNullOrWhiteSpace(pr))
            return;

        context.FixPullRequestUrl = pr;
        context.CustomFields["fixPullRequestUrl"] = pr;
        progress?.Report(
            $"Ticket references a fix PR (not the portal): {pr}. " +
            "Ask for the environment URL where that fix may already be deployed, then try to reproduce there.");
    }
}
