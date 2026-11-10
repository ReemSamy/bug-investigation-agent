using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Reporting;
using BugInvestigationAgent.Core.Workflow;

namespace BugInvestigationAgent.Core.Investigations;

public sealed record InvestigationAssessment(
    InvestigationExecutionStatus ExecutionStatus,
    string ExecutionDetail,
    ReproductionResult Reproduction,
    BugAssessment Assessment,
    string AssessmentReason,
    IReadOnlyList<string> Flags,
    string RecommendedAction,
    bool RootCauseConfirmed,
    IReadOnlyList<string> DuplicateCandidates);

/// <summary>
/// Turns one finished investigation into an execution status and ONE primary bug assessment, using only evidence
/// the run produced. Conservative by design: "not reproduced" never means "fixed", and every non-evidence signal
/// (age, wording, priority) only ever leads to a "potentially …" category for human review.
/// </summary>
public static class InvestigationAssessor
{
    private static readonly string[] HighPriorities = ["Highest", "High", "Critical", "Blocker", "Urgent"];
    private static readonly Regex HighImpactWording = new(
        @"(?i)\b(data\s+loss|lost\s+data|security|vulnerab\w*|crash\w*|outage|corrupt\w*|cannot\s+log\s*in|can't\s+log\s*in|all\s+users)\b",
        RegexOptions.Compiled);
    private static readonly string[] DoneStatuses = ["Done", "Closed", "Resolved", "Dev Done"];

    public static InvestigationAssessment Assess(
        AgentState finalState, RuntimeContext ctx, InvestigationReport report, JiraIssueSnapshot? issue, DateTimeOffset now)
    {
        var screenshots = ctx.Evidence.Count(e => e.Kind == "screenshot" && !string.IsNullOrWhiteSpace(e.Path));
        var reproduced = ctx.ReproductionOutcome == ReproductionOutcome.Reproduced && screenshots > 0;
        var hasRootCause = !string.IsNullOrWhiteSpace(ctx.RootCause) && !(ctx.CustomFields.ContainsKey("investigationBlockedNoLlm") || ctx.CustomFields.ContainsKey("codeAnalysisSkipped"));

        var (status, detail) = finalState switch
        {
            AgentState.Failed => (InvestigationExecutionStatus.Failed, ctx.FinalStatusMessage ?? LastLog(ctx) ?? "The investigation failed."),
            AgentState.Blocked or AgentState.AwaitingMissingInfo =>
                (InvestigationExecutionStatus.Blocked, ctx.MissingInfoReason ?? ctx.FinalStatusMessage ?? LastLog(ctx) ?? "Blocked: required information was not available."),
            AgentState.Completed when reproduced || hasRootCause =>
                (InvestigationExecutionStatus.Completed, ctx.FinalStatusMessage ?? "Completed."),
            AgentState.Completed =>
                (InvestigationExecutionStatus.Inconclusive, (ctx.FinalStatusMessage ?? "Completed") + " — no reproduction evidence and no code-analysis result."),
            _ => (InvestigationExecutionStatus.Blocked, $"The run ended before completion (state: {finalState}).")
        };

        var reproduction = ctx.ReproductionOutcome switch
        {
            ReproductionOutcome.Reproduced when reproduced => ReproductionResult.Reproduced,
            ReproductionOutcome.Reproduced => ReproductionResult.Inconclusive, // signal without evidence
            ReproductionOutcome.NotReproduced => ReproductionResult.NotReproduced,
            ReproductionOutcome.Ambiguous => ReproductionResult.Inconclusive,
            ReproductionOutcome.Blocked or ReproductionOutcome.EnvironmentUnavailable or ReproductionOutcome.Error => ReproductionResult.Blocked,
            _ => ReproductionResult.NotAttempted
        };

        // ── Signals ──
        var duplicates = ctx.Investigation.SimilarIssues
            .Where(s => !string.Equals(s.Key, ctx.JiraTicketKey, StringComparison.OrdinalIgnoreCase) &&
                        ((s.RankedByLlm && s.SameUnderlyingProblem) || s.RelevanceScore >= 0.85))
            .Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var fixedRelated = ctx.Investigation.SimilarIssues
            .Where(s => s.RankedByLlm && s.SameUnderlyingProblem && DoneStatuses.Contains(s.Status ?? "", StringComparer.OrdinalIgnoreCase))
            .Select(s => s.Key).ToList();
        // Confirmed Fixed needs BOTH: every reproduction step ran automatically without showing the defect, AND a commit in the
        // branch of the deployed version mentions this ticket (or a resolved linked issue).
        var allStepsAutomated = ctx.ReproductionSteps.Count > 0 &&
                                (!ctx.CustomFields.TryGetValue("manualStepsRemaining", out var manual) || manual == "0") &&
                                (!ctx.CustomFields.TryGetValue("failedNavSteps", out var failedNav) || failedNav == "0");
        var confirmedFixed = ctx.ReproductionOutcome == ReproductionOutcome.NotReproduced && allStepsAutomated && ctx.FixCommitsInVersion.Count > 0;
        var possiblyFixed = !reproduced && !confirmedFixed &&
                            (!string.IsNullOrWhiteSpace(ctx.FixPullRequestUrl) || fixedRelated.Count > 0 ||
                             ctx.FixCommitsInVersion.Count > 0 || ctx.ResolvedLinkedIssueKeys.Count > 0);
        var missingInfo = !ctx.CustomFields.ContainsKey("stepsFromJira") && (ctx.Description ?? "").Trim().Length < 80;
        var priority = issue?.Priority ?? (ctx.CustomFields.TryGetValue("priority", out var p) ? p : null);
        var critical = HighPriorities.Contains(priority ?? "", StringComparer.OrdinalIgnoreCase) ||
                       HighImpactWording.IsMatch($"{ctx.Summary}\n{ctx.Description}");
        var obsolete = !reproduced && issue?.Created is { } created && now - created > TimeSpan.FromDays(3 * 365);

        var signals = new List<(BugAssessment Category, bool On, string Reason)>
        {
            (BugAssessment.StillExists, reproduced, "Reproduced with screenshot evidence on the tested environment" +
                (string.IsNullOrWhiteSpace(ctx.DeployedVersion) ? "." : $" (KMS {ctx.DeployedVersion}).")),
            (BugAssessment.ConfirmedFixed, confirmedFixed,
                $"All reproduction steps ran without showing the defect on KMS {ctx.DeployedVersion}, and {ctx.SourceVersionRef} contains: {string.Join("; ", ctx.FixCommitsInVersion.Take(2))}."),
            (BugAssessment.PotentialDuplicate, duplicates.Count > 0, $"Closely matches {string.Join(", ", duplicates)}."),
            (BugAssessment.PossiblyFixed, possiblyFixed,
                !string.IsNullOrWhiteSpace(ctx.FixPullRequestUrl)
                    ? $"The ticket links a fix ({ctx.FixPullRequestUrl}) and the bug was not reproduced."
                    : ctx.FixCommitsInVersion.Count > 0
                        ? $"{ctx.SourceVersionRef} contains a commit mentioning the ticket, but reproduction did not fully confirm the fix."
                        : ctx.ResolvedLinkedIssueKeys.Count > 0
                            ? $"Linked resolved issue(s) {string.Join(", ", ctx.ResolvedLinkedIssueKeys)} may contain the fix; not verified."
                            : $"A resolved issue with the same underlying problem exists ({string.Join(", ", fixedRelated)}) and the bug was not reproduced."),
            (BugAssessment.NeedsMoreInformation, missingInfo && !reproduced, "The ticket has no reproduction steps and only a short description."),
            (BugAssessment.CriticalCandidate, critical, $"High-impact signal (priority: {priority ?? "—"}, or wording such as crash / data loss / security)."),
            (BugAssessment.PotentiallyObsolete, obsolete, $"Created {issue?.Created:yyyy-MM-dd} (over 3 years ago) and not reproduced.")
        };

        var primary = status == InvestigationExecutionStatus.Failed ? default : signals.FirstOrDefault(s => s.On);
        var assessment = primary.On ? primary.Category : BugAssessment.Undetermined;
        var reason = primary.On
            ? primary.Reason
            : status == InvestigationExecutionStatus.Failed
                ? "The investigation failed; no assessment."
                : "The available evidence does not support any category.";
        var flags = signals.Where(s => s.On && s.Category != assessment).Select(s => s.Category.ToString()).ToList();

        var action = assessment switch
        {
            BugAssessment.StillExists => string.IsNullOrWhiteSpace(ctx.CreatedBranch)
                ? "Still reproducible: plan a fix; evidence and the recommended fix are in the report. " + (ctx.BranchNote ?? "")
                : $"Still reproducible: work on local branch {ctx.CreatedBranch} (created from {ctx.SourceVersionRef}); the recommended fix is in the report.",
            BugAssessment.ConfirmedFixed => "Fixed in the deployed version (evidence in the report). A person may close the ticket — the tool does not change Jira.",
            BugAssessment.PotentialDuplicate => $"Compare with {string.Join(", ", duplicates)}; if a person confirms it, link it as a duplicate.",
            BugAssessment.PossiblyFixed => "Verify on an environment that contains the fix before closing — not reproducing it is not proof it is fixed.",
            BugAssessment.NeedsMoreInformation => "Ask the reporter for reproduction steps, the environment and the expected result.",
            BugAssessment.CriticalCandidate => "Bring to the team for review soon: high-impact signals.",
            BugAssessment.PotentiallyObsolete => "Ask the product owner whether this is still relevant (human decision).",
            _ => "Requires further investigation."
        };
        if (status is InvestigationExecutionStatus.Blocked or InvestigationExecutionStatus.Failed)
            action = $"Resolve the problem ({Short(detail, 160)}) and run the investigation again. " + action;

        return new InvestigationAssessment(status, detail, reproduction, assessment, reason, flags, action,
            RootCauseConfirmed: hasRootCause && ctx.FixApproved && ctx.RetestPassed, duplicates);
    }

    public static string Label(BugAssessment a) => a switch
    {
        BugAssessment.StillExists => "Still Exists",
        BugAssessment.ConfirmedFixed => "Confirmed Fixed",
        BugAssessment.PossiblyFixed => "Possibly Fixed",
        BugAssessment.PotentialDuplicate => "Potential Duplicate",
        BugAssessment.PotentiallyObsolete => "Potentially Obsolete",
        BugAssessment.CriticalCandidate => "Critical Candidate",
        BugAssessment.NeedsMoreInformation => "Needs More Information",
        _ => "Undetermined"
    };

    public static string Label(ReproductionResult r) => r switch
    {
        ReproductionResult.Reproduced => "Reproduced",
        ReproductionResult.NotReproduced => "Not Reproduced",
        ReproductionResult.Inconclusive => "Inconclusive",
        ReproductionResult.Blocked => "Blocked",
        _ => "Not Attempted"
    };

    private static string? LastLog(RuntimeContext ctx) => ctx.StatusLog.SafeSnapshot().LastOrDefault();

    internal static string Short(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Length <= max ? s.Trim() : s[..(max - 1)].Trim() + "…";
}
