using System.Text.Json;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Skills.Common;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Skills.Jira;

/// <summary>
/// Compares historical candidate steps with the current ticket.
/// Only marks steps as adoptable when confidence is high — never blindly executes foreign steps.
/// </summary>
public sealed class HistoricalReproductionReuseSkill : ISkill
{
    private readonly IAgentRuntimeResolver _runtimeResolver;
    private readonly ISecretRedactor _redactor;
    private readonly HistoricalJiraOptions _options;

    public HistoricalReproductionReuseSkill(
        IAgentRuntimeResolver runtimeResolver,
        ISecretRedactor redactor,
        IOptions<AgentOptions> agentOptions)
    {
        _runtimeResolver = runtimeResolver;
        _redactor = redactor;
        _options = agentOptions.Value.HistoricalJira;
    }

    public string SkillId => SkillIds.HistoricalReproductionReuse;
    public string DisplayName => "Historical Reproduction Reuse";
    public AgentPermission RequiredPermissions => AgentPermission.ReadJira;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        ctx.Investigation.CandidateHistoricalSteps.Clear();
        ctx.Investigation.AdoptedHistoricalSteps.Clear();
        ctx.Investigation.CandidateStepsValidatedAgainstTicket = false;
        ctx.Investigation.CandidateStepsConfidence = 0;
        ctx.Investigation.CandidateStepsSourceKey = null;

        // Prefer current ticket steps when Jira provided them.
        if (ctx.CustomFields.ContainsKey("stepsFromJira") && ctx.ReproductionSteps.Count > 0)
        {
            request.Progress?.Report("Current ticket already has reproduction steps — not adopting historical steps.");
            return SkillResult.Ok("Kept current ticket steps.");
        }

        var best = ctx.Investigation.SimilarIssues
            .Where(s => s.CandidateSteps.Count > 0
                        && (s.SameUnderlyingProblem
                            || s.RelevanceScore >= 0.7
                            || string.Equals(s.SimilarityLevel, "High", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.SameUnderlyingProblem)
            .ThenByDescending(s => s.RelevanceScore)
            .ThenByDescending(s => s.CandidateSteps.Count)
            .FirstOrDefault();

        if (best is null)
        {
            request.Progress?.Report("No historical candidate steps available.");
            return SkillResult.Ok("No candidate historical steps.");
        }

        ctx.Investigation.CandidateHistoricalSteps = best.CandidateSteps.ToList();
        ctx.Investigation.CandidateStepsSourceKey = best.Key;

        var runtime = _runtimeResolver.GetCurrent();
        if (!LlmHelpers.IsUsableLlm(runtime))
        {
            // Conservative: store candidates but do not validate/adopt without LLM.
            request.Progress?.Report($"Stored {best.CandidateSteps.Count} candidate step(s) from {best.Key} (not validated — no LLM).");
            return SkillResult.Ok("Candidate historical steps stored (unvalidated).");
        }

        var prompt =
            "Compare current ticket with historical steps. Respond ONLY JSON:\n" +
            "{\n" +
            "  \"compatible\": true,\n" +
            "  \"confidence\": 0.0,\n" +
            "  \"adoptedSteps\": [\"...\"],\n" +
            "  \"rationale\": \"...\"\n" +
            "}\n\n" +
            $"Current ticket: {_redactor.Redact(ctx.JiraTicketKey)}\n" +
            $"Summary: {_redactor.Redact(ctx.Summary)}\n" +
            $"Description: {_redactor.Redact(Truncate(ctx.Description, 1500))}\n" +
            $"Intent module: {_redactor.Redact(ctx.Investigation.Intent?.AffectedModule)}\n" +
            $"Intent problem: {_redactor.Redact(ctx.Investigation.Intent?.ActualProblem)}\n\n" +
            $"Historical issue: {best.Key} (score={best.RelevanceScore:0.00})\n" +
            $"Why relevant: {_redactor.Redact(best.WhyRelevant)}\n" +
            "Historical steps:\n" +
            $"{_redactor.Redact(string.Join("\n", best.CandidateSteps.Select((s, i) => $"{i + 1}. {s}")))}\n\n" +
            "Only set compatible=true when steps clearly apply to THIS ticket's module and defect.\n" +
            "Prefer adapting wording to the current ticket; do not invent unrelated navigation.\n";

        try
        {
            var result = await runtime.CompleteAsync(new AgentTurnRequest
            {
                SystemPrompt =
                    "You validate whether historical bug reproduction steps can help investigate a different ticket. " +
                    "Be conservative. Never claim identical bugs without evidence.",
                Messages = [new AgentMessage { Role = "user", Content = prompt }],
                Temperature = 0.1,
                MaxTokens = 1500
            }, cancellationToken);

            ApplyValidation(ctx, result.Content, _options.MinStepAdoptionConfidence);
            request.Progress?.Report(
                ctx.Investigation.CandidateStepsValidatedAgainstTicket
                    ? $"Adopted {ctx.Investigation.AdoptedHistoricalSteps.Count} historical step(s) from {best.Key} (confidence={ctx.Investigation.CandidateStepsConfidence:0.00})."
                    : $"Historical steps from {best.Key} not adopted (confidence={ctx.Investigation.CandidateStepsConfidence:0.00}).");
            return SkillResult.Ok("Historical reproduction reuse evaluated.");
        }
        catch (Exception ex)
        {
            request.Progress?.Report("Historical step validation failed: " + _redactor.Redact(ex.Message));
            return SkillResult.Ok("Historical reuse soft-failed.");
        }
    }

    public static void ApplyValidation(RuntimeContext ctx, string content, double minConfidence)
    {
        using var doc = LlmHelpers.TryParseJson(content);
        if (doc is null)
            return;

        var root = doc.RootElement;
        var confidence = 0.0;
        if (root.TryGetProperty("confidence", out var c))
        {
            if (c.ValueKind == JsonValueKind.Number)
                confidence = c.GetDouble();
            else
                double.TryParse(c.GetString(), out confidence);
        }

        ctx.Investigation.CandidateStepsConfidence = Math.Clamp(confidence, 0, 1);
        var compatible = root.TryGetProperty("compatible", out var comp) &&
                         (comp.ValueKind == JsonValueKind.True ||
                          string.Equals(comp.GetString(), "true", StringComparison.OrdinalIgnoreCase));

        var adopted = LlmHelpers.ReadStringArray(root, "adoptedSteps");
        if (adopted.Count == 0)
            adopted = ctx.Investigation.CandidateHistoricalSteps.ToList();

        if (compatible && confidence >= minConfidence && adopted.Count > 0)
        {
            ctx.Investigation.CandidateStepsValidatedAgainstTicket = true;
            ctx.Investigation.AdoptedHistoricalSteps = adopted;
        }
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..(max - 1)] + "…";
}
