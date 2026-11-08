using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Workflow;

namespace BugInvestigationAgent.Core.Reporting;

/// <summary>
/// Structured investigation report. Findings are split into CONFIRMED (backed by captured evidence) and
/// HYPOTHESES (analysis not verified by evidence). The builder never upgrades a hypothesis to a confirmed finding.
/// </summary>
public sealed class InvestigationReport
{
    public required string IssueKey { get; init; }
    public required Guid InvestigationId { get; init; }
    public string? EnvironmentId { get; init; }
    public string? EnvironmentName { get; init; }
    public string? EnvironmentType { get; init; }
    public string? EnvironmentUrl { get; init; }
    public bool ReadOnlyEnvironment { get; init; }
    public required string Authentication { get; init; }
    public required string ReproductionOutcome { get; init; }
    public required string ReproductionSummary { get; init; }
    public List<string> ConfirmedFindings { get; init; } = new();
    public List<string> Hypotheses { get; init; } = new();
    public List<string> CodeFindings { get; init; } = new();
    public List<string> Recommendations { get; init; } = new();
    public List<string> ReproductionSteps { get; init; } = new();
    public List<string> SkippedSteps { get; init; } = new();
    public List<EvidenceReference> Evidence { get; init; } = new();
    public string? Repository { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
    public static InvestigationReport? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<InvestigationReport>(json);
}

public sealed class EvidenceReference
{
    public required string FileName { get; init; }
    public string? Caption { get; init; }
    public string? Category { get; init; }
}

public static class InvestigationReportBuilder
{
    public static InvestigationReport Build(RuntimeContext ctx, ISecretRedactor redactor, string? repository = null)
    {
        string R(string? s) => redactor.Redact(s ?? "");

        var screenshots = ctx.Evidence.Where(e => e.Kind == "screenshot" && !string.IsNullOrWhiteSpace(e.Path)).ToList();
        var failureShots = screenshots.Where(e => e.Category is EvidenceCategories.FailureState or EvidenceCategories.ErrorMessage).ToList();
        var pageErrors = ctx.Evidence.Where(e => e.Kind == "error").Select(e => R(e.Caption)).Where(s => s.Length > 0).ToList();

        var confirmed = new List<string>();
        var hypotheses = new List<string>();

        if (ctx.AuthenticationStatus == AuthenticationStatus.Succeeded)
            confirmed.Add($"Signed in to {ctx.SelectedEnvironmentDisplayName ?? ctx.EnvironmentName} successfully (verified, see post-login screenshot).");

        var reproducedWithEvidence = ctx.ReproductionOutcome == ReproductionOutcome.Reproduced && screenshots.Count > 0;
        switch (ctx.ReproductionOutcome)
        {
            case ReproductionOutcome.Reproduced when reproducedWithEvidence:
                confirmed.Add($"Bug reproduced on {ctx.SelectedEnvironmentDisplayName ?? "the selected environment"}: {R(ctx.ReproductionNotes)}" +
                              (failureShots.Count > 0 ? $" Evidence: {string.Join(", ", failureShots.Select(s => Path.GetFileName(s.Path)))}." : ""));
                break;
            case ReproductionOutcome.Reproduced:
                // A "reproduced" signal without any captured evidence is NOT reported as confirmed.
                hypotheses.Add($"A defect signal was detected but no screenshot evidence was captured, so reproduction is unconfirmed: {R(ctx.ReproductionNotes)}");
                break;
            case ReproductionOutcome.NotReproduced:
                confirmed.Add($"The reported behaviour was NOT observed when following the steps on {ctx.SelectedEnvironmentDisplayName ?? "the selected environment"}.");
                break;
        }

        foreach (var err in pageErrors.Take(3))
            confirmed.Add($"Browser reported a page error: {err}");

        if (!string.IsNullOrWhiteSpace(ctx.RootCause) && !ctx.CustomFields.ContainsKey("investigationBlockedNoLlm"))
        {
            // Root cause from code analysis is a hypothesis unless a fix was applied AND the retest no longer shows the bug.
            if (ctx.FixApproved && ctx.RetestPassed)
                confirmed.Add($"Root cause confirmed by fix + retest: {R(ctx.RootCause)}");
            else
                hypotheses.Add($"Probable root cause (code analysis, not verified by a fix/retest): {R(ctx.RootCause)}");
        }

        if (!string.IsNullOrWhiteSpace(ctx.Investigation.EvidenceReasoning))
            hypotheses.Add("Reasoning: " + R(ctx.Investigation.EvidenceReasoning));
        if (!string.IsNullOrWhiteSpace(ctx.FixPullRequestUrl))
            hypotheses.Add($"The ticket links a fix PR ({ctx.FixPullRequestUrl}); it may not be deployed on the tested environment.");

        var codeFindings = new List<string>();
        if (ctx.ProposedFiles.Count > 0)
            codeFindings.Add("Files most relevant to the defect: " + string.Join(", ", ctx.ProposedFiles.Take(10)));
        foreach (var commit in ctx.CodeCorrelation.Take(10))
            codeFindings.Add(R(commit));

        var recommendations = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.ProposedFixSummary))
            recommendations.Add("Proposed fix (for human review, not applied): " + R(ctx.ProposedFixSummary));
        if (!string.IsNullOrWhiteSpace(ctx.Investigation.PotentialImpact))
            recommendations.Add("Potential impact: " + R(ctx.Investigation.PotentialImpact));
        if (ctx.ReproductionOutcome is ReproductionOutcome.Ambiguous)
            recommendations.Add("Add precise reproduction steps to the ticket and run the agent again.");
        if (ctx.SkippedStateChangingSteps.Count > 0)
            recommendations.Add("Some steps were skipped because Production is read-only; reproduce them on a non-production environment.");
        if (ctx.ReproductionOutcome is ReproductionOutcome.Blocked or ReproductionOutcome.Error or ReproductionOutcome.EnvironmentUnavailable)
            recommendations.Add("Resolve the blocking problem above and run the agent again.");

        return new InvestigationReport
        {
            IssueKey = ctx.JiraTicketKey ?? "?",
            InvestigationId = ctx.InvestigationId,
            EnvironmentId = ctx.SelectedEnvironmentId,
            EnvironmentName = ctx.SelectedEnvironmentDisplayName ?? ctx.EnvironmentName,
            EnvironmentType = ctx.SelectedEnvironmentType?.ToString(),
            EnvironmentUrl = EnvironmentUrlSanitizer.StripCredentials(ctx.EnvironmentUrl),
            ReadOnlyEnvironment = ctx.IsReadOnlyEnvironment,
            Authentication = AuthenticationStatusDisplay.Label(ctx.AuthenticationStatus),
            ReproductionOutcome = ctx.ReproductionOutcome.ToString(),
            ReproductionSummary = R(ctx.ReproductionNotes ?? "Reproduction did not run."),
            ConfirmedFindings = confirmed,
            Hypotheses = hypotheses,
            CodeFindings = codeFindings,
            Recommendations = recommendations,
            ReproductionSteps = ctx.ReproductionSteps.Take(15).Select(R).ToList(),
            SkippedSteps = ctx.SkippedStateChangingSteps.Select(R).ToList(),
            Evidence = screenshots.Select(s => new EvidenceReference
            {
                FileName = Path.GetFileName(s.Path!),
                Caption = R(s.Caption),
                Category = s.Category
            }).ToList(),
            Repository = repository
        };
    }

    /// <summary>Markdown for the Jira agent task (Rovo). Long parts are collapsed in &lt;details&gt;.</summary>
    public static string ToMarkdown(InvestigationReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Investigation of {r.IssueKey}** — reproduction: **{r.ReproductionOutcome}**");
        sb.AppendLine();
        sb.AppendLine($"Environment: {r.EnvironmentName ?? "—"}{(r.EnvironmentType is null ? "" : $" ({r.EnvironmentType})")}{(r.ReadOnlyEnvironment ? " — read-only" : "")} · Sign-in: {r.Authentication}");
        sb.AppendLine();
        AppendList(sb, "Confirmed findings (backed by evidence)", r.ConfirmedFindings, "None.");
        AppendList(sb, "Hypotheses (not verified)", r.Hypotheses, "None.");
        AppendList(sb, "Recommendations", r.Recommendations, "None.");
        sb.AppendLine("<details><summary>Reproduction, code and evidence details</summary>");
        sb.AppendLine();
        sb.AppendLine("Reproduction: " + r.ReproductionSummary);
        AppendList(sb, "Steps followed", r.ReproductionSteps, "None.");
        if (r.SkippedSteps.Count > 0)
            AppendList(sb, "Skipped (Production read-only)", r.SkippedSteps, "");
        AppendList(sb, "Code findings" + (r.Repository is null ? "" : $" ({r.Repository})"), r.CodeFindings, "None.");
        AppendList(sb, "Evidence (screenshots, passwords masked)", r.Evidence.Select(e => $"{e.FileName} — {e.Caption}").ToList(), "None.");
        sb.AppendLine($"Investigation ID: {r.InvestigationId:N}");
        sb.AppendLine("</details>");
        return sb.ToString();
    }

    /// <summary>Atlassian Document Format body for the Jira issue comment.</summary>
    public static JsonObject ToAdf(InvestigationReport r, IReadOnlyDictionary<string, string>? attachmentLinks = null)
    {
        var content = new JsonArray
        {
            Heading($"AI Bug Investigation — {r.IssueKey}"),
            Paragraph($"Reproduction: {r.ReproductionOutcome}. Environment: {r.EnvironmentName ?? "—"}{(r.EnvironmentType is null ? "" : $" ({r.EnvironmentType})")}{(r.ReadOnlyEnvironment ? ", read-only" : "")}. Sign-in: {r.Authentication}."),
            Heading("Confirmed findings (backed by evidence)", 3),
            BulletList(r.ConfirmedFindings, "None."),
            Heading("Hypotheses (not verified)", 3),
            BulletList(r.Hypotheses, "None."),
            Heading("Reproduction", 3),
            Paragraph(r.ReproductionSummary),
            BulletList(r.ReproductionSteps, "No steps recorded."),
            Heading("Code findings" + (r.Repository is null ? "" : $" ({r.Repository})"), 3),
            BulletList(r.CodeFindings, "None."),
            Heading("Recommendations", 3),
            BulletList(r.Recommendations, "None."),
            Heading("Evidence", 3),
            BulletList(r.Evidence.Select(e =>
                (attachmentLinks is not null && attachmentLinks.ContainsKey(e.FileName) ? "[attached] " : "") +
                $"{e.FileName} — {e.Caption}").ToList(), "No screenshots."),
            Paragraph($"Investigation ID {r.InvestigationId:N}. Generated by the AI Bug Investigation Agent; review before acting. Code was not modified.")
        };
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = content };
    }

    private static void AppendList(StringBuilder sb, string title, IReadOnlyCollection<string> items, string empty)
    {
        sb.AppendLine($"**{title}**");
        if (items.Count == 0)
            sb.AppendLine("- " + empty);
        foreach (var i in items)
            sb.AppendLine("- " + i.Replace('\n', ' '));
        sb.AppendLine();
    }

    private static JsonObject Heading(string text, int level = 2) => new()
    {
        ["type"] = "heading",
        ["attrs"] = new JsonObject { ["level"] = level },
        ["content"] = new JsonArray { Text(text) }
    };

    private static JsonObject Paragraph(string text) => new()
    {
        ["type"] = "paragraph",
        ["content"] = new JsonArray { Text(string.IsNullOrWhiteSpace(text) ? "—" : text) }
    };

    private static JsonObject BulletList(IReadOnlyCollection<string> items, string empty)
    {
        var list = new JsonArray();
        foreach (var item in items.Count == 0 ? new[] { empty } : items.ToArray())
            list.Add(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray { Paragraph(item) } });
        return new JsonObject { ["type"] = "bulletList", ["content"] = list };
    }

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text.Length > 4000 ? text[..4000] + "…" : text };
}
