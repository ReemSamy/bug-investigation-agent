using System.Text;
using System.Text.Json.Nodes;
using AI.BugInvestigationTool.Models;

namespace AI.BugInvestigationTool.Services.Workflow;

/// <summary>Renders an investigation as Markdown (CLI, report.md, PR comment) and as a Jira comment (ADF).</summary>
public static class ReportFormatter
{
    public static string OutcomeLabel(InvestigationOutcome o) => o switch
    {
        InvestigationOutcome.NotReproducible => "NOT REPRODUCIBLE",
        _ => o.ToString().ToUpperInvariant(),
    };

    public static string ToMarkdown(Investigation inv)
    {
        var sb = new StringBuilder();
        var r = inv.Report;
        var t = inv.Target;
        sb.AppendLine(t.Kind == TargetKind.JiraIssue
            ? $"# Bug investigation: {t.JiraKey}{(inv.Issue is null ? "" : " — " + inv.Issue.Summary)}"
            : $"# PR investigation: #{t.PullRequestId} {inv.PullRequest?.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Investigation ID:** `{inv.Id}`");
        sb.AppendLine($"- **Source:** {t.Url}");
        sb.AppendLine($"- **State:** {inv.Step}");
        if (inv.Error is not null) sb.AppendLine($"- **Error:** {inv.Error}");

        if (inv.Step == InvestigationStep.WaitingForInput)
        {
            sb.AppendLine();
            sb.AppendLine("## Information needed before browser testing");
            foreach (var m in inv.MissingInfo)
                sb.AppendLine($"- [{(m.Required ? "REQUIRED" : "optional")}] `{m.Key}` — {m.Question}");
        }

        if (inv.PullRequest is { } pr)
        {
            sb.AppendLine();
            sb.AppendLine($"## Pull request `{pr.SourceBranch}` → `{pr.TargetBranch}`");
            sb.AppendLine($"**Author:** {pr.Author} · **Status:** {pr.Status} · **Scope:** {pr.Files.Count} files, +{pr.Additions} / -{pr.Deletions}");
            if (pr.LinkedJiraKeys.Count > 0 || pr.LinkedWorkItems.Count > 0)
                sb.AppendLine($"**Linked work:** {string.Join(", ", pr.LinkedJiraKeys.Concat(pr.LinkedWorkItems.Select(w => "#" + w)))}");
            sb.AppendLine();
            sb.AppendLine("| File | Change | +/- |");
            sb.AppendLine("|---|---|---|");
            foreach (var f in pr.Files)
                sb.AppendLine($"| `{f.Path}` | {f.ChangeType} | +{f.Additions}/-{f.Deletions}{(f.SkippedReason is null ? "" : $" ({f.SkippedReason})")} |");
        }

        if (r is null) return sb.ToString();

        sb.AppendLine();
        if (t.Kind == TargetKind.JiraIssue)
        {
            sb.AppendLine($"## Outcome: **{OutcomeLabel(r.Outcome)}** (confidence {r.Confidence:P0})");
            if (r.ProposedOutcome is not null) sb.AppendLine($"> Claude proposed {OutcomeLabel(r.ProposedOutcome.Value)}; adjusted because {r.OutcomeAdjustmentReason}.");
        }
        else
        {
            sb.AppendLine($"## Verdict (advisory): **{r.PrVerdict}** (confidence {r.Confidence:P0})");
        }

        Section(sb, "Summary", r.Summary);
        if (t.Kind == TargetKind.JiraIssue)
        {
            Section(sb, "Expected behaviour", r.ExpectedBehavior);
            Section(sb, "Actual behaviour", r.ActualBehavior);
            Section(sb, "Reproduction result", r.ReproductionResult);
            if (inv.Browser is { } b)
            {
                sb.AppendLine($"- Browser attempted: {b.Attempted}, login succeeded: {b.LoginSucceeded}, reproduced: {b.Reproduced}, expected behaviour observed: {b.ExpectedBehaviorObserved}");
                if (b.Error is not null) sb.AppendLine($"- Browser error: {b.Error}");
                foreach (var s in b.StepsExecuted) sb.AppendLine($"  - {s}");
            }
            Section(sb, $"Root cause ({r.RootCauseStatus})", r.RootCause);
        }

        if (r.Findings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Findings");
            sb.AppendLine("| # | Finding | Severity | File | Lines |");
            sb.AppendLine("|---|---|---|---|---|");
            var i = 1;
            foreach (var f in r.Findings)
            {
                var lines = f.Reference?.StartLine is null ? "" : f.Reference.EndLine is null || f.Reference.EndLine == f.Reference.StartLine ? $"{f.Reference.StartLine}" : $"{f.Reference.StartLine}–{f.Reference.EndLine}";
                sb.AppendLine($"| {i++} | {Cell(f.Title)}: {Cell(f.Detail)}{(f.Recommendation.Length > 0 ? " **Fix:** " + Cell(f.Recommendation) : "")} | {f.Severity} | `{f.Reference?.Path}`{(f.ReferenceUnverified ? " ⚠ unverified" : "")} | {lines} |");
            }
        }

        if (r.CodeReferences.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Code references");
            foreach (var c in r.CodeReferences) sb.AppendLine($"- `{c}` — {c.Note}");
        }
        List(sb, "Recommendations", r.Recommendations);
        List(sb, "Missing tests", r.MissingTests);

        if (inv.Tests is { } tests)
        {
            sb.AppendLine();
            sb.AppendLine("## Test execution");
            sb.AppendLine(tests.Executed ? $"Exit code {tests.ExitCode}. {tests.Summary}" : $"Not executed: {tests.Summary}");
            if (tests.OutputFile is not null) sb.AppendLine($"Output: `{tests.OutputFile}`");
        }

        if (inv.Evidence.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Evidence");
            foreach (var e in inv.Evidence) sb.AppendLine($"- [{e.Kind}] {e.Label}{(e.File is null ? "" : $" → `{e.File}`")}{(e.Detail is null ? "" : $" ({e.Detail})")}");
        }
        if (inv.Browser is { } br && (br.ConsoleErrors.Count > 0 || br.NetworkFailures.Count > 0))
        {
            List(sb, "Browser console errors", br.ConsoleErrors.Take(20));
            List(sb, "Network failures", br.NetworkFailures.Take(20));
        }

        if (!string.IsNullOrWhiteSpace(r.OverallAssessment)) Section(sb, "Overall assessment", r.OverallAssessment);
        List(sb, "Limitations", r.Limitations);
        if (r.ProposedJiraAction is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Proposed Jira action (requires your approval):** {r.ProposedJiraAction}");
        }
        if (inv.Publishing.Count > 0)
            List(sb, "Publishing", inv.Publishing.Select(p => $"{p.At:u} {p.Action}: {(p.Success ? "OK" : "FAILED")} {p.Detail}"));
        return sb.ToString();
    }

    /// <summary>The Jira comment as editable plain text with light markup ("## " headings, "- " bullets).</summary>
    public static string JiraCommentText(Investigation inv)
    {
        var r = inv.Report ?? throw new InvalidOperationException("No report to publish.");
        var sb = new StringBuilder();
        sb.AppendLine("## AI Bug Investigation");
        sb.AppendLine($"Outcome: {OutcomeLabel(r.Outcome)} (confidence {r.Confidence:P0})");
        sb.AppendLine("## Investigation summary");
        sb.AppendLine(r.Summary);
        sb.AppendLine("## Expected behaviour");
        sb.AppendLine(Or(r.ExpectedBehavior));
        sb.AppendLine("## Actual behaviour");
        sb.AppendLine(Or(r.ActualBehavior));
        sb.AppendLine("## Reproduction results");
        sb.AppendLine(Or(r.ReproductionResult));
        foreach (var s in inv.Browser?.StepsExecuted ?? []) sb.AppendLine($"- {s}");
        sb.AppendLine($"## Root cause ({(r.RootCauseStatus == "confirmed" ? "confirmed" : "hypothesis — not confirmed")})");
        sb.AppendLine(Or(r.RootCause));
        if (r.CodeReferences.Count > 0)
        {
            sb.AppendLine("## Code references");
            foreach (var c in r.CodeReferences) sb.AppendLine($"- {c} — {c.Note}");
        }
        if (r.Recommendations.Count > 0)
        {
            sb.AppendLine("## Recommendations");
            foreach (var x in r.Recommendations) sb.AppendLine($"- {x}");
        }
        var shots = inv.Evidence.Where(e => e.Kind == "screenshot").ToList();
        sb.AppendLine("## Screenshots and evidence");
        if (shots.Count == 0) sb.AppendLine("No screenshots captured.");
        foreach (var e in shots) sb.AppendLine($"- {Path.GetFileName(e.File)} — {e.Label}");
        var errors = inv.Browser?.ConsoleErrors.Count ?? 0;
        var net = inv.Browser?.NetworkFailures.Count ?? 0;
        if (errors + net > 0) sb.AppendLine($"- {errors} console error(s), {net} network failure(s) recorded");
        if (r.Limitations.Count > 0)
        {
            sb.AppendLine("## Limitations");
            foreach (var x in r.Limitations) sb.AppendLine($"- {x}");
        }
        sb.AppendLine("## Final investigation outcome");
        sb.AppendLine(OutcomeLabel(r.Outcome));
        return sb.ToString().Trim();
    }

    /// <summary>Converts the light markup text to Atlassian Document Format.</summary>
    public static JsonObject TextToAdf(string text)
    {
        var content = new JsonArray();
        JsonArray? list = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (list is null) { list = []; content.Add(new JsonObject { ["type"] = "bulletList", ["content"] = list }); }
                list.Add(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(Paragraph(line[2..])) });
                continue;
            }
            list = null;
            if (line.Length == 0) continue;
            if (line.StartsWith("## ", StringComparison.Ordinal))
                content.Add(new JsonObject { ["type"] = "heading", ["attrs"] = new JsonObject { ["level"] = 3 }, ["content"] = new JsonArray(TextNode(line[3..])) });
            else
                content.Add(Paragraph(line));
        }
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = content };
    }

    private static JsonObject Paragraph(string text) => new() { ["type"] = "paragraph", ["content"] = new JsonArray(TextNode(text)) };
    private static JsonObject TextNode(string text) => new() { ["type"] = "text", ["text"] = text.Length == 0 ? " " : text };

    public static string PrCommentMarkdown(Investigation inv)
    {
        var r = inv.Report ?? throw new InvalidOperationException("No report to publish.");
        var sb = new StringBuilder();
        sb.AppendLine($"**[AI investigation]** Verdict (advisory): **{r.PrVerdict}**");
        sb.AppendLine();
        sb.AppendLine(r.Summary);
        if (r.Findings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| # | Finding | Severity | File | Lines |");
            sb.AppendLine("|---|---|---|---|---|");
            var i = 1;
            foreach (var f in r.Findings)
                sb.AppendLine($"| {i++} | {Cell(f.Title)}: {Cell(f.Detail)} | {f.Severity} | `{f.Reference?.Path}` | {f.Reference?.StartLine}{(f.Reference?.EndLine is { } e && e != f.Reference.StartLine ? $"–{e}" : "")} |");
        }
        if (r.MissingTests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Missing tests:**");
            foreach (var m in r.MissingTests) sb.AppendLine($"- {m}");
        }
        if (inv.Tests is { Executed: true } t)
        {
            sb.AppendLine();
            sb.AppendLine($"**Tests:** {t.Summary}");
        }
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        sb.AppendLine();
        sb.AppendLine($"## {title}");
        sb.AppendLine(body.Trim());
    }

    private static void List(StringBuilder sb, string title, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"## {title}");
        foreach (var i in list) sb.AppendLine($"- {i}");
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");
    private static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "Not determined." : s;
}
