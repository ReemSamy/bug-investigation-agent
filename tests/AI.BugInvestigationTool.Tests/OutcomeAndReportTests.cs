using System.Text.Json;
using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services.Workflow;

namespace AI.BugInvestigationTool.Tests;

public class OutcomeClassifierTests
{
    private static BrowserResult Browser(string reproduced, bool expectedSeen = false, bool login = true, string? error = null) =>
        new() { Attempted = true, LoginSucceeded = login, Reproduced = reproduced, ExpectedBehaviorObserved = expectedSeen, Error = error };

    [Theory]
    // Never RESOLVED just because the bug did not reproduce.
    [InlineData(InvestigationOutcome.Resolved, "no", false, 0.9, InvestigationOutcome.NotReproducible)]
    [InlineData(InvestigationOutcome.Resolved, "unclear", false, 0.9, InvestigationOutcome.Inconclusive)]
    [InlineData(InvestigationOutcome.Resolved, "no", true, 0.9, InvestigationOutcome.Resolved)]
    [InlineData(InvestigationOutcome.Resolved, "no", true, 0.5, InvestigationOutcome.Inconclusive)]
    [InlineData(InvestigationOutcome.Resolved, "yes", true, 0.9, InvestigationOutcome.Unresolved)]
    [InlineData(InvestigationOutcome.Unresolved, "yes", false, 0.9, InvestigationOutcome.Unresolved)]
    [InlineData(InvestigationOutcome.Unresolved, "no", false, 0.9, InvestigationOutcome.Inconclusive)]
    [InlineData(InvestigationOutcome.NotReproducible, "no", false, 0.9, InvestigationOutcome.NotReproducible)]
    [InlineData(InvestigationOutcome.NotReproducible, "yes", false, 0.9, InvestigationOutcome.Unresolved)]
    [InlineData(InvestigationOutcome.NotReproducible, "unclear", false, 0.9, InvestigationOutcome.Inconclusive)]
    [InlineData(InvestigationOutcome.Inconclusive, "yes", false, 0.9, InvestigationOutcome.Inconclusive)]
    public void Applies_evidence_rules(InvestigationOutcome proposed, string reproduced, bool expectedSeen, double confidence, InvestigationOutcome expected)
    {
        var (outcome, _) = OutcomeClassifier.Decide(proposed, Browser(reproduced, expectedSeen), loginRequired: true, confidence);
        Assert.Equal(expected, outcome);
    }

    [Fact]
    public void Untested_or_failed_environment_blocks_definitive_outcomes()
    {
        Assert.Equal(InvestigationOutcome.Blocked, OutcomeClassifier.Decide(InvestigationOutcome.Resolved, null, true, 1).Outcome);
        Assert.Equal(InvestigationOutcome.Blocked, OutcomeClassifier.Decide(InvestigationOutcome.Unresolved, Browser("yes", login: false), true, 1).Outcome);
        Assert.Equal(InvestigationOutcome.Blocked, OutcomeClassifier.Decide(InvestigationOutcome.NotReproducible, Browser("no", error: "crash"), true, 1).Outcome);
        Assert.Equal(InvestigationOutcome.Inconclusive, OutcomeClassifier.Decide(InvestigationOutcome.Inconclusive, null, true, 1).Outcome);
        Assert.Equal(InvestigationOutcome.Unresolved, OutcomeClassifier.Decide(InvestigationOutcome.Unresolved, Browser("yes", login: false), false, 1).Outcome);
    }

    [Fact]
    public void Apply_records_adjustment_and_only_proposes_supported_jira_actions()
    {
        var report = new InvestigationReport { Outcome = InvestigationOutcome.Resolved, Confidence = 0.9, ProposedJiraAction = "Close" };
        OutcomeClassifier.Apply(report, Browser("no"), true);
        Assert.Equal(InvestigationOutcome.NotReproducible, report.Outcome);
        Assert.Equal(InvestigationOutcome.Resolved, report.ProposedOutcome);
        Assert.Null(report.ProposedJiraAction); // never close without positive evidence
        Assert.Contains(report.Limitations, l => l.Contains("Outcome changed"));

        var unresolved = new InvestigationReport { Outcome = InvestigationOutcome.Unresolved, Confidence = 0.9 };
        OutcomeClassifier.Apply(unresolved, Browser("yes"), true);
        Assert.Equal("Reopen", unresolved.ProposedJiraAction);
    }

    [Fact]
    public void Pr_verdict_follows_the_review_decision_matrix()
    {
        Assert.Equal("Approve", OutcomeClassifier.PrVerdict([]));
        Assert.Equal("Approve with suggestions", OutcomeClassifier.PrVerdict([new Finding { Severity = "Low" }, new Finding { Severity = "Medium" }]));
        Assert.Equal("Waiting for author", OutcomeClassifier.PrVerdict([new Finding { Severity = "Low" }, new Finding { Severity = "Critical" }]));
    }
}

public class ReportTests
{
    private static Investigation Sample() => new()
    {
        Id = "x",
        Target = new InvestigationTarget(TargetKind.JiraIssue, "https://acme.atlassian.net/browse/SHOP-42", "https://acme.atlassian.net", "SHOP-42"),
        Step = InvestigationStep.Completed,
        Issue = new JiraIssue { Key = "SHOP-42", Summary = "Save fails" },
        Browser = new BrowserResult { Attempted = true, LoginSucceeded = true, Reproduced = "yes", StepsExecuted = ["Opened Orders", "Clicked Save → error"], ConsoleErrors = ["500"] },
        Evidence = [new EvidenceItem { Kind = "screenshot", Label = "error dialog", File = "screenshots/01-error.png" }],
        Report = new InvestigationReport
        {
            Outcome = InvestigationOutcome.Unresolved, Confidence = 0.85, Summary = "The defect still occurs.",
            ExpectedBehavior = "Order saved", ActualBehavior = "Error shown", ReproductionResult = "Reproduced on QA",
            RootCause = "Null customer dereferenced", RootCauseStatus = "hypothesis",
            CodeReferences = [new CodeReference { Repository = "shop-api", Path = "/src/OrderService.cs", StartLine = 5, EndLine = 7, Note = "dereference" }],
            Recommendations = ["Guard against null Customer in OrderService.Save"], Limitations = ["Only tested in Chrome"], ProposedJiraAction = "Reopen",
        },
    };

    [Fact]
    public void Jira_comment_contains_all_required_sections()
    {
        var text = ReportFormatter.JiraCommentText(Sample());
        foreach (var section in new[] { "Investigation summary", "Expected behaviour", "Actual behaviour", "Reproduction results", "Root cause (hypothesis — not confirmed)",
                     "Code references", "Recommendations", "Screenshots and evidence", "Final investigation outcome" })
            Assert.Contains("## " + section, text);
        Assert.Contains("shop-api:/src/OrderService.cs L5-7", text);
        Assert.Contains("01-error.png — error dialog", text);
        Assert.EndsWith("UNRESOLVED", text);
    }

    [Fact]
    public void Comment_text_converts_to_valid_adf()
    {
        var adf = ReportFormatter.TextToAdf("## Heading\nParagraph\n- one\n- two\n\nAfter");
        var json = JsonDocument.Parse(adf.ToJsonString()).RootElement;
        Assert.Equal("doc", json.GetProperty("type").GetString());
        var types = json.GetProperty("content").EnumerateArray().Select(n => n.GetProperty("type").GetString()).ToList();
        Assert.Equal(["heading", "paragraph", "bulletList", "paragraph"], types);
        Assert.Equal(2, json.GetProperty("content")[2].GetProperty("content").GetArrayLength());
    }

    [Fact]
    public void Markdown_report_shows_outcome_and_proposed_action()
    {
        var md = ReportFormatter.ToMarkdown(Sample());
        Assert.Contains("## Outcome: **UNRESOLVED** (confidence 85", md);
        Assert.Contains("Proposed Jira action (requires your approval):** Reopen", md);
        Assert.Contains("## Root cause (hypothesis)", md);
    }
}
