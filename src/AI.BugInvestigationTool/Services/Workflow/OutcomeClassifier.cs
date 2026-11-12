using AI.BugInvestigationTool.Models;

namespace AI.BugInvestigationTool.Services.Workflow;

/// <summary>
/// Evidence rules applied after Claude proposes an outcome. Claude's view is kept, but an outcome is only
/// accepted when the browser evidence supports it — in particular a bug is never RESOLVED just because it
/// could not be reproduced.
/// </summary>
public static class OutcomeClassifier
{
    public const double ResolvedMinConfidence = 0.7;

    public static void Apply(InvestigationReport report, BrowserResult? browser, bool loginRequired)
    {
        var proposed = report.Outcome;
        var (final, reason) = Decide(proposed, browser, loginRequired, report.Confidence);
        if (final != proposed)
        {
            report.ProposedOutcome = proposed;
            report.OutcomeAdjustmentReason = reason;
            report.Outcome = final;
            report.Limitations.Add($"Outcome changed from {proposed} to {final}: {reason}");
        }

        // Only propose Jira transitions that the evidence supports; never close inconclusive/blocked work.
        report.ProposedJiraAction = report.Outcome switch
        {
            InvestigationOutcome.Resolved => NormalizeAction(report.ProposedJiraAction, "Close"),
            InvestigationOutcome.Unresolved => NormalizeAction(report.ProposedJiraAction, "Reopen"),
            _ => null,
        };
    }

    public static (InvestigationOutcome Outcome, string Reason) Decide(InvestigationOutcome proposed, BrowserResult? browser, bool loginRequired, double confidence)
    {
        var browserUsable = browser is { Attempted: true, Error: null } && (!loginRequired || browser.LoginSucceeded);
        if (!browserUsable)
        {
            var why = browser is null || !browser.Attempted ? "the environment was not tested"
                : browser.Error is not null ? $"the browser investigation failed ({browser.Error})"
                : "login to the environment failed";
            return proposed switch
            {
                InvestigationOutcome.Inconclusive => (proposed, ""),
                InvestigationOutcome.Blocked => (proposed, ""),
                _ => (InvestigationOutcome.Blocked, $"{why}, so the reported behaviour could not be verified"),
            };
        }

        var reproduced = browser!.Reproduced;
        return proposed switch
        {
            InvestigationOutcome.Resolved when reproduced == "yes" =>
                (InvestigationOutcome.Unresolved, "the issue was reproduced in the environment"),
            InvestigationOutcome.Resolved when !browser.ExpectedBehaviorObserved =>
                (reproduced == "no" ? InvestigationOutcome.NotReproducible : InvestigationOutcome.Inconclusive,
                 "the expected behaviour was not positively observed; failing to reproduce is not proof of a fix"),
            InvestigationOutcome.Resolved when confidence < ResolvedMinConfidence =>
                (InvestigationOutcome.Inconclusive, $"confidence {confidence:0.00} is below {ResolvedMinConfidence:0.0}"),
            InvestigationOutcome.NotReproducible when reproduced == "yes" =>
                (InvestigationOutcome.Unresolved, "the issue was reproduced in the environment"),
            InvestigationOutcome.NotReproducible when reproduced != "no" =>
                (InvestigationOutcome.Inconclusive, "the reproduction attempt was unclear"),
            InvestigationOutcome.Unresolved when reproduced != "yes" =>
                (InvestigationOutcome.Inconclusive, "the issue was not reproduced, so it cannot be confirmed as unresolved"),
            _ => (proposed, ""),
        };
    }

    private static string NormalizeAction(string? proposed, string fallback) =>
        string.IsNullOrWhiteSpace(proposed) || proposed.Equals("none", StringComparison.OrdinalIgnoreCase) ? fallback : proposed;

    /// <summary>PR advisory verdict, matching the shared review-pr decision matrix.</summary>
    public static string PrVerdict(IEnumerable<Finding> findings)
    {
        var severities = findings.Select(f => f.Severity).ToList();
        if (severities.Contains("Critical")) return "Waiting for author";
        return severities.Count > 0 ? "Approve with suggestions" : "Approve";
    }
}
