using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Knowledge;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Skills;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Skills.Knowledge;

/// <summary>
/// Retrieves Visium KMS markdown documentation relevant to the current investigation intent.
/// Product knowledge stays outside Skills — this skill only queries the knowledge store.
/// </summary>
public sealed class KmsDocumentationSkill : ISkill
{
    private readonly IKnowledgeIngestionService _ingestion;
    private readonly IKnowledgeRetriever _retriever;
    private readonly KmsDocsOptions _options;
    private bool _ingested;

    public KmsDocumentationSkill(
        IKnowledgeIngestionService ingestion,
        IKnowledgeRetriever retriever,
        IOptions<AgentOptions> agentOptions)
    {
        _ingestion = ingestion;
        _retriever = retriever;
        _options = agentOptions.Value.KmsDocs;
    }

    public string SkillId => SkillIds.KmsDocumentation;
    public string DisplayName => "KMS Documentation";
    public AgentPermission RequiredPermissions => AgentPermission.ReadWorkspace;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        ctx.Investigation.KmsDocuments.Clear();

        if (!_options.Enabled)
        {
            request.Progress?.Report("KMS documentation retrieval disabled.");
            return SkillResult.Ok("KMS docs disabled.");
        }

        if (string.IsNullOrWhiteSpace(_options.RootPath) || !Directory.Exists(_options.RootPath))
        {
            request.Progress?.Report(
                "KMS docs RootPath not configured or missing. Set Agent:KmsDocs:RootPath to a local markdown clone.");
            return SkillResult.Ok("KMS docs path not configured.");
        }

        if (!_ingested)
        {
            request.Progress?.Report($"Ingesting KMS docs from {_options.RootPath}…");
            var count = await _ingestion.IngestAsync(_options.RootPath, _options.ChunkSizeChars, cancellationToken);
            _ingested = true;
            request.Progress?.Report($"Ingested {count} documentation chunk(s).");
        }

        var intent = ctx.Investigation.Intent;
        var query = string.Join(" ",
            new[]
            {
                intent?.AffectedModule,
                intent?.ActualProblem,
                ctx.Summary,
                string.Join(" ", intent?.Keywords ?? Array.Empty<string>())
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (string.IsNullOrWhiteSpace(query))
            query = ctx.JiraTicketKey ?? "Visium KMS";

        var hits = await _retriever.RetrieveAsync(query, _options.MaxChunks, cancellationToken);
        foreach (var hit in hits)
        {
            ctx.Investigation.KmsDocuments.Add(new RetrievedDocumentChunk
            {
                SourcePath = hit.SourcePath,
                Title = hit.Title,
                Text = hit.Text.Length > 1500 ? hit.Text[..1500] + "…" : hit.Text,
                Score = hit.Score,
                ModuleHint = hit.ModuleHint
            });
        }

        request.Progress?.Report($"Retrieved {ctx.Investigation.KmsDocuments.Count} KMS documentation chunk(s).");
        return SkillResult.Ok($"KMS documentation retrieval complete ({ctx.Investigation.KmsDocuments.Count}).");
    }
}
