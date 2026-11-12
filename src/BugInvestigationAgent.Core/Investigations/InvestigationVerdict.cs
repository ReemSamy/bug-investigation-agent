using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Workflow;

namespace BugInvestigationAgent.Core.Investigations;

/// <summary>
/// One plain-language sentence saying what the investigation concluded about the bug and on what basis — shown first
/// in the UI, the record and the report. Tooling details (LLM, version detection) are never the headline; they are
/// reported separately by <see cref="CodeAnalysisStatus"/>. Never claims more than the evidence supports.
/// </summary>
public static class InvestigationVerdict
{
    /// <summary>The single Bug verdict. Blocked = the investigation could not run; Inconclusive = it ran but the evidence
    /// neither proves the fix nor shows the defect (including "most likely fixed").</summary>
    public static BugVerdict Classify(InvestigationAssessment a, RuntimeContext ctx)
    {
        if (a.ExecutionStatus is InvestigationExecutionStatus.Failed or InvestigationExecutionStatus.Blocked ||
            ctx.ReproductionOutcome is ReproductionOutcome.Blocked or ReproductionOutcome.Error or ReproductionOutcome.EnvironmentUnavailable)
            return BugVerdict.Blocked;
        return a.Assessment switch
        {
            BugAssessment.StillExists => BugVerdict.StillReproducible,
            BugAssessment.ConfirmedFixed => BugVerdict.ConfirmedFixed,
            _ => BugVerdict.Inconclusive
        };
    }

    public static string Label(BugVerdict v) => v switch
    {
        BugVerdict.ConfirmedFixed => "Confirmed Fixed",
        BugVerdict.StillReproducible => "Still Reproducible",
        BugVerdict.Blocked => "Blocked",
        _ => "Inconclusive"
    };

    /// <summary>"&lt;Verdict&gt; — &lt;plain reason&gt;". Never claims more than the evidence supports.</summary>
    public static string Build(InvestigationAssessment a, RuntimeContext ctx)
    {
        var env = ctx.SelectedEnvironmentDisplayName ?? ctx.EnvironmentName ?? "the selected environment";
        var on = string.IsNullOrWhiteSpace(ctx.DeployedVersion) ? $"on {env}" : $"on {env} (KMS {ctx.DeployedVersion})";
        var verdict = Classify(a, ctx);
        string reason;

        if (verdict == BugVerdict.Blocked)
            reason = Plain(ctx.ReproductionOutcome is ReproductionOutcome.Blocked or ReproductionOutcome.Error or ReproductionOutcome.EnvironmentUnavailable
                ? ctx.ReproductionNotes ?? a.ExecutionDetail
                : a.ExecutionDetail);
        else if (verdict == BugVerdict.StillReproducible)
            reason = $"the bug appeared {on}." +
                     (string.IsNullOrWhiteSpace(ctx.CreatedBranch)
                         ? (string.IsNullOrWhiteSpace(ctx.BranchNote) ? "" : $" {ctx.BranchNote}")
                         : $" Local branch {ctx.CreatedBranch} was created from {ctx.SourceVersionRef} for the fix.");
        else if (verdict == BugVerdict.ConfirmedFixed)
            reason = $"every reproduction step ran {on} without the defect appearing, and {ctx.SourceVersionRef} contains the fix " +
                     $"({ctx.FixCommitsInVersion.FirstOrDefault()}).";
        else
            reason = a.Assessment switch
            {
                BugAssessment.PossiblyFixed when InvestigationAssessor.FixVersionIncluded(ctx.FixVersion, ctx.DeployedVersion) == false =>
                    $"the fix is not deployed here: the bug was not reproduced {on}, but the ticket's fix version {ctx.FixVersion} is newer than the deployed version.",
                BugAssessment.PossiblyFixed =>
                    $"most likely fixed: the bug was not reproduced {on}. " +
                    $"{(InvestigationAssessor.IsResolved(ctx) ? InvestigationAssessor.ResolvedTicketReason(ctx) : a.AssessmentReason)} {NotProvenBecause(ctx)}".Trim(),
                BugAssessment.PotentialDuplicate => $"possible duplicate: {a.AssessmentReason} A person should compare the tickets.",
                BugAssessment.NeedsMoreInformation =>
                    ctx.CustomFields.TryGetValue("reproductionConclusion", out var missing) && ctx.CustomFields.ContainsKey("insufficientInformation")
                        ? $"not enough information: {missing}"
                        : "not enough information: the ticket has no clear reproduction steps or expected result.",
                _ when ctx.ReproductionOutcome == ReproductionOutcome.NotReproduced =>
                    $"not reproduced {on}, but there is no fix evidence (resolved ticket, fix commit or linked fix), so it is not confirmed fixed.",
                _ => $"could not verify: {MainObstacle(ctx)}"
            };

        return $"{Label(verdict)} — {reason}".Trim();
    }

    /// <summary>
    /// Sufficient = a definite verdict backed by evidence; Partial = inconclusive but with real evidence (screenshots of
    /// the followed steps plus fix evidence or a completed reproduction); Insufficient = blocked or nothing to go on.
    /// </summary>
    public static EvidenceSufficiency Sufficiency(BugVerdict verdict, RuntimeContext ctx)
    {
        if (verdict is BugVerdict.ConfirmedFixed or BugVerdict.StillReproducible)
            return EvidenceSufficiency.Sufficient;
        if (verdict == BugVerdict.Blocked)
            return EvidenceSufficiency.Insufficient;
        var screenshots = ctx.Evidence.Count(e => e.Kind == "screenshot");
        var fixEvidence = InvestigationAssessor.IsResolved(ctx) || ctx.FixCommitsInVersion.Count > 0 || !string.IsNullOrWhiteSpace(ctx.FixPullRequestUrl);
        return screenshots > 2 && (fixEvidence || ctx.ReproductionOutcome == ReproductionOutcome.NotReproduced)
            ? EvidenceSufficiency.Partial
            : EvidenceSufficiency.Insufficient;
    }

    /// <summary>Whether code was analysed, and if not, the real reason (one line, separate from the verdict).</summary>
    public static string CodeAnalysisStatus(RuntimeContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.RootCause) && !ctx.CustomFields.ContainsKey("investigationBlockedNoLlm") && !ctx.CustomFields.ContainsKey("codeAnalysisSkipped"))
            return $"Code analysis: done on {ctx.SourceVersionRef ?? "the repository"} (root cause is a hypothesis unless a fix was verified).";
        if (string.IsNullOrWhiteSpace(ctx.DeployedVersion))
            return "Code analysis: not run — the deployed KMS version could not be identified, so the matching source branch is unknown.";
        if (string.IsNullOrWhiteSpace(ctx.SourceVersionRef))
            return $"Code analysis: not run — no source branch matching KMS {ctx.DeployedVersion} was found in the local clone.";
        if (ctx.CustomFields.ContainsKey("investigationBlockedNoLlm"))
            return $"Code analysis: not run on {ctx.SourceVersionRef} — no approved LLM is configured (Sign in / Account → AI provider).";
        return $"Code analysis: not run on {ctx.SourceVersionRef}.";
    }

    private static string NotProvenBecause(RuntimeContext ctx)
    {
        if (ctx.CustomFields.TryGetValue("manualStepsRemaining", out var m) && m != "0")
            return $"Not fully proven: {m} reproduction step(s) need a person (data entry, waiting or e-mail).";
        if (ctx.CustomFields.TryGetValue("failedNavSteps", out var f) && f != "0")
            return "Not fully proven: the agent could not reach the screen where the bug appears.";
        if (ctx.FixCommitsInVersion.Count == 0 && !string.IsNullOrWhiteSpace(ctx.SourceVersionRef))
            return $"Not fully proven: no commit in {ctx.SourceVersionRef} mentions the ticket.";
        return "";
    }

    private static string MainObstacle(RuntimeContext ctx)
    {
        if (ctx.CustomFields.TryGetValue("reproductionConclusion", out var conclusion) && !string.IsNullOrWhiteSpace(conclusion))
            return conclusion.TrimEnd('.') + ".";
        if (ctx.CustomFields.TryGetValue("failedNavSteps", out var f) && f != "0")
            return "the agent could not reach the screen where the bug appears (some ticket steps could not be followed).";
        if (ctx.CustomFields.TryGetValue("manualStepsRemaining", out var m) && m != "0")
            return $"{m} reproduction step(s) need a person (data entry, waiting or e-mail checks).";
        if (ctx.ReproductionSteps.Count == 0)
            return "the ticket has no reproduction steps.";
        return ctx.ReproductionOutcome switch
        {
            ReproductionOutcome.Ambiguous => "the steps ran but the defect neither appeared nor was clearly absent.",
            ReproductionOutcome.NotStarted => "reproduction was not attempted.",
            _ => $"reproduction could not run ({ctx.ReproductionOutcome})."
        };
    }

    private static string Plain(string? s) => string.IsNullOrWhiteSpace(s) ? "see the report." : s.Trim();
}
