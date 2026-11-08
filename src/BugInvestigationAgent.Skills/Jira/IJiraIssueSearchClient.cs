using BugInvestigationAgent.Core.Context;

namespace BugInvestigationAgent.Skills.Jira;

public interface IJiraIssueSearchClient
{
    Task<IReadOnlyList<JiraSearchHit>> SearchAsync(string jql, int maxResults, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JiraSearchHit>> SearchAsync(BugInvestigationAgent.Core.Context.RuntimeContext context, string jql, int maxResults, CancellationToken cancellationToken = default);
}

public sealed class JiraSearchHit
{
    public string Key { get; set; } = "";
    public string? Summary { get; set; }
    public string? Status { get; set; }
    public string? Description { get; set; }
    public IList<string> ReproductionSteps { get; set; } = new List<string>();
    public string? Url { get; set; }
}

/// <summary>
/// Extracts problem-focused search terms. Excludes version/epic noise (V14, Beta, etc.)
/// so candidate retrieval is not dominated by same-release siblings.
/// </summary>
public static class BugSimilarityTerms
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "from", "with", "without",
        "when", "then", "than", "that", "this", "these", "those", "into", "over", "under", "after",
        "before", "while", "about", "across", "all", "any", "not", "but", "are",
        "is", "was", "were", "be", "been", "being", "have", "has", "had", "does", "did", "do",
        "bug", "issue", "ticket", "defect", "error", "problem", "fix", "failed", "fails",
        "v12", "v13", "v14", "v15", "beta", "alpha", "rc", "release", "version", "sprint",
        "module", "modules", "area", "screen", "page", "user", "users", "customer", "customers",
        "behavior", "behaviour", "desired", "undesired", "maybe", "like", "via", "using",
        "add", "adding", "added", "new", "old", "same", "different"
    };

    public static IReadOnlyList<string> Extract(RuntimeContext ctx, InvestigationIntent? intent, int maxTerms = 8)
    {
        var terms = new List<string>();

        void AddPhrase(string? phrase)
        {
            if (string.IsNullOrWhiteSpace(phrase))
                return;
            foreach (var token in Tokenize(phrase))
                terms.Add(token);
        }

        if (intent is not null)
        {
            AddPhrase(intent.ActualProblem);
            AddPhrase(intent.ActualBehavior);
            AddPhrase(intent.ExpectedBehavior);
            AddPhrase(intent.AffectedModule);
            AddPhrase(intent.BugCategory);
            AddPhrase(intent.ReproductionIntent);
            foreach (var k in intent.Keywords.Take(8))
                AddPhrase(k);
            foreach (var e in intent.RelevantEntities.Take(4))
                AddPhrase(e);
        }

        if (terms.Count < 3)
        {
            AddPhrase(StripReleasePrefix(ctx.Summary));
            AddPhrase(StripReleasePrefix(ctx.Description));
            foreach (var step in ctx.ReproductionSteps.Take(5))
                AddPhrase(step);
        }

        return terms
            .Where(t => t.Length >= 3 && !StopWords.Contains(t) && !LooksLikeVersionToken(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxTerms)
            .ToList();
    }

    public static double KeywordOverlapScore(IEnumerable<string> queryTerms, string? summary, string? description, IEnumerable<string>? steps)
    {
        var terms = queryTerms.ToList();
        if (terms.Count == 0)
            return 0;

        var hay = string.Join(' ',
            new[] { summary ?? "", description ?? "", string.Join(' ', steps ?? Array.Empty<string>()) });
        if (string.IsNullOrWhiteSpace(hay))
            return 0;

        var hits = terms.Count(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
        // Cap so keyword fallback never looks like high semantic similarity.
        return Math.Min(0.55, (double)hits / terms.Count);
    }

    public static string SimilarityLevelFromScore(double score, bool sameUnderlyingProblem)
    {
        if (sameUnderlyingProblem && score >= 0.7)
            return "High";
        if (score >= 0.75)
            return "High";
        if (score >= 0.55)
            return "Medium";
        if (score >= 0.25)
            return "Low";
        return "Unrelated";
    }

    public static string StripReleasePrefix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"^\s*V?\d+(\.\d+)*\s*[-–—:]*\s*(Beta|Alpha|RC)?\s*[-–—:]*\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
    }

    private static IEnumerable<string> Tokenize(string phrase)
    {
        foreach (var raw in phrase.Split(new[] { ' ', '\t', '\r', '\n', '/', '\\', ',', ';', ':', '|', '(', ')', '[', ']', '"', '\'' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (cleaned.Length >= 3)
                yield return cleaned;
        }
    }

    private static bool LooksLikeVersionToken(string term) =>
        System.Text.RegularExpressions.Regex.IsMatch(term, @"^v?\d+(\.\d+)*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

/// <summary>Builds JQL from investigation intent + current ticket project (candidate retrieval only).</summary>
public static class HistoricalJiraJqlBuilder
{
    public static string Build(RuntimeContext ctx, InvestigationIntent? intent)
    {
        var project = ExtractProjectKey(ctx.JiraTicketKey) ?? "VKMS";
        var sanitized = BugSimilarityTerms.Extract(ctx, intent, maxTerms: 6)
            .Select(SanitizeTerm)
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        var textClause = sanitized.Count == 0
            ? "text ~ \"bug\""
            : string.Join(" OR ", sanitized.Select(t => $"text ~ \"{t}\""));

        var exclude = string.IsNullOrWhiteSpace(ctx.JiraTicketKey) ? "" : $" AND key != {ctx.JiraTicketKey}";
        return $"project = {project} AND ({textClause}) AND statusCategory in (Done, \"In Progress\", \"To Do\"){exclude} ORDER BY updated DESC";
    }

    public static string? ExtractProjectKey(string? issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            return null;
        var dash = issueKey.IndexOf('-');
        return dash > 0 ? issueKey[..dash] : null;
    }

    private static string SanitizeTerm(string term) =>
        new string(term.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()).Replace("\"", "");
}
