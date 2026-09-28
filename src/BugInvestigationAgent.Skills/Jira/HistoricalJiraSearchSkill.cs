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
/// Candidate retrieval via JQL, then LLM semantic similarity ranking (similar bugs, not same-parent siblings).
/// Keyword/JQL overlap is only a fallback when no LLM is configured — clearly labeled as non-semantic.
/// </summary>
public sealed class HistoricalJiraSearchSkill : ISkill
{
    private readonly IJiraIssueSearchClient _search;
    private readonly IAgentRuntimeResolver _runtimeResolver;
    private readonly ISecretRedactor _redactor;
    private readonly HistoricalJiraOptions _options;

    public HistoricalJiraSearchSkill(
        IJiraIssueSearchClient search,
        IAgentRuntimeResolver runtimeResolver,
        ISecretRedactor redactor,
        IOptions<AgentOptions> agentOptions)
    {
        _search = search;
        _runtimeResolver = runtimeResolver;
        _redactor = redactor;
        _options = agentOptions.Value.HistoricalJira;
    }

    public string SkillId => SkillIds.HistoricalJiraSearch;
    public string DisplayName => "Historical Jira Search";
    public AgentPermission RequiredPermissions => AgentPermission.ReadJira;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions) && context.JiraDetailsLoaded;

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        ctx.Investigation.SimilarIssues.Clear();
        ctx.Investigation.SimilarIssuesRankingMode = SimilarIssueRankingModes.None;
        ctx.Investigation.SimilarIssuesRankingNote = null;

        if (!_options.Enabled)
        {
            request.Progress?.Report("Historical Jira search disabled in settings.");
            return SkillResult.Ok("Historical search disabled.");
        }

        var jql = HistoricalJiraJqlBuilder.Build(ctx, ctx.Investigation.Intent);
        request.Progress?.Report($"Searching Jira history (candidate retrieval): {jql}");

        IReadOnlyList<JiraSearchHit> hits;
        try
        {
            hits = await _search.SearchAsync(jql, _options.MaxCandidates, cancellationToken);
        }
        catch (Exception ex)
        {
            request.Progress?.Report("Jira history search failed: " + _redactor.Redact(ex.Message));
            return SkillResult.Ok("Historical search soft-failed.");
        }

        request.Progress?.Report($"Found {hits.Count} candidate issue(s) for semantic comparison.");

        if (hits.Count == 0)
            return SkillResult.Ok("No similar historical issues found.");

        var runtime = _runtimeResolver.GetCurrent();
        if (!LlmHelpers.IsUsableLlm(runtime))
        {
            ApplyKeywordFallback(ctx, hits);
            request.Progress?.Report(ctx.Investigation.SimilarIssuesRankingNote ?? "Keyword fallback applied.");
            return SkillResult.Ok($"Stored {ctx.Investigation.SimilarIssues.Count} keyword-ranked candidate(s) (semantic ranking unavailable).");
        }

        var intent = ctx.Investigation.Intent;
        var candidateBlock = string.Join("\n---\n", hits.Take(_options.MaxCandidates).Select(h =>
            $"Key: {h.Key}\nSummary: {h.Summary}\nStatus: {h.Status}\nSteps: {string.Join(" | ", h.ReproductionSteps.Take(8))}\nDescription: {Truncate(h.Description, 600)}"));

        var userPrompt =
            "Compare the CURRENT bug to each CANDIDATE historical issue.\n" +
            "Rank by whether they describe the SAME underlying problem / behavior / symptoms / reproduction — NOT by shared parent, version, epic, or release label (e.g. V14 Beta).\n" +
            "Same parent or same version alone is NOT similarity. Prefer a different-version ticket with the same defect over a same-version unrelated ticket.\n" +
            "Do not invent details that are not present in the candidate text.\n\n" +
            $"CURRENT ticket: {_redactor.Redact(ctx.JiraTicketKey)}\n" +
            $"Summary: {_redactor.Redact(ctx.Summary)}\n" +
            $"Description: {_redactor.Redact(Truncate(ctx.Description, 1200))}\n" +
            $"Reproduction steps: {_redactor.Redact(string.Join(" | ", ctx.ReproductionSteps.Take(10)))}\n" +
            $"Intent problem: {_redactor.Redact(intent?.ActualProblem)}\n" +
            $"Expected: {_redactor.Redact(intent?.ExpectedBehavior)}\n" +
            $"Actual: {_redactor.Redact(intent?.ActualBehavior)}\n" +
            $"Module: {_redactor.Redact(intent?.AffectedModule)}\n" +
            $"Keywords: {_redactor.Redact(string.Join(", ", intent?.Keywords ?? Array.Empty<string>()))}\n\n" +
            "CANDIDATES:\n" +
            $"{_redactor.Redact(candidateBlock)}\n\n" +
            "Return ONLY JSON:\n" +
            "{\n" +
            "  \"ranked\": [\n" +
            "    {\n" +
            "      \"ticketKey\": \"PROJ-123\",\n" +
            "      \"similarity\": \"High\",\n" +
            "      \"score\": 0.87,\n" +
            "      \"reason\": \"Both involve stale typed input remaining in a multi-select control after reopen.\",\n" +
            "      \"sameUnderlyingProblem\": true,\n" +
            "      \"sameFunctionalArea\": true,\n" +
            "      \"similarSymptoms\": true,\n" +
            "      \"similarReproductionFlow\": true,\n" +
            "      \"candidateReproductionSteps\": [\"step1\"],\n" +
            "      \"candidateStepsApplicability\": \"Historical steps open the same assignee picker; validate against current UI before use.\"\n" +
            "    }\n" +
            "  ]\n" +
            "}\n" +
            "Include ONLY candidates with genuine problem similarity (score >= 0.55 OR sameUnderlyingProblem=true).\n" +
            $"At most {_options.MaxRankedResults} items. Omit unrelated same-parent/version tickets.\n" +
            "similarity must be High, Medium, or Low. score is 0..1.\n";

        try
        {
            var result = await runtime.CompleteAsync(new AgentTurnRequest
            {
                SystemPrompt =
                    "You are a senior bug-triage engineer. Compare bugs by underlying defect and user-visible behavior. " +
                    "Ignore shared epic/parent/fix-version as proof of similarity. Extract reproduction steps only when present in the candidate.",
                Messages = [new AgentMessage { Role = "user", Content = userPrompt }],
                Temperature = 0.1,
                MaxTokens = 2500
            }, cancellationToken);

            ApplyRanking(ctx, hits, result.Content, _options.MinSemanticScore, _options.MaxRankedResults);
            ctx.Investigation.SimilarIssuesRankingMode = SimilarIssueRankingModes.LlmSemantic;
            ctx.Investigation.SimilarIssuesRankingNote =
                "LLM semantic ranking used — issues ranked by underlying problem/behavior, not parent/version.";
            request.Progress?.Report($"Semantic ranking kept {ctx.Investigation.SimilarIssues.Count} similar bug(s).");
            return SkillResult.Ok($"Historical search complete ({ctx.Investigation.SimilarIssues.Count} semantically ranked).");
        }
        catch (Exception ex)
        {
            request.Progress?.Report("LLM semantic ranking failed — keyword fallback: " + _redactor.Redact(ex.Message));
            ApplyKeywordFallback(ctx, hits);
            return SkillResult.Ok("Historical search completed with keyword fallback after LLM failure.");
        }
    }

    /// <summary>Maps LLM JSON into <see cref="SimilarIssueCandidate"/> list (filters weak / unrelated).</summary>
    public static void ApplyRanking(
        RuntimeContext ctx,
        IReadOnlyList<JiraSearchHit> hits,
        string llmContent,
        double minSemanticScore = 0.55,
        int maxRanked = 5)
    {
        using var doc = LlmHelpers.TryParseJson(llmContent);
        if (doc is null)
            return;

        var root = doc.RootElement;
        if (!root.TryGetProperty("ranked", out var ranked) || ranked.ValueKind != JsonValueKind.Array)
            return;

        var byKey = hits.ToDictionary(h => h.Key, StringComparer.OrdinalIgnoreCase);
        var mapped = new List<SimilarIssueCandidate>();

        foreach (var item in ranked.EnumerateArray())
        {
            var key = LlmHelpers.ReadString(item, "ticketKey")
                      ?? LlmHelpers.ReadString(item, "key")
                      ?? "";
            if (string.IsNullOrWhiteSpace(key))
                continue;

            byKey.TryGetValue(key, out var hit);
            var sameProblem = ReadBool(item, "sameUnderlyingProblem");
            var sameArea = ReadBool(item, "sameFunctionalArea");
            var similarSymptoms = ReadBool(item, "similarSymptoms");
            var similarRepro = ReadBool(item, "similarReproductionFlow");

            var score = ReadScore(item);
            var level = LlmHelpers.ReadString(item, "similarity")
                        ?? BugSimilarityTerms.SimilarityLevelFromScore(score, sameProblem);

            // Reject weak matches: same-area-only or low score without same problem.
            if (!sameProblem && score < minSemanticScore)
                continue;
            if (string.Equals(level, "Unrelated", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(level, "Low", StringComparison.OrdinalIgnoreCase) && !sameProblem && score < minSemanticScore)
                continue;

            var steps = LlmHelpers.ReadStringArray(item, "candidateReproductionSteps");
            if (steps.Count == 0)
                steps = LlmHelpers.ReadStringArray(item, "candidateSteps");
            if (steps.Count == 0 && hit is not null)
                steps = hit.ReproductionSteps.ToList();

            var reason = LlmHelpers.ReadString(item, "reason")
                         ?? LlmHelpers.ReadString(item, "whyRelevant")
                         ?? "LLM-ranked similar bug";

            mapped.Add(new SimilarIssueCandidate
            {
                Key = key,
                Summary = hit?.Summary,
                Status = hit?.Status,
                DescriptionExcerpt = Truncate(hit?.Description, 400),
                WhyRelevant = reason,
                RelevanceScore = Math.Clamp(score, 0, 1),
                SimilarityLevel = NormalizeLevel(level, score, sameProblem),
                SameUnderlyingProblem = sameProblem,
                SameFunctionalArea = sameArea,
                SimilarSymptoms = similarSymptoms,
                SimilarReproductionFlow = similarRepro,
                RankedByLlm = true,
                CandidateStepsApplicability = LlmHelpers.ReadString(item, "candidateStepsApplicability"),
                CandidateSteps = steps,
                Url = hit?.Url
            });
        }

        foreach (var c in mapped
                     .OrderByDescending(c => c.SameUnderlyingProblem)
                     .ThenByDescending(c => c.SimilarSymptoms)
                     .ThenByDescending(c => c.SimilarReproductionFlow)
                     .ThenByDescending(c => c.RelevanceScore)
                     .Take(maxRanked))
        {
            ctx.Investigation.SimilarIssues.Add(c);
        }
    }

    private void ApplyKeywordFallback(RuntimeContext ctx, IReadOnlyList<JiraSearchHit> hits)
    {
        var queryTerms = BugSimilarityTerms.Extract(ctx, ctx.Investigation.Intent, maxTerms: 10);
        var scored = hits
            .Select(hit =>
            {
                var score = BugSimilarityTerms.KeywordOverlapScore(
                    queryTerms, hit.Summary, hit.Description, hit.ReproductionSteps);
                return (hit, score);
            })
            .Where(x => x.score >= _options.MinKeywordScore)
            .OrderByDescending(x => x.score)
            .Take(_options.MaxRankedResults)
            .ToList();

        foreach (var (hit, score) in scored)
        {
            var matched = queryTerms
                .Where(t => (hit.Summary ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                            || (hit.Description ?? "").Contains(t, StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .ToList();

            ctx.Investigation.SimilarIssues.Add(new SimilarIssueCandidate
            {
                Key = hit.Key,
                Summary = hit.Summary,
                Status = hit.Status,
                DescriptionExcerpt = Truncate(hit.Description, 400),
                WhyRelevant = matched.Count == 0
                    ? "Keyword/JQL candidate only — not semantic similarity. Shared release/parent is not treated as a match."
                    : $"Keyword overlap on: {string.Join(", ", matched)}. Not semantic similarity — configure an LLM for true bug comparison.",
                RelevanceScore = score,
                SimilarityLevel = BugSimilarityTerms.SimilarityLevelFromScore(score, sameUnderlyingProblem: false),
                SameUnderlyingProblem = false,
                SameFunctionalArea = false,
                RankedByLlm = false,
                CandidateSteps = hit.ReproductionSteps.ToList(),
                CandidateStepsApplicability = hit.ReproductionSteps.Count > 0
                    ? "Unvalidated keyword-candidate steps — do not treat as confirmed for this ticket."
                    : null,
                Url = hit.Url
            });
        }

        ctx.Investigation.SimilarIssuesRankingMode = SimilarIssueRankingModes.KeywordJqlFallback;
        ctx.Investigation.SimilarIssuesRankingNote =
            "Semantic ranking unavailable — using keyword/JQL candidate ranking. These are retrieval candidates, not confirmed similar bugs.";
    }

    private static double ReadScore(JsonElement item)
    {
        if (item.TryGetProperty("score", out var s) || item.TryGetProperty("relevanceScore", out s))
        {
            if (s.ValueKind == JsonValueKind.Number)
                return s.GetDouble();
            if (double.TryParse(s.GetString(), out var parsed))
                return parsed;
        }

        return 0.5;
    }

    private static bool ReadBool(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var p))
            return false;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(p.GetString(), out var b) && b,
            _ => false
        };
    }

    private static string NormalizeLevel(string? level, double score, bool sameProblem)
    {
        if (!string.IsNullOrWhiteSpace(level)
            && (level.Equals("High", StringComparison.OrdinalIgnoreCase)
                || level.Equals("Medium", StringComparison.OrdinalIgnoreCase)
                || level.Equals("Low", StringComparison.OrdinalIgnoreCase)))
            return char.ToUpperInvariant(level[0]) + level[1..].ToLowerInvariant();

        return BugSimilarityTerms.SimilarityLevelFromScore(score, sameProblem);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..(max - 1)] + "…";
}
