using System.Text.Json;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Skills.Common;

namespace BugInvestigationAgent.Skills.Jira;

/// <summary>
/// After JiraSkill retrieves the ticket, asks the LLM for structured InvestigationIntent.
/// Soft-fails when no LLM is configured — raw Jira remains source of truth.
/// </summary>
public sealed class TicketUnderstandingSkill : ISkill
{
    private readonly IAgentRuntimeResolver _runtimeResolver;
    private readonly ISecretRedactor _redactor;

    public TicketUnderstandingSkill(IAgentRuntimeResolver runtimeResolver, ISecretRedactor redactor)
    {
        _runtimeResolver = runtimeResolver;
        _redactor = redactor;
    }

    public string SkillId => SkillIds.TicketUnderstanding;
    public string DisplayName => "Ticket Understanding";
    public AgentPermission RequiredPermissions => AgentPermission.ReadJira;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions) && context.JiraDetailsLoaded;

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        var runtime = _runtimeResolver.GetCurrent();
        if (!LlmHelpers.IsUsableLlm(runtime))
        {
            request.Progress?.Report("Skipping LLM ticket understanding — " + (_runtimeResolver.LlmUnavailableReason ?? "no LLM provider available."));
            ctx.Investigation.Intent = null;
            return SkillResult.Ok("Ticket understanding skipped (no LLM).");
        }

        request.Progress?.Report("Asking LLM to structure investigation intent from the Jira ticket…");
        var userPrompt =
            "Analyze this Jira bug ticket and respond with ONLY JSON:\n" +
            "{\n" +
            "  \"actualProblem\": \"...\",\n" +
            "  \"expectedBehavior\": \"...\",\n" +
            "  \"actualBehavior\": \"...\",\n" +
            "  \"affectedModule\": \"...\",\n" +
            "  \"bugCategory\": \"...\",\n" +
            "  \"reproductionIntent\": \"...\",\n" +
            "  \"keywords\": [\"...\"],\n" +
            "  \"relevantEntities\": [\"...\"],\n" +
            "  \"missingInformation\": [\"...\"],\n" +
            "  \"investigationHints\": [\"...\"]\n" +
            "}\n\n" +
            $"Ticket: {_redactor.Redact(ctx.JiraTicketKey)}\n" +
            $"Summary: {_redactor.Redact(ctx.Summary)}\n" +
            $"Description: {_redactor.Redact(ctx.Description)}\n" +
            $"Reproduction steps: {_redactor.Redact(string.Join(" | ", ctx.ReproductionSteps))}\n" +
            $"Comments: {_redactor.Redact(string.Join(" | ", ctx.Comments.Take(5)))}\n";

        try
        {
            var result = await runtime.CompleteAsync(new AgentTurnRequest
            {
                SystemPrompt =
                    "You are a senior QA/engineer assistant. Extract structured investigation intent from bug tickets. " +
                    "Do not invent environment URLs or credentials. Prefer concise factual fields. " +
                    "Keywords must describe the defect (controls, symptoms, verbs) — never release labels like V14/Beta or epic names alone.",
                Messages = [new AgentMessage { Role = "user", Content = userPrompt }],
                Temperature = 0.1,
                MaxTokens = 1500
            }, cancellationToken);

            var intent = ParseIntent(result.Content);
            intent.RawLlmResponse = result.Content;
            intent.GeneratedByLlm = true;
            ctx.Investigation.Intent = intent;
            request.Progress?.Report(
                $"Intent ready: module={intent.AffectedModule ?? "?"}, category={intent.BugCategory ?? "?"}, keywords={intent.Keywords.Count}.");
            return SkillResult.Ok("Ticket understanding complete.");
        }
        catch (Exception ex)
        {
            request.Progress?.Report("LLM ticket understanding failed — continuing without intent: " + _redactor.Redact(ex.Message));
            ctx.Investigation.Intent = null;
            return SkillResult.Ok("Ticket understanding soft-failed; continuing.");
        }
    }

    public static InvestigationIntent ParseIntent(string content)
    {
        var intent = new InvestigationIntent();
        using var doc = LlmHelpers.TryParseJson(content);
        if (doc is null)
            return intent;

        var root = doc.RootElement;
        intent.ActualProblem = LlmHelpers.ReadString(root, "actualProblem", "problem");
        intent.ExpectedBehavior = LlmHelpers.ReadString(root, "expectedBehavior", "expected");
        intent.ActualBehavior = LlmHelpers.ReadString(root, "actualBehavior", "actual");
        intent.AffectedModule = LlmHelpers.ReadString(root, "affectedModule", "module");
        intent.BugCategory = LlmHelpers.ReadString(root, "bugCategory", "category");
        intent.ReproductionIntent = LlmHelpers.ReadString(root, "reproductionIntent");
        intent.Keywords = LlmHelpers.ReadStringArray(root, "keywords");
        intent.RelevantEntities = LlmHelpers.ReadStringArray(root, "relevantEntities");
        intent.MissingInformation = LlmHelpers.ReadStringArray(root, "missingInformation");
        intent.InvestigationHints = LlmHelpers.ReadStringArray(root, "investigationHints");
        return intent;
    }
}
