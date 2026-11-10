using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Repositories;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Skills.Code;

/// <summary>
/// Hosted mode: resolves the repository for the ticket (Jira project / component / application mapping), checks it
/// out read-only into a per-investigation folder and correlates the ticket with commits and code-search hits.
/// The existing CodeInvestigationSkill then analyses that folder. No writes to the repository ever happen.
/// </summary>
public sealed class SourceRepositorySkill : ISkill
{
    private readonly IRepositoryResolver _resolver;
    private readonly IEnumerable<ISourceRepositoryProvider> _providers;
    private readonly RepositoryMappingOptions _options;
    private readonly ISecretRedactor _redactor;

    public SourceRepositorySkill(
        IRepositoryResolver resolver,
        IEnumerable<ISourceRepositoryProvider> providers,
        IOptions<RepositoryMappingOptions> options,
        ISecretRedactor redactor)
    {
        _resolver = resolver;
        _providers = providers;
        _options = options.Value;
        _redactor = redactor;
    }

    public string SkillId => SkillIds.SourceRepository;
    public string DisplayName => "Source Repository";
    public AgentPermission RequiredPermissions => AgentPermission.ReadWorkspace;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) => permissions.IsAllowed(RequiredPermissions);


    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        if (MissingInfoResolver.IsLocalWorkspace(ctx.WorkspacePath))
            return SkillResult.Ok("Workspace already available.");

        var selection = _resolver.Resolve(ctx.JiraTicketKey, ctx.Components, ctx.ApplicationId);
        if (selection is null)
        {
            request.Progress?.Report($"No repository mapping matches {ctx.JiraTicketKey} (project/component). Code investigation will be unavailable.");
            ctx.CustomFields["repositoryMappingMissing"] = "true";
            return SkillResult.Ok("No repository mapping.");
        }

        var provider = _providers.FirstOrDefault(p => string.Equals(p.ProviderId, selection.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return SkillResult.Fail($"Repository provider '{selection.Provider}' is not configured.");

        ctx.RepositoryDisplay = selection.Display;
        var target = _options.CheckoutDirectoryFor(ctx.InvestigationId);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);

        request.Progress?.Report($"Checking out {selection.Display} (read-only, shallow).");
        try
        {
            await provider.CheckoutAsync(selection, target, cancellationToken);
            ctx.WorkspacePath = target;
            ctx.CustomFields[CodeInvestigationSkill.HostedCheckoutField] = "true";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Progress?.Report("Repository checkout failed: " + _redactor.Redact(ex.Message));
            ctx.CustomFields["repositoryMappingMissing"] = "true";
            return SkillResult.Ok("Checkout failed.");
        }

        if (!string.IsNullOrWhiteSpace(ctx.JiraTicketKey))
        {
            var commits = await provider.FindCommitsMentioningAsync(selection, ctx.JiraTicketKey!, 5, cancellationToken);
            foreach (var c in commits)
                ctx.CodeCorrelation.Add($"Commit {c.CommitId[..Math.Min(8, c.CommitId.Length)]} mentions {ctx.JiraTicketKey}: {FirstLine(c.Comment)}");
            request.Progress?.Report($"{commits.Count} commit(s) on {selection.Branch} mention {ctx.JiraTicketKey}.");
        }

        var terms = ctx.Investigation.Intent?.Keywords.Where(k => k.Length >= 4).Take(3).ToList() ?? new List<string>();
        foreach (var term in terms)
        {
            var hits = await provider.SearchCodeAsync(selection, term, 3, cancellationToken);
            foreach (var h in hits)
                ctx.CodeCorrelation.Add($"Code search '{term}': {h.Path}");
        }

        return SkillResult.Ok($"Repository {selection.Display} ready.");
    }

    private static string FirstLine(string? s) =>
        (s ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
