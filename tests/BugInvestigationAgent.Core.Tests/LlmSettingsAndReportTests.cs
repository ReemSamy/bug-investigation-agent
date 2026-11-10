using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Reporting;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Workflow;

namespace BugInvestigationAgent.Core.Tests;

public class LlmSettingsAndReportTests
{
    private sealed class MutableSettings : ILlmSettingsSource
    {
        public string ActiveProviderId { get; set; } = "null";
        public LlmProviderOptions Current { get; set; } = new();
        public bool ExternalDataSharingApproved { get; set; } = true;
    }

    private sealed class NamedRuntime(string id) : IAgentRuntime
    {
        public string ProviderId => id;
        public Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentTurnResult { Content = id });
        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentTurnRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }

    [Test]
    public void Resolver_follows_the_provider_chosen_in_settings_on_every_call()
    {
        var settings = new MutableSettings();
        var resolver = new AgentRuntimeResolver(new IAgentRuntime[] { new NamedRuntime("null"), new NamedRuntime("anthropic") }, settings);

        Assert.That(resolver.GetCurrent().ProviderId, Is.EqualTo("null"));
        settings.ActiveProviderId = "anthropic"; // user saves Settings — no restart
        Assert.That(resolver.GetCurrent().ProviderId, Is.EqualTo("anthropic"));
    }

    private static RuntimeContext Context(ReproductionOutcome outcome) => new()
    {
        JiraTicketKey = "VKMS-5102",
        Summary = "Badgermail does not re-trigger",
        ReproductionOutcome = outcome
    };

    [Test]
    public void Not_reproduced_with_code_analysis_is_a_medium_confidence_hypothesis_with_next_steps()
    {
        var ctx = Context(ReproductionOutcome.Ambiguous);
        ctx.RootCause = "Alert is not re-queued when the expiration date changes.";
        ctx.CustomFields["manualStepsRemaining"] = "5";
        ctx.Investigation.SimilarIssues.Add(new SimilarIssueCandidate { Key = "VKMS-5101", Summary = "Badgermail spamming", RelevanceScore = 0.5 });

        var report = InvestigationReportBuilder.Build(ctx, new SecretRedactor());
        var md = InvestigationReportBuilder.ToMarkdown(report);

        Assert.That(report.Confidence, Is.EqualTo("Medium"));
        Assert.That(report.ConfirmedFindings, Has.None.Contains("Alert is not re-queued"), "a hypothesis is never a confirmed finding");
        Assert.That(report.Hypotheses, Has.Some.Contains("Probable root cause"));
        Assert.That(report.Recommendations, Has.Some.Contains("Validate the root-cause hypothesis manually").And.Some.Contains("5 ticket step(s) need a person"));
        Assert.That(report.SimilarIssues.Single(), Does.StartWith("VKMS-5101").And.Contain("keyword match only"));
        Assert.That(md, Does.Contain("Confidence: **Medium**").And.Contain("Ticket: Badgermail does not re-trigger"));
    }

    [Test]
    public void Missing_llm_is_reported_as_a_next_step_not_as_a_root_cause()
    {
        var ctx = Context(ReproductionOutcome.NotReproduced);
        ctx.RootCause = "Investigation paused: no real LLM provider is configured.";
        ctx.CustomFields["investigationBlockedNoLlm"] = "true";

        var report = InvestigationReportBuilder.Build(ctx, new SecretRedactor());

        Assert.That(report.Confidence, Is.EqualTo("Low"));
        Assert.That(report.Hypotheses, Has.None.Contains("Probable root cause"));
        Assert.That(report.Recommendations, Has.Some.Contains("configure an approved LLM provider"));
    }
}
