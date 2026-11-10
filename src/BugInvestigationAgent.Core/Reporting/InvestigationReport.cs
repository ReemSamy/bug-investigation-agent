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
    public string? Summary { get; init; }
    /// <summary>High / Medium / Low — how strongly the evidence supports the conclusions (see <see cref="ConfidenceReason"/>).</summary>
    public string Confidence { get; init; } = "Low";
    public string? ConfidenceReason { get; init; }
    public List<string> SimilarIssues { get; init; } = new();
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

    // Ticket detail
    public string? ExpectedResult { get; init; }
    public string? ActualResult { get; init; }
    public List<string> LinkedIssues { get; init; } = new();
    public List<string> IssueHistory { get; init; } = new();
    // Version and branches
    public string? KmsVersion { get; init; }
    public string? KmsVersionSource { get; init; }
    public string? SourceBranch { get; init; }
    public string? SourceCommit { get; init; }
    public List<string> FixCommitsInVersion { get; init; } = new();
    public string? CreatedBranch { get; init; }
    public string? BranchNote { get; init; }
    // Recommended fix (text only — never applied)
    public string? ProposedFix { get; init; }
    public List<string> AffectedFiles { get; init; } = new();
    public string? SuggestedCodeChange { get; init; }
    /// <summary>What prevented full verification and what information is needed.</summary>
    public List<string> MissingForVerification { get; init; } = new();

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

        if (ctx.BlockedBrowserRequests.Count > 0)
            confirmed.Add($"Read-only protection blocked {ctx.BlockedBrowserRequests.Count} state-changing browser request(s): " +
                          string.Join("; ", ctx.BlockedBrowserRequests.Distinct().Take(5)));

        foreach (var err in pageErrors.Take(3))
            confirmed.Add($"Browser reported a page error: {err}");

        if (!string.IsNullOrWhiteSpace(ctx.RootCause) && !(ctx.CustomFields.ContainsKey("investigationBlockedNoLlm") || ctx.CustomFields.ContainsKey("codeAnalysisSkipped")))
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

        var noLlm = ctx.CustomFields.ContainsKey("investigationBlockedNoLlm");
        var hasRootCause = !string.IsNullOrWhiteSpace(ctx.RootCause) && !(ctx.CustomFields.ContainsKey("investigationBlockedNoLlm") || ctx.CustomFields.ContainsKey("codeAnalysisSkipped"));
        if (ctx.CustomFields.TryGetValue("codeAnalysisSkipped", out var skipped))
            recommendations.Add("Code was not analysed: " + R(skipped));
        if (ctx.FixCommitsInVersion.Count > 0)
            confirmed.Add($"{ctx.FixCommitsInVersion.Count} commit(s) in {ctx.SourceVersionRef} (deployed KMS {ctx.DeployedVersion}) mention this ticket or a resolved linked issue: " +
                          string.Join("; ", ctx.FixCommitsInVersion.Take(3).Select(R)));
        if (!string.IsNullOrWhiteSpace(ctx.CreatedBranch))
            recommendations.Add($"Local branch {ctx.CreatedBranch} was created from {ctx.SourceVersionRef} for the fix (not checked out; nothing committed or pushed).");

        var missing = new List<string>();
        if (ctx.ReproductionOutcome is not ReproductionOutcome.Reproduced and not ReproductionOutcome.NotReproduced)
        {
            if (ctx.CustomFields.TryGetValue("manualStepsRemaining", out var manualCount) && manualCount != "0")
                missing.Add($"{manualCount} ticket step(s) need a person (form data entry, waiting or e-mail checks).");
            if (ctx.ReproductionSteps.Count == 0)
                missing.Add("The ticket has no reproduction steps.");
            if (!string.IsNullOrWhiteSpace(ctx.ReproductionNotes))
                missing.Add("Reproduction note: " + R(ctx.ReproductionNotes));
            missing.Add(ctx.ReproductionOutcome switch
            {
                ReproductionOutcome.Ambiguous => "The reproduction neither showed nor ruled out the defect: precise steps and the expected result shown in the UI are needed to verify it.",
                ReproductionOutcome.NotStarted => "Reproduction was not attempted.",
                _ => $"Reproduction could not run ({ctx.ReproductionOutcome}); resolve the problem above and investigate again."
            });
        }
        if (string.IsNullOrWhiteSpace(ctx.DeployedVersion))
            missing.Add("The deployed KMS version could not be identified" + (ctx.DeployedVersionSource is null ? "." : $" ({R(ctx.DeployedVersionSource)})."));
        if (string.IsNullOrWhiteSpace(ctx.SourceVersionRef) && !string.IsNullOrWhiteSpace(ctx.SourceVersionNote))
            missing.Add(R(ctx.SourceVersionNote));
        if (string.IsNullOrWhiteSpace(ctx.ExpectedResult))
            missing.Add("The ticket does not state the expected result.");
        if (noLlm)
            recommendations.Add("No code analysis result: configure an approved LLM provider in Sign in / Account (or fix its settings) and run the agent again. Detail: " + R(ctx.RootCause));
        if (hasRootCause && !reproducedWithEvidence)
            recommendations.Add($"Validate the root-cause hypothesis manually — reproduction was not confirmed (outcome: {ctx.ReproductionOutcome}).");
        if (ctx.CustomFields.TryGetValue("manualStepsRemaining", out var manual) && manual != "0")
            recommendations.Add($"{manual} ticket step(s) need a person (form data entry, waiting or e-mail checks); complete them manually to confirm the bug.");
        if (recommendations.Count == 0)
            recommendations.Add("Review the evidence and decide the next action for this ticket.");

        var (confidence, confidenceReason) = (reproducedWithEvidence, hasRootCause) switch
        {
            (true, true) => ("High", "Reproduced with screenshot evidence, and code analysis produced a root-cause hypothesis."),
            (true, false) => ("Medium", "Reproduced with screenshot evidence; no code analysis result."),
            (false, true) => ("Medium", $"Root cause is a code-analysis hypothesis; reproduction was not confirmed ({ctx.ReproductionOutcome})."),
            _ => ("Low", $"Reproduction not confirmed ({ctx.ReproductionOutcome}) and no code analysis result.")
        };

        var similar = ctx.Investigation.SimilarIssues.Take(5)
            .Select(i => $"{i.Key} — {R(i.Summary)} (score {i.RelevanceScore:0.00}, {(i.RankedByLlm ? "semantic" : "keyword match only")})")
            .ToList();

        return new InvestigationReport
        {
            IssueKey = ctx.JiraTicketKey ?? "?",
            InvestigationId = ctx.InvestigationId,
            Summary = R(ctx.Summary),
            Confidence = confidence,
            ConfidenceReason = confidenceReason,
            SimilarIssues = similar,
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
            Repository = repository,
            ExpectedResult = R(ctx.ExpectedResult),
            ActualResult = R(ctx.ActualResult),
            LinkedIssues = ctx.LinkedIssues.Select(R).ToList(),
            IssueHistory = ctx.IssueHistory.TakeLast(10).Select(R).ToList(),
            KmsVersion = ctx.DeployedVersion,
            KmsVersionSource = R(ctx.DeployedVersionSource),
            SourceBranch = ctx.SourceVersionRef,
            SourceCommit = ctx.SourceVersionCommit,
            FixCommitsInVersion = ctx.FixCommitsInVersion.Select(R).ToList(),
            CreatedBranch = ctx.CreatedBranch,
            BranchNote = R(ctx.BranchNote),
            ProposedFix = hasRootCause ? R(ctx.ProposedFixSummary) : null,
            AffectedFiles = hasRootCause ? ctx.ProposedFiles.Take(15).ToList() : new List<string>(),
            SuggestedCodeChange = hasRootCause && !string.IsNullOrWhiteSpace(ctx.ProposedDiff) ? R(ctx.ProposedDiff) : null,
            MissingForVerification = missing
        };
    }

    /// <summary>Markdown for the Jira agent task (Rovo). Long parts are collapsed in &lt;details&gt;.</summary>
    public static string ToMarkdown(InvestigationReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Investigation of {r.IssueKey}** — reproduction: **{r.ReproductionOutcome}**");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(r.Summary))
            sb.AppendLine($"Ticket: {r.Summary}").AppendLine();
        sb.AppendLine($"Environment: {r.EnvironmentName ?? "—"}{(r.EnvironmentType is null ? "" : $" ({r.EnvironmentType})")}{(r.ReadOnlyEnvironment ? " — read-only" : "")} · Sign-in: {r.Authentication}");
        sb.AppendLine();
        sb.AppendLine($"Confidence: **{r.Confidence}** — {r.ConfidenceReason}");
        sb.AppendLine();
        sb.AppendLine($"KMS version: {r.KmsVersion ?? "unknown"}{(r.KmsVersionSource is null ? "" : $" ({r.KmsVersionSource})")} · Source branch: {r.SourceBranch ?? "—"}{(r.SourceCommit is null ? "" : $" @ {r.SourceCommit[..Math.Min(10, r.SourceCommit.Length)]}")}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(r.CreatedBranch))
            sb.AppendLine($"**Local branch created: {r.CreatedBranch}** (not checked out; nothing committed or pushed).").AppendLine();
        else if (!string.IsNullOrWhiteSpace(r.BranchNote))
            sb.AppendLine("Branch: " + r.BranchNote).AppendLine();
        AppendList(sb, "Confirmed findings (backed by evidence)", r.ConfirmedFindings, "None.");
        AppendList(sb, "Hypotheses (not verified)", r.Hypotheses, "None.");
        AppendList(sb, "Recommendations", r.Recommendations, "None.");
        if (r.MissingForVerification.Count > 0)
            AppendList(sb, "What prevented full verification / information needed", r.MissingForVerification, "");
        if (!string.IsNullOrWhiteSpace(r.ProposedFix))
        {
            sb.AppendLine("**Recommended fix (not applied):** " + r.ProposedFix);
            AppendList(sb, "Affected files", r.AffectedFiles, "Not identified.");
            if (!string.IsNullOrWhiteSpace(r.SuggestedCodeChange))
                sb.AppendLine("Suggested code change:").AppendLine("```diff").AppendLine(r.SuggestedCodeChange.Trim()).AppendLine("```").AppendLine();
        }
        sb.AppendLine("<details><summary>Reproduction, code and evidence details</summary>");
        sb.AppendLine();
        sb.AppendLine("Reproduction: " + r.ReproductionSummary);
        AppendList(sb, "Steps followed", r.ReproductionSteps, "None.");
        if (r.SkippedSteps.Count > 0)
            AppendList(sb, "Skipped (Production read-only)", r.SkippedSteps, "");
        AppendList(sb, "Code findings" + (r.Repository is null ? "" : $" ({r.Repository})"), r.CodeFindings, "None.");
        AppendList(sb, "Similar historical issues (candidates, not confirmed duplicates)", r.SimilarIssues, "None found.");
        AppendList(sb, "Linked Jira issues", r.LinkedIssues, "None.");
        AppendList(sb, "Commits in the version branch mentioning the ticket", r.FixCommitsInVersion, "None.");
        if (!string.IsNullOrWhiteSpace(r.ExpectedResult) || !string.IsNullOrWhiteSpace(r.ActualResult))
            sb.AppendLine($"Expected: {r.ExpectedResult ?? "—"}").AppendLine($"Actual: {r.ActualResult ?? "—"}").AppendLine();
        AppendList(sb, "Recent issue history", r.IssueHistory, "None.");
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
