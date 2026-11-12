using System.Text;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Core.Reporting;

namespace BugInvestigationAgent.Core.Investigations;

/// <summary>
/// Concise, human-reviewable Jira comment built from an investigation report. The full report stays in the tool;
/// the comment is a summary. Every draft is labelled AI-generated and ends with a marker line that makes
/// publication idempotent (an existing comment with the marker is never posted again).
/// </summary>
public static class CommentDraftBuilder
{
    public const string Header = "[AI-generated investigation summary — requires human review; not posted to Jira]";

    public static string Marker(Guid recordId) => $"AI-INVESTIGATION-ID: {recordId:N}";

    public static string Build(InvestigationRecord record, InvestigationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine($"Investigation of {record.IssueKey}: {InvestigationAssessor.Short(record.ExecutionDetail, 400)}");
        sb.AppendLine($"Assessment: {InvestigationAssessor.Label(record.Assessment)} — {InvestigationAssessor.Short(record.AssessmentReason, 220)}");
        var env = string.IsNullOrWhiteSpace(record.EnvironmentName) ? "" : $" on {record.EnvironmentName}";
        var notProof = record.ReproductionOutcome == ReproductionResult.NotReproduced ? " (not proof that the bug is fixed)" : "";
        sb.AppendLine($"Reproduction: {InvestigationAssessor.Label(record.ReproductionOutcome)}{env}{notProof}.");
        sb.AppendLine($"KMS version: {record.KmsVersion ?? "unknown"}; source branch: {record.SourceBranch ?? "—"}" +
                      (string.IsNullOrWhiteSpace(record.CreatedBranch) ? "." : $"; local branch {record.CreatedBranch} created for the fix."));
        if (!string.IsNullOrWhiteSpace(record.RootCause))
            sb.AppendLine($"Root cause ({(record.RootCauseConfirmed ? "Confirmed" : "Hypothesis")}): {InvestigationAssessor.Short(record.RootCause, 400)}");
        sb.AppendLine($"Confidence: {report.Confidence} — {InvestigationAssessor.Short(report.ConfidenceReason, 200)}");

        AppendList(sb, "Key evidence:", report.ConfirmedFindings.Take(3), 240);
        var related = record.SimilarIssueKeys.Take(6).ToList();
        if (related.Count > 0)
            sb.AppendLine("Related issues: " + string.Join(", ", related));
        AppendList(sb, "Code references:", report.CodeFindings.Take(3), 240);
        AppendList(sb, "Recommended next steps:",
            new[] { record.RecommendedAction ?? "" }.Concat(report.Recommendations).Where(s => s.Length > 0).Distinct().Take(4), 240);
        sb.AppendLine("Full investigation report: kept in the AI Bug Investigation Tool.");
        sb.Append(Marker(record.Id));
        return sb.ToString();
    }

    /// <summary>Plain-text draft → Atlassian Document Format; "- " lines become a bullet list. Marker line is kept verbatim.</summary>
    public static string ToAdf(string text, Guid recordId)
    {
        var content = new JsonArray();
        JsonArray? bullets = null;
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        var marker = Marker(recordId);
        if (!lines.Any(l => l.Contains(marker, StringComparison.Ordinal)))
            lines.Add(marker); // an edit may remove it; it is required for duplicate prevention
        foreach (var line in lines)
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                bullets ??= new JsonArray();
                bullets.Add(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(Paragraph(line[2..])) });
                continue;
            }
            if (bullets is not null)
            {
                content.Add(new JsonObject { ["type"] = "bulletList", ["content"] = bullets });
                bullets = null;
            }
            content.Add(Paragraph(line));
        }
        if (bullets is not null)
            content.Add(new JsonObject { ["type"] = "bulletList", ["content"] = bullets });
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = content }.ToJsonString();
    }

    private static JsonObject Paragraph(string text) =>
        new() { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };

    private static void AppendList(StringBuilder sb, string title, IEnumerable<string> items, int max)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (list.Count == 0)
            return;
        sb.AppendLine(title);
        foreach (var item in list)
            sb.AppendLine("- " + InvestigationAssessor.Short(item.Replace('\n', ' '), max));
    }
}
