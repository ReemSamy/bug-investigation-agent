using System.Text;
using System.Text.Json;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Skills.Reproduction;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Skills.Evidence;

public sealed class EvidenceReportingSkill : ISkill
{
    private readonly ISecretRedactor _redactor;
    private readonly AgentOptions? _options;

    public EvidenceReportingSkill(ISecretRedactor redactor, IOptions<AgentOptions>? options = null)
    {
        _redactor = redactor;
        _options = options?.Value;
    }

    public string SkillId => SkillIds.EvidenceReporting;
    public string DisplayName => "Evidence Reporting";
    public AgentPermission RequiredPermissions => AgentPermission.None;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) => true;

    public Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        var manifestPath = WriteManifest(ctx);
        var summary = BuildHumanReadableSummary(ctx, manifestPath);
        ctx.Evidence.Add(EvidenceItem.For(ctx, "summary", null, summary));

        // Keep a short machine log too (not the primary UI text). Never includes credentials.
        var sb = new StringBuilder();
        sb.AppendLine($"Ticket: {ctx.JiraTicketKey}");
        sb.AppendLine($"Investigation: {ctx.InvestigationId:N}");
        sb.AppendLine($"Environment: {ctx.SelectedEnvironmentDisplayName ?? ctx.EnvironmentName ?? "?"} [{ctx.SelectedEnvironmentId ?? "?"}] {EnvironmentUrlSanitizer.StripCredentials(ctx.EnvironmentUrl)}");
        sb.AppendLine($"Sign-in: {AuthenticationStatusDisplay.Label(ctx.AuthenticationStatus)}");
        sb.AppendLine($"Outcome: {ctx.ReproductionOutcome}");
        sb.AppendLine($"Screenshots: {ctx.Evidence.Count(e => e.Kind == "screenshot")}");
        foreach (var e in ctx.Evidence.Where(e => e.Kind == "screenshot").Take(30))
            sb.AppendLine($"- [{e.Category ?? "screenshot"}] {e.Path}");
        if (manifestPath is not null)
            sb.AppendLine($"Manifest: {manifestPath}");
        sb.AppendLine("Jira upload: not performed (attachment upload is not implemented; evidence is stored locally).");

        ctx.Evidence.Add(EvidenceItem.For(ctx, "report", null, sb.ToString()));

        if (string.IsNullOrWhiteSpace(ctx.FinalStatusMessage) && ctx.ReproductionOutcome == ReproductionOutcome.Reproduced && ctx.RetestPassed)
            ctx.FinalStatusMessage = "LOCAL CHANGES VALIDATED → READY FOR HUMAN REVIEW";

        request.Progress?.Report("Readable evidence summary ready.");
        return Task.FromResult(SkillResult.Ok("Evidence report ready."));
    }

    private string BuildHumanReadableSummary(RuntimeContext ctx, string? manifestPath = null)
    {
        var lines = new List<string>();
        var ticket = ctx.JiraTicketKey ?? "this ticket";

        lines.Add($"What happened for {ticket}");
        lines.Add("");
        var envLabel = ctx.SelectedEnvironmentDisplayName ?? ctx.EnvironmentName;
        if (!string.IsNullOrWhiteSpace(envLabel))
        {
            lines.Add($"Environment: {envLabel}" +
                      (ctx.SelectedEnvironmentType is { } t ? $" ({t})" : "") +
                      (string.IsNullOrWhiteSpace(ctx.EnvironmentUrl) ? "" : $" — {EnvironmentUrlSanitizer.StripCredentials(ctx.EnvironmentUrl)}"));
            lines.Add($"Sign-in: {AuthenticationStatusDisplay.Label(ctx.AuthenticationStatus)}");
            if (ctx.IsReadOnlyEnvironment)
            {
                lines.Add(ctx.StateChangingActionsConfirmed
                    ? "Production: the user explicitly allowed potentially state-changing steps."
                    : "Production: read-only investigation (no data-changing steps were performed).");
                if (ctx.SkippedStateChangingSteps.Count > 0)
                {
                    lines.Add("Skipped (potentially state-changing):");
                    foreach (var step in ctx.SkippedStateChangingSteps.Take(10))
                        lines.Add("  - " + _redactor.Redact(step));
                }
            }
            lines.Add("");
        }

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
                if (!string.IsNullOrWhiteSpace(ctx.FixPullRequestUrl))
                {
                    lines.Add("");
                    lines.Add($"Note: this ticket links a fix PR ({ctx.FixPullRequestUrl}). That may mean the bug is already fixed on the environment you tested — or the fix is not deployed there yet.");
                }
                lines.Add("No code investigation or fix was started, because the bug was not clearly reproduced.");
                lines.Add("Review the screenshots. If needed, add clearer Jira steps for this ticket and run Analyze again.");
                break;

            case ReproductionOutcome.NotReproduced:
                lines.Add("Result: Could not reproduce — the reported issue was not found.");
                lines.Add("");
                lines.Add("The agent followed the available steps and did not observe the defect described in the ticket.");
                if (!string.IsNullOrWhiteSpace(ctx.FixPullRequestUrl))
                    lines.Add($"Ticket also links a fix PR ({ctx.FixPullRequestUrl}) — consistent with the defect already being fixed on this environment.");
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
        lines.Add(manifestPath is null
            ? "Evidence is kept locally in this app. It was NOT uploaded to Jira (attachment upload is not implemented)."
            : $"Evidence is kept locally ({Path.GetDirectoryName(manifestPath)}). It was NOT uploaded to Jira (attachment upload is not implemented).");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Writes evidence-manifest.json next to the screenshots: ties every file to ticket, investigation and environment.
    /// Contains no usernames, passwords or tokens.
    /// </summary>
    private string? WriteManifest(RuntimeContext ctx)
    {
        if (_options is null)
            return null;
        try
        {
            var root = Path.IsPathRooted(_options.EvidenceDirectory)
                ? _options.EvidenceDirectory
                : Path.Combine(Directory.GetCurrentDirectory(), _options.EvidenceDirectory);
            var dir = Path.Combine(root, ctx.InvestigationId.ToString("N"));
            Directory.CreateDirectory(dir);
            var manifest = new
            {
                ticket = ctx.JiraTicketKey,
                investigationId = ctx.InvestigationId.ToString("N"),
                environment = new
                {
                    id = ctx.SelectedEnvironmentId,
                    name = ctx.SelectedEnvironmentDisplayName ?? ctx.EnvironmentName,
                    type = ctx.SelectedEnvironmentType?.ToString(),
                    url = EnvironmentUrlSanitizer.StripCredentials(ctx.EnvironmentUrl),
                    readOnly = ctx.IsReadOnlyEnvironment
                },
                authentication = ctx.AuthenticationStatus.ToString(),
                outcome = ctx.ReproductionOutcome.ToString(),
                uploadedToJira = false,
                generatedAtUtc = DateTimeOffset.UtcNow,
                items = ctx.Evidence
                    .Where(e => e.Kind is "screenshot" or "error" or "note")
                    .Select(e => new
                    {
                        kind = e.Kind,
                        category = e.Category,
                        file = e.Path is null ? null : Path.GetFileName(e.Path),
                        caption = _redactor.Redact(e.Caption),
                        ticket = e.JiraTicketKey,
                        investigationId = e.InvestigationId?.ToString("N"),
                        environmentId = e.EnvironmentId,
                        capturedAtUtc = e.CapturedAt
                    })
            };
            var path = Path.Combine(dir, "evidence-manifest.json");
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
