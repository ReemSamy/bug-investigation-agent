using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Skills.Jira;

namespace BugInvestigationAgent.Skills.Environment;

public sealed class EnvironmentDiscoverySkill : ISkill
{
    private readonly IEnvironmentSelectionService? _environments;

    public EnvironmentDiscoverySkill(IEnvironmentSelectionService? environments = null) => _environments = environments;

    public string SkillId => SkillIds.EnvironmentDiscovery;
    public string DisplayName => "Environment Discovery";
    public AgentPermission RequiredPermissions => AgentPermission.ReadJira;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) => true;

    public Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        var corpus = BuildCorpus(ctx);
        request.Progress?.Report("Extracting environment, user/role, URLs, and platform from ticket text.");

        ctx.EnvironmentUrl = EnvironmentUrlParser.NormalizeUrl(ctx.EnvironmentUrl) ?? EnvironmentUrlParser.FindFirstUrl(corpus);
        ctx.PlatformOs ??= FindNamed(corpus, @"(?i)\b(windows(?:\s*\d+)?|linux|macos|mac\s*os|ios|android|chrome|edge|firefox)\b");
        ctx.Username ??= FindUsername(corpus);
        ctx.Role ??= FindRole(corpus);
        ctx.FixVersion ??= FindNamed(corpus, @"(?i)\bfix\s*versions?\s*[:=]?\s*([0-9][^\n,;]*)");
        ctx.EnvironmentName ??= FindEnvironmentName(corpus);

        var system = FindSystemName(corpus, ctx);
        if (!string.IsNullOrWhiteSpace(system))
            ctx.SystemName = system;
        else if (LooksLikeIssueKey(ctx.SystemName) || string.IsNullOrWhiteSpace(ctx.SystemName))
            ctx.SystemName = null;

        if (ctx.CustomFields.TryGetValue("environment", out var envField))
        {
            if (string.IsNullOrWhiteSpace(ctx.EnvironmentName))
                ctx.EnvironmentName = envField.Split('\n').FirstOrDefault()?.Trim();
            ctx.EnvironmentUrl ??= EnvironmentUrlParser.FindFirstUrl(envField);
        }

        if (string.IsNullOrWhiteSpace(ctx.EnvironmentUrl))
        {
            foreach (var value in ctx.CustomFields.Values)
            {
                var url = EnvironmentUrlParser.FindFirstUrl(value);
                if (url is not null)
                {
                    ctx.EnvironmentUrl = url;
                    break;
                }
            }
        }

        ctx.EnvironmentUrl = EnvironmentUrlParser.NormalizeUrl(ctx.EnvironmentUrl);
        if (!EnvironmentUrlParser.IsLikelyPortalEnvironmentUrl(ctx.EnvironmentUrl))
            ctx.EnvironmentUrl = null;

        // Don't keep tracker hosts as the "environment" label.
        if (string.Equals(ctx.EnvironmentName, "dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ctx.EnvironmentName, "visualstudio.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ctx.EnvironmentName, "atlassian.net", StringComparison.OrdinalIgnoreCase))
            ctx.EnvironmentName = null;

        if (ctx.ReproductionSteps.Count == 0)
            ctx.ReproductionSteps = Jira.JiraSkill.ExtractSteps(ctx.Description);

        request.Progress?.Report(
            $"Extracted system={ctx.SystemName ?? "?"}, env={ctx.EnvironmentName ?? "?"}, user={ctx.Username ?? ctx.Role ?? "?"}, url={(ctx.EnvironmentUrl is null ? "missing" : "found")}.");

        // Also surface fix PRs found only in comments/custom fields after Jira load.
        if (string.IsNullOrWhiteSpace(ctx.FixPullRequestUrl))
            JiraSkill.CaptureFixPullRequestReference(ctx, request.Progress);

        // Match the ticket against the environment registry. This is only a suggestion — the user confirms or changes it.
        if (_environments is not null)
        {
            var suggestion = _environments.ApplySuggestion(ctx);
            request.Progress?.Report(suggestion.EnvironmentId is null
                ? $"Environment not identified from the ticket: {suggestion.Reason}"
                : $"Suggested environment: {suggestion.EnvironmentId} — {suggestion.Reason}");
        }

        return Task.FromResult(SkillResult.Ok("Environment discovery complete."));
    }

    private static string BuildCorpus(RuntimeContext ctx)
    {
        var parts = new List<string?>
        {
            ctx.Summary,
            ctx.Description,
            ctx.FixVersion,
            string.Join('\n', ctx.Comments),
            string.Join('\n', ctx.CustomFields.Select(kv => $"{kv.Key}: {kv.Value}")),
            string.Join('\n', ctx.ReproductionSteps)
        };
        return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string? FindNamed(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        if (!m.Success)
            return null;
        return m.Groups.Count > 1 ? m.Groups[1].Value.Trim() : m.Value.Trim();
    }

    private static string? FindUsername(string text)
    {
        var patterns = new[]
        {
            @"(?i)\b(?:user(?:name)?|login|account)\s*[:=]\s*([A-Za-z0-9._\-]+)",
            @"(?i)\busing\s+([A-Za-z0-9._\-]+)\b",
            @"(?i)\bas\s+([A-Za-z0-9._\-]+)\b",
            @"(?i)\b([A-Za-z0-9._\-]*(?:admin|coordinator|fireman)[A-Za-z0-9._\-]*)\b"
        };
        foreach (var p in patterns)
        {
            var m = Regex.Match(text, p);
            if (m.Success)
            {
                var value = m.Groups[1].Value;
                if (value.Length >= 3 && !IsCommonWord(value) && !LooksLikeIssueKey(value))
                    return value;
            }
        }
        return null;
    }

    private static string? FindRole(string text)
    {
        var m = Regex.Match(text, @"(?i)\b(super\s*admin|admin|coordinator|fireman|viewer|editor|operator)\b");
        return m.Success ? Regex.Replace(m.Value, @"\s+", " ").Trim() : null;
    }

    private static string? FindEnvironmentName(string text)
    {
        var m = Regex.Match(text, @"(?i)\b(?:env(?:ironment)?|stage|stack)\s*[:=]\s*([A-Za-z0-9._\- ]+)");
        if (m.Success)
        {
            var value = m.Groups[1].Value.Trim();
            var host = EnvironmentUrlParser.FindFirstUrl(value);
            return host is not null ? new Uri(host).Host : value.Split('\n')[0].Trim();
        }

        m = Regex.Match(text, @"(?i)\b(reproduced on|tested on|running on)\s+([^\n\.]+)");
        if (m.Success)
            return m.Groups[2].Value.Trim();

        var url = EnvironmentUrlParser.FindFirstUrl(text);
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host;

        m = Regex.Match(text, @"(?i)\b(prod(?:uction)?|staging|qa|uat|rc|dev(?:elopment)?|local)\b");
        return m.Success ? m.Value : null;
    }

    private static string? FindSystemName(string text, RuntimeContext ctx)
    {
        var m = Regex.Match(text, @"(?i)\b(?:system|application|app|product)\s*[:=]\s*([A-Za-z0-9._\- ]+)");
        if (m.Success)
        {
            var value = m.Groups[1].Value.Trim();
            if (!LooksLikeIssueKey(value))
                return value;
        }

        return null;
    }

    private static bool LooksLikeIssueKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), @"^[A-Z][A-Z0-9]+-\d+$");

    private static bool IsCommonWord(string value) =>
        value.Equals("using", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("with", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("from", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("user", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("browser", StringComparison.OrdinalIgnoreCase);
}
