using System.Text;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Skills.Reproduction;

namespace BugInvestigationAgent.Skills.Evidence;

public sealed class EvidenceReportingSkill : ISkill
{
    private readonly ISecretRedactor _redactor;

    public EvidenceReportingSkill(ISecretRedactor redactor) => _redactor = redactor;

    public string SkillId => SkillIds.EvidenceReporting;
    public string DisplayName => "Evidence Reporting";
    public AgentPermission RequiredPermissions => AgentPermission.None;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) => true;

    public Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        var summary = BuildHumanReadableSummary(ctx);
        ctx.Evidence.Add(new EvidenceItem
        {
            Kind = "summary",
            Caption = summary
        });

        // Keep a short machine log too (not the primary UI text).
        var sb = new StringBuilder();
        sb.AppendLine($"Ticket: {ctx.JiraTicketKey}");
        sb.AppendLine($"Outcome: {ctx.ReproductionOutcome}");
        sb.AppendLine($"Screenshots: {ctx.Evidence.Count(e => e.Kind == "screenshot")}");
        foreach (var e in ctx.Evidence.Where(e => e.Kind == "screenshot").Take(20))
            sb.AppendLine($"- {e.Path}");

        ctx.Evidence.Add(new EvidenceItem
        {
            Kind = "report",
            Caption = sb.ToString()
        });

        if (string.IsNullOrWhiteSpace(ctx.FinalStatusMessage) && ctx.ReproductionOutcome == ReproductionOutcome.Reproduced && ctx.RetestPassed)
            ctx.FinalStatusMessage = "LOCAL CHANGES VALIDATED → READY FOR HUMAN REVIEW";

        request.Progress?.Report("Readable evidence summary ready.");
        return Task.FromResult(SkillResult.Ok("Evidence report ready."));
    }

    private string BuildHumanReadableSummary(RuntimeContext ctx)
    {
        var lines = new List<string>();
        var ticket = ctx.JiraTicketKey ?? "this ticket";

        lines.Add($"What happened for {ticket}");
        lines.Add("");

        switch (ctx.ReproductionOutcome)
        {
            case ReproductionOutcome.Ambiguous:
                lines.Add($"Result: Could not reproduce / could not confirm the issue for {ticket}.");
                lines.Add("");
                lines.Add($"Ticket summary: {_redactor.Redact(ctx.Summary)}");
                lines.Add("");
                lines.Add("The agent stayed logged in and tried to open the feature area described in THIS ticket (not a previous ticket).");
                if (ctx.CustomFields.TryGetValue("inferredForTicket", out var inferredKey) &&
                    !string.IsNullOrWhiteSpace(inferredKey))
                    lines.Add($"Navigation steps were inferred specifically for {inferredKey} from its summary/description.");
                if (ctx.ReproductionSteps.Count > 0)
                {
                    lines.Add("Steps used:");
                    foreach (var step in ctx.ReproductionSteps.Take(10))
                        lines.Add("  - " + _redactor.Redact(step));
                }
                lines.Add($"The reported defect was not clearly seen: {TicketStepPlanner.DescribeDefect(ctx.Summary, ctx.Description)}");
                lines.Add("No code investigation or fix was started, because the bug was not clearly reproduced.");
                lines.Add("Review the screenshots. If needed, add clearer Jira steps for this ticket and run Analyze again.");
                break;

            case ReproductionOutcome.NotReproduced:
                lines.Add("Result: Could not reproduce — the reported issue was not found.");
                lines.Add("");
                lines.Add("The agent followed the available steps and did not observe the defect described in the ticket.");
                lines.Add("No fix was applied.");
                break;

            case ReproductionOutcome.Reproduced:
                lines.Add("Result: Bug was reproduced.");
                lines.Add("");
                lines.Add("The agent observed error signals matching the ticket while following the steps.");
                if (!string.IsNullOrWhiteSpace(ctx.RootCause))
                    lines.Add("Root cause: " + _redactor.Redact(ctx.RootCause));
                if (!string.IsNullOrWhiteSpace(ctx.ProposedFixSummary))
                    lines.Add("Proposed fix: " + _redactor.Redact(ctx.ProposedFixSummary));
                break;

            case ReproductionOutcome.Blocked:
            case ReproductionOutcome.EnvironmentUnavailable:
            case ReproductionOutcome.Error:
                lines.Add("Result: Run blocked before a clear reproduction.");
                lines.Add("");
                if (!string.IsNullOrWhiteSpace(ctx.ReproductionNotes))
                    lines.Add(_redactor.Redact(ctx.ReproductionNotes));
                break;

            default:
                lines.Add($"Result: {ctx.ReproductionOutcome}");
                if (!string.IsNullOrWhiteSpace(ctx.ReproductionNotes))
                    lines.Add(_redactor.Redact(ctx.ReproductionNotes));
                break;
        }

        lines.Add("");
        lines.Add($"Screenshots captured: {ctx.Evidence.Count(e => e.Kind == "screenshot")}. They are shown below.");
        return string.Join('\n', lines);
    }
}
