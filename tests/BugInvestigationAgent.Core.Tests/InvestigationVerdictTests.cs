using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Investigations;
using BugInvestigationAgent.Core.Reporting;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Workflow;

namespace BugInvestigationAgent.Core.Tests;

/// <summary>The headline must say what was concluded about the bug and why — never an LLM/tooling message.</summary>
public class InvestigationVerdictTests
{
    // Shape of the real VKMS-4183 run: ticket Done with fix version 14.0.0, environment on 14.1.0, steps not completed.
    private static RuntimeContext SolvedTicket(string? deployed = "14.1.0", string fixVersion = "14.0.0")
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "VKMS-4183",
            Summary = "Auditor Proposal: plain Save shows misleading \"sent for approval\" confirmation message",
            Description = new string('x', 200),
            JiraStatus = "Done",
            FixVersion = fixVersion,
            JiraResolvedAt = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero),
            DeployedVersion = deployed,
            SourceVersionRef = deployed is null ? null : "origin/release/release-v14.1.0",
            SelectedEnvironmentDisplayName = "kms14-portal (release, non-production)",
            ReproductionOutcome = ReproductionOutcome.Ambiguous,
            ReproductionSteps = new List<string> { "Open Modules menu", "Open module 'Auditor'" }
        };
        ctx.CustomFields["stepsFromJira"] = "true";
        ctx.CustomFields["failedNavSteps"] = "6";
        ctx.CustomFields["investigationBlockedNoLlm"] = "true";
        return ctx;
    }

    private static (InvestigationAssessment A, string Verdict) Run(RuntimeContext ctx)
    {
        var a = InvestigationAssessor.Assess(AgentState.Completed, ctx, InvestigationReportBuilder.Build(ctx, new SecretRedactor()), null, DateTimeOffset.UtcNow);
        return (a, InvestigationVerdict.Build(a, ctx));
    }

    [Test]
    public void Solved_ticket_not_reproduced_reads_as_most_likely_fixed_with_the_basis_and_no_llm_wording()
    {
        var (a, verdict) = Run(SolvedTicket());

        Assert.That(a.Assessment, Is.EqualTo(BugAssessment.PossiblyFixed));
        Assert.That(verdict, Does.StartWith("Inconclusive — most likely fixed: the bug was not reproduced on kms14-portal (release, non-production) (KMS 14.1.0)."));
        Assert.That(verdict, Does.Contain("The ticket is resolved (Done, on 2026-09-17)").And.Contain("fix version 14.0.0 is included in the deployed KMS 14.1.0"));
        Assert.That(verdict, Does.Contain("Not fully proven: the agent could not reach the screen where the bug appears."));
        Assert.That(verdict, Does.Not.Contain("LLM"));
    }

    [Test]
    public void Code_analysis_status_is_a_separate_line_with_the_real_reason()
    {
        Assert.That(InvestigationVerdict.CodeAnalysisStatus(SolvedTicket()), Does.Contain("no approved LLM is configured"));
        Assert.That(InvestigationVerdict.CodeAnalysisStatus(SolvedTicket(deployed: null)), Does.Contain("deployed KMS version could not be identified").And.Not.Contain("LLM"));
    }

    [Test]
    public void Fix_version_newer_than_the_deployed_version_is_not_called_fixed()
    {
        var (_, verdict) = Run(SolvedTicket(deployed: "14.0.0", fixVersion: "14.2.0"));
        Assert.That(verdict, Does.StartWith("Inconclusive — the fix is not deployed here").And.Contain("newer than the deployed version"));
    }

    [Test]
    public void Unresolved_ticket_that_could_not_be_followed_says_what_blocked_it()
    {
        var ctx = SolvedTicket();
        ctx.JiraStatus = "Backlog";
        ctx.FixVersion = null;
        var (a, verdict) = Run(ctx);
        Assert.That(a.Assessment, Is.EqualTo(BugAssessment.Undetermined));
        Assert.That(verdict, Is.EqualTo("Inconclusive — could not verify: the agent could not reach the screen where the bug appears (some ticket steps could not be followed)."));
    }

    [Test]
    public void Reproduced_bug_says_still_reproducible_with_the_branch()
    {
        var ctx = SolvedTicket();
        ctx.ReproductionOutcome = ReproductionOutcome.Reproduced;
        ctx.Evidence.Add(new EvidenceItem { Kind = "screenshot", Path = "C:/e/defect.png" });
        ctx.CreatedBranch = "bugfix/VKMS-4183";
        var (_, verdict) = Run(ctx);
        Assert.That(verdict, Does.StartWith("Still Reproducible — the bug appeared on kms14-portal").And.Contain("bugfix/VKMS-4183"));
    }

    [TestCase("14.0.0", "14.1.0", true)]
    [TestCase("14.1.0", "14.1", true)]
    [TestCase("14.2.0", "14.1.0", false)]
    [TestCase("12.28.6, 14.0.0", "14.0.0", true)]
    [TestCase("Future", "14.1.0", null)]
    [TestCase("14.0.0", null, null)]
    public void Fix_version_inclusion(string? fix, string? deployed, bool? expected) =>
        Assert.That(InvestigationAssessor.FixVersionIncluded(fix, deployed), Is.EqualTo(expected));

    [Test]
    public void Every_verdict_starts_with_exactly_one_of_the_four_labels()
    {
        var labels = new[] { "Confirmed Fixed — ", "Still Reproducible — ", "Inconclusive — ", "Blocked — " };
        foreach (var outcome in Enum.GetValues<ReproductionOutcome>())
        {
            var ctx = SolvedTicket();
            ctx.ReproductionOutcome = outcome;
            var (_, verdict) = Run(ctx);
            Assert.That(labels.Count(verdict.StartsWith), Is.EqualTo(1), $"{outcome}: {verdict}");
        }
    }

    [Test]
    public void Unreachable_environment_is_blocked_with_insufficient_evidence()
    {
        var ctx = SolvedTicket();
        ctx.ReproductionOutcome = ReproductionOutcome.EnvironmentUnavailable;
        ctx.ReproductionNotes = "The environment did not respond.";
        var (a, verdict) = Run(ctx);
        Assert.That(InvestigationVerdict.Classify(a, ctx), Is.EqualTo(BugVerdict.Blocked));
        Assert.That(verdict, Does.StartWith("Blocked — "));
        Assert.That(InvestigationVerdict.Sufficiency(BugVerdict.Blocked, ctx), Is.EqualTo(EvidenceSufficiency.Insufficient));
    }

    [Test]
    public void Most_likely_fixed_is_inconclusive_never_confirmed_fixed()
    {
        var ctx = SolvedTicket();
        var (a, _) = Run(ctx);
        Assert.That(a.Assessment, Is.EqualTo(BugAssessment.PossiblyFixed));
        Assert.That(InvestigationVerdict.Classify(a, ctx), Is.EqualTo(BugVerdict.Inconclusive));
        Assert.That(InvestigationVerdict.Sufficiency(BugVerdict.StillReproducible, ctx), Is.EqualTo(EvidenceSufficiency.Sufficient));
    }
}
