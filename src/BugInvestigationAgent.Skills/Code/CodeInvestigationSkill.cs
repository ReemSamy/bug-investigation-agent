using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Memory;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Rules;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;

namespace BugInvestigationAgent.Skills.Code;

public sealed class CodeInvestigationSkill : ISkill
{
    private readonly IAgentRuntimeResolver _runtimeResolver;
    private readonly IRulesProvider _rulesProvider;
    private readonly IEngineeringMemoryStore _memoryStore;
    private readonly ISecretRedactor _redactor;

    public CodeInvestigationSkill(
        IAgentRuntimeResolver runtimeResolver,
        IRulesProvider rulesProvider,
        IEngineeringMemoryStore memoryStore,
        ISecretRedactor redactor)
    {
        _runtimeResolver = runtimeResolver;
        _rulesProvider = rulesProvider;
        _memoryStore = memoryStore;
        _redactor = redactor;
    }

    public string SkillId => SkillIds.CodeInvestigation;
    public string DisplayName => "Code Investigation";
    public AgentPermission RequiredPermissions => AgentPermission.ReadWorkspace;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions) && !string.IsNullOrWhiteSpace(context.WorkspacePath);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        var workspace = ctx.WorkspacePath!;
        request.Progress?.Report($"Scanning workspace and project rules under {workspace}.");

        var safety = await _rulesProvider.GetAppSafetyRulesAsync(cancellationToken);
        var workspaceRules = await _rulesProvider.LoadWorkspaceRulesAsync(workspace, cancellationToken);
        var lessons = await _memoryStore.QueryRelevantAsync(workspace, new[] { ctx.SystemName ?? "", "bug", "routing" }, cancellationToken);

        var keywords = ExtractKeywords(ctx);
        var snippets = SearchWorkspace(workspace, keywords, maxFiles: 12);
        var browserObs = BuildBrowserObservations(ctx);
        ctx.Investigation.BrowserObservationsSummary = browserObs;

        var system = string.Join("\n", safety.Concat(workspaceRules.Take(5)).Select(r => "- " + _redactor.Redact(r)));
        if (lessons.Count > 0)
        {
            system += "\n\nHuman-validated engineering lessons:\n" +
                      string.Join("\n", lessons.Select(l => $"- { _redactor.Redact(l.Lesson)}"));
        }

        var intent = ctx.Investigation.Intent;
        var similar = string.Join("\n", ctx.Investigation.SimilarIssues.Take(5).Select(s =>
            $"- {s.Key}: {s.Summary} | why={s.WhyRelevant} | score={s.RelevanceScore:0.00}"));
        var docs = string.Join("\n---\n", ctx.Investigation.KmsDocuments.Take(4).Select(d =>
            $"[{d.Title}] ({d.SourcePath})\n{d.Text}"));
        var histSteps = ctx.Investigation.AdoptedHistoricalSteps.Count > 0
            ? string.Join(" | ", ctx.Investigation.AdoptedHistoricalSteps)
            : string.Join(" | ", ctx.Investigation.CandidateHistoricalSteps.Take(8));

        var userPrompt = $"""
            Analyze this bug and propose a minimal fix. Respond in JSON with keys:
            rootCause, evidenceReasoning, potentialImpact, proposedFixSummary,
            proposedFiles (array of paths), proposedDiff (unified diff or empty if unsure).

            Ticket: {ctx.JiraTicketKey}
            Summary: {_redactor.Redact(ctx.Summary)}
            Description: {_redactor.Redact(ctx.Description)}
            Reproduction steps: {_redactor.Redact(string.Join(" | ", ctx.ReproductionSteps))}
            Reproduction notes: {_redactor.Redact(ctx.ReproductionNotes)}
            Reproduction outcome: {ctx.ReproductionOutcome}

            LLM ticket understanding:
            - problem: {_redactor.Redact(intent?.ActualProblem)}
            - expected: {_redactor.Redact(intent?.ExpectedBehavior)}
            - actual: {_redactor.Redact(intent?.ActualBehavior)}
            - module: {_redactor.Redact(intent?.AffectedModule)}
            - category: {_redactor.Redact(intent?.BugCategory)}
            - hints: {_redactor.Redact(string.Join("; ", intent?.InvestigationHints ?? Array.Empty<string>()))}

            Similar historical issues:
            {_redactor.Redact(string.IsNullOrWhiteSpace(similar) ? "(none)" : similar)}

            Candidate/adopted historical steps:
            {_redactor.Redact(string.IsNullOrWhiteSpace(histSteps) ? "(none)" : histSteps)}

            Relevant KMS documentation:
            {_redactor.Redact(string.IsNullOrWhiteSpace(docs) ? "(none)" : docs)}

            Browser evidence / observations:
            {_redactor.Redact(browserObs)}

            Code snippets:
            {_redactor.Redact(snippets)}
            """;

        var runtime = _runtimeResolver.GetCurrent();
        if (string.Equals(runtime.ProviderId, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(runtime.ProviderId, "cursor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(runtime.ProviderId, "azure-openai", StringComparison.OrdinalIgnoreCase))
        {
            ctx.RootCause =
                "Investigation paused: no real LLM provider is configured (active provider is a stub/null). " +
                "The bug may have been reproduced in the browser, but a code fix cannot be invented without an API key. " +
                "Open Sign in / Account, set OpenAI-compatible or Anthropic API key, set Active provider, then Analyze again.";
            ctx.ProposedFixSummary = "Configure an LLM API key before requesting a code fix.";
            ctx.ProposedFiles = new List<string>();
            ctx.ProposedDiff = "";
            ctx.CustomFields["investigationBlockedNoLlm"] = "true";
            request.Progress?.Report("Skipped code fix proposal — no LLM API key configured.");
            return SkillResult.Ok("Investigation blocked: LLM not configured.");
        }

        request.Progress?.Report($"Asking provider '{runtime.ProviderId}' for root-cause analysis.");
        AgentTurnResult result;
        try
        {
            result = await runtime.CompleteAsync(new AgentTurnRequest
            {
                SystemPrompt = system,
                Messages = [new AgentMessage { Role = "user", Content = userPrompt }],
                Temperature = 0.1,
                MaxTokens = 3000
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            ctx.RootCause = "LLM provider call failed: " + _redactor.Redact(ex.Message);
            ctx.ProposedFixSummary = "Fix the LLM provider settings, then re-run investigation.";
            ctx.ProposedFiles = new List<string>();
            ctx.ProposedDiff = "";
            ctx.CustomFields["investigationBlockedNoLlm"] = "true";
            request.Progress?.Report("LLM call failed — no fake fix proposed.");
            return SkillResult.Ok("Investigation blocked: LLM call failed.");
        }

        ApplyAnalysis(ctx, result.Content, snippets);
        request.Progress?.Report("Root cause and proposed fix prepared.");
        return SkillResult.Ok("Investigation complete.");
    }

    private static void ApplyAnalysis(RuntimeContext ctx, string content, string snippets)
    {
        ctx.RootCause = content;
        ctx.ProposedFixSummary = content;
        try
        {
            var json = ExtractJson(content);
            if (json is not null)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("rootCause", out var rc))
                    ctx.RootCause = rc.GetString();
                if (root.TryGetProperty("proposedFixSummary", out var pf))
                    ctx.ProposedFixSummary = pf.GetString();
                if (root.TryGetProperty("proposedDiff", out var diff))
                    ctx.ProposedDiff = diff.GetString();
                if (root.TryGetProperty("evidenceReasoning", out var er))
                    ctx.Investigation.EvidenceReasoning = er.GetString();
                if (root.TryGetProperty("potentialImpact", out var pi))
                    ctx.Investigation.PotentialImpact = pi.GetString();
                if (root.TryGetProperty("proposedFiles", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    ctx.ProposedFiles = files.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
                }
            }
        }
        catch
        {
            // keep raw content
        }

        if (ctx.ProposedFiles.Count == 0)
        {
            foreach (Match m in Regex.Matches(snippets, @"^---\s+(.+)$", RegexOptions.Multiline))
                ctx.ProposedFiles.Add(m.Groups[1].Value.Trim());
        }
    }

    private static string? ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return content[start..(end + 1)];
    }

    private static List<string> ExtractKeywords(RuntimeContext ctx)
    {
        var intentBits = ctx.Investigation.Intent is null
            ? ""
            : string.Join(" ", ctx.Investigation.Intent.Keywords.Concat(ctx.Investigation.Intent.RelevantEntities)
                .Append(ctx.Investigation.Intent.AffectedModule ?? ""));
        var text = $"{ctx.Summary} {ctx.Description} {intentBits}";
        return Regex.Matches(text, @"\b[A-Za-z][A-Za-z0-9_]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static string BuildBrowserObservations(RuntimeContext ctx)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.ReproductionNotes))
            lines.Add("Notes: " + ctx.ReproductionNotes);
        foreach (var e in ctx.Evidence.Take(20))
        {
            if (!string.IsNullOrWhiteSpace(e.Caption))
                lines.Add($"{e.Kind}: {e.Caption}");
        }

        return lines.Count == 0 ? "(no browser observations)" : string.Join("\n", lines);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "with", "from", "when", "where", "have", "been", "were", "will", "should", "could", "would",
        "about", "after", "before", "error", "issue", "bug", "steps", "reproduce", "expected", "actual"
    };

    private static string SearchWorkspace(string workspace, IReadOnlyList<string> keywords, int maxFiles)
    {
        var sb = new StringBuilder();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".cshtml", ".js", ".ts", ".tsx", ".css", ".razor", ".json", ".md" };
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "obj", "node_modules", ".git", "packages", ".vs" };

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(workspace, "*.*", SearchOption.AllDirectories))
        {
            var relParts = Path.GetRelativePath(workspace, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relParts.Any(p => skipDirs.Contains(p)))
                continue;
            if (!extensions.Contains(Path.GetExtension(file)))
                continue;

            string text;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 400_000)
                    continue;
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            if (keywords.Count > 0 && !keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                continue;

            sb.AppendLine($"--- {Path.GetRelativePath(workspace, file)}");
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var take = Math.Min(lines.Length, 80);
            for (var i = 0; i < take; i++)
                sb.AppendLine(lines[i]);
            sb.AppendLine();
            count++;
            if (count >= maxFiles)
                break;
        }

        return sb.Length == 0 ? "(no matching source files found for keywords)" : sb.ToString();
    }
}
