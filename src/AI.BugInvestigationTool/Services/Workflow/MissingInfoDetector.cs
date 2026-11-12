using System.Text.RegularExpressions;
using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Models;

namespace AI.BugInvestigationTool.Services.Workflow;

/// <summary>The values the browser investigation will actually use, after merging user input, ticket and Claude's reading.</summary>
public sealed record EffectiveContext(
    string? EnvironmentUrl,
    string ExpectedBehavior,
    string ActualBehavior,
    List<string> ReproductionSteps,
    bool RequiresLogin);

/// <summary>
/// Deterministic missing-information rules. Claude's suggestions are merged in, but the essential checks
/// do not depend on the model: no browser testing without an allowed environment URL, login (when needed),
/// expected behaviour and reproduction steps.
/// </summary>
public static partial class MissingInfoDetector
{
    public const string EnvironmentUrl = "environmentUrl";
    public const string EnvironmentLogin = "environmentLogin";
    public const string ExpectedBehavior = "expectedBehavior";
    public const string ActualBehavior = "actualBehavior";
    public const string ReproductionSteps = "reproductionSteps";
    public const string NoLogin = "noLoginRequired";

    [GeneratedRegex(@"(?ims)^\W*(expected\s*(result|behaviou?r|outcome)?s?)\s*[:\-]\s*(?<v>.+?)(?=^\W*(actual|steps|environment|notes?)\b[^\n]*[:\-]|\z)")]
    private static partial Regex ExpectedRegex();
    [GeneratedRegex(@"(?ims)^\W*(actual\s*(result|behaviou?r|outcome)?s?)\s*[:\-]\s*(?<v>.+?)(?=^\W*(expected|steps|environment|notes?)\b[^\n]*[:\-]|\z)")]
    private static partial Regex ActualRegex();
    [GeneratedRegex(@"(?ims)^\W*(steps\s*(to\s*reproduce)?|reproduction\s*steps|str)\s*[:\-]\s*(?<v>.+?)(?=^\W*(expected|actual|environment|notes?)\b[^\n]*[:\-]|\z)")]
    private static partial Regex StepsRegex();
    [GeneratedRegex(@"https?://[^\s<>""')\]]+")]
    private static partial Regex UrlRegex();

    public static (string? Expected, string? Actual, List<string> Steps) ExtractFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return (null, null, []);
        string? Grab(Regex r) => r.Match(description) is { Success: true } m ? m.Groups["v"].Value.Trim() : null;
        var steps = Grab(StepsRegex())?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Regex.Replace(s, @"^(\d+[\.\)]|[-*•])\s*", ""))
            .Where(s => s.Length > 0)
            .ToList() ?? [];
        return (Grab(ExpectedRegex()), Grab(ActualRegex()), steps);
    }

    public static EffectiveContext Resolve(JiraIssue? issue, TicketUnderstanding? understanding, IReadOnlyDictionary<string, string> userInfo, Credentials credentials)
    {
        var (expected, actual, steps) = ExtractFromDescription(issue?.Description);

        string? User(string key) => userInfo.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        var userSteps = User(ReproductionSteps)?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var noLogin = string.Equals(User(NoLogin), "true", StringComparison.OrdinalIgnoreCase);

        return new EffectiveContext(
            EnvironmentUrl: User(EnvironmentUrl) ?? NonEmpty(credentials.EnvironmentUrl) ?? NonEmpty(understanding?.EnvironmentUrl),
            ExpectedBehavior: User(ExpectedBehavior) ?? NonEmpty(understanding?.ExpectedBehavior) ?? expected ?? "",
            ActualBehavior: User(ActualBehavior) ?? NonEmpty(understanding?.ActualBehavior) ?? actual ?? "",
            ReproductionSteps: userSteps ?? (understanding?.ReproductionSteps is { Count: > 0 } s ? s : steps),
            RequiresLogin: !noLogin && (understanding?.RequiresLogin ?? true));
    }

    /// <summary>Candidate environment URLs mentioned in the ticket (for the UI to suggest; never used automatically).</summary>
    public static List<string> UrlsInTicket(JiraIssue? issue) =>
        issue is null ? [] : UrlRegex().Matches($"{issue.Environment}\n{issue.Description}").Select(m => m.Value.TrimEnd('.', ',')).Distinct().Take(10).ToList();

    public static List<MissingInfoItem> Detect(EffectiveContext ctx, TicketUnderstanding? understanding, IReadOnlyDictionary<string, string> userInfo,
        Credentials credentials, IReadOnlyCollection<string> allowedHosts)
    {
        var missing = new List<MissingInfoItem>();

        if (ctx.EnvironmentUrl is null)
            missing.Add(new() { Key = EnvironmentUrl, Required = true, Question = "Which environment URL should be used to reproduce the issue?" });
        else if (!HostPolicy.TryValidateEnvironmentUrl(ctx.EnvironmentUrl, EffectiveAllowedHosts(allowedHosts, null), out _, out var error))
            missing.Add(new() { Key = EnvironmentUrl, Required = true, Question = $"The environment URL cannot be used: {error}" });

        if (ctx.RequiresLogin && !credentials.HasEnvironmentLogin)
            missing.Add(new()
            {
                Key = EnvironmentLogin, Required = true,
                Question = "Environment username and password are needed to sign in (enter them in the credentials form, or confirm no login is required).",
            });

        if (string.IsNullOrWhiteSpace(ctx.ExpectedBehavior))
            missing.Add(new() { Key = ExpectedBehavior, Required = true, Question = "What is the expected behaviour?" });

        if (ctx.ReproductionSteps.Count == 0)
            missing.Add(new() { Key = ReproductionSteps, Required = true, Question = "What are the steps to reproduce the issue (one per line)?" });

        if (string.IsNullOrWhiteSpace(ctx.ActualBehavior))
            missing.Add(new() { Key = ActualBehavior, Required = false, Question = "What is the actual (incorrect) behaviour observed?" });

        // Claude's extra questions: never override the rules above, and skip anything already answered.
        foreach (var item in understanding?.MissingInformation ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Question)) continue;
            if (missing.Any(m => m.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase))) continue;
            if (userInfo.ContainsKey(item.Key)) continue;
            if (IsStandardKey(item.Key)) continue;
            missing.Add(new() { Key = item.Key, Question = item.Question, Required = item.Required });
        }

        return missing;
    }

    private static bool IsStandardKey(string key) =>
        key.Equals(EnvironmentUrl, StringComparison.OrdinalIgnoreCase) || key.Equals(EnvironmentLogin, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(ExpectedBehavior, StringComparison.OrdinalIgnoreCase) || key.Equals(ReproductionSteps, StringComparison.OrdinalIgnoreCase) ||
        key.Equals(ActualBehavior, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> EffectiveAllowedHosts(IReadOnlyCollection<string> configured, string? extra) =>
        extra is null ? configured : configured.Append(extra).ToList();
}
