using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Repositories;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Skills.Code;

public static class VersionSkillIds
{
    public const string SourceVersion = "source-version";
    public const string LocalBranch = "local-branch";
}

/// <summary>
/// Read-only: finds the source branch matching the KMS version deployed on the tested environment, and commits in it
/// that mention this ticket or a resolved linked issue (evidence for "already fixed"). Nothing is checked out.
/// </summary>
public sealed class SourceVersionSkill : ISkill
{
    private readonly ISourceVersionControl _git;
    private readonly SourceVersioningOptions _options;

    public SourceVersionSkill(ISourceVersionControl git, IOptions<AgentOptions> options)
    {
        _git = git;
        _options = options.Value.SourceVersioning;
    }

    public string SkillId => VersionSkillIds.SourceVersion;
    public string DisplayName => "Source Version";
    public AgentPermission RequiredPermissions => AgentPermission.ReadWorkspace;
    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) => permissions.IsAllowed(RequiredPermissions);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        if (!MissingInfoResolver.IsLocalWorkspace(ctx.WorkspacePath) || !Directory.Exists(ctx.WorkspacePath))
        {
            ctx.SourceVersionNote = "No local source clone is configured (Sign in / Account → workspace); code was not analysed.";
            request.Progress?.Report(ctx.SourceVersionNote);
            return SkillResult.Ok("No workspace.");
        }
        if (string.IsNullOrWhiteSpace(ctx.DeployedVersion))
        {
            ctx.SourceVersionNote = "The deployed KMS version is unknown (" + (ctx.DeployedVersionSource ?? "not detected") +
                                    "), so the matching source branch cannot be chosen; code was not analysed and no branch can be created.";
            request.Progress?.Report(ctx.SourceVersionNote);
            return SkillResult.Ok("Version unknown.");
        }

        VersionRef? match;
        try
        {
            match = await _git.ResolveVersionRefAsync(ctx.WorkspacePath!, ctx.DeployedVersion!, _options.BranchPatterns, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            ctx.SourceVersionNote = ex.Message;
            request.Progress?.Report(ex.Message);
            return SkillResult.Ok("Repository not readable.");
        }
        if (match is null)
        {
            ctx.SourceVersionNote = $"No branch matching KMS {ctx.DeployedVersion} exists in the local clone (tried: " +
                                    string.Join(", ", _options.BranchPatterns.Take(4)) + " …). Run `git fetch` if branches are missing, or adjust Agent:SourceVersioning:BranchPatterns.";
            request.Progress?.Report(ctx.SourceVersionNote);
            return SkillResult.Ok("No matching branch.");
        }

        ctx.SourceVersionRef = match.RefName;
        ctx.SourceVersionCommit = match.Commit;
        ctx.RepositoryDisplay = $"{Path.GetFileName(ctx.WorkspacePath!.TrimEnd('\\', '/'))} @ {match.RefName} ({match.Commit[..Math.Min(10, match.Commit.Length)]})";
        request.Progress?.Report($"Source branch for KMS {ctx.DeployedVersion}: {match.RefName} at {match.Commit[..Math.Min(10, match.Commit.Length)]} (read without checkout; remote branches as of the last fetch).");

        var keys = new List<string> { ctx.JiraTicketKey ?? "" };
        keys.AddRange(ctx.ResolvedLinkedIssueKeys);
        foreach (var commit in await _git.FindCommitsMentioningAsync(ctx.WorkspacePath!, match.RefName, keys, cancellationToken))
        {
            ctx.FixCommitsInVersion.Add(commit);
            ctx.CodeCorrelation.Add($"In {match.RefName}: {commit}");
        }
        request.Progress?.Report(ctx.FixCommitsInVersion.Count == 0
            ? $"No commit in {match.RefName} mentions {string.Join(", ", keys.Where(k => k.Length > 0))}."
            : $"{ctx.FixCommitsInVersion.Count} commit(s) in {match.RefName} mention the ticket or a resolved linked issue.");
        return SkillResult.Ok("Version branch resolved.");
    }
}

/// <summary>
/// Creates ONE new local branch named after the Jira key from the version branch, for a bug reproduced with evidence.
/// No checkout, no file change, no commit, no push; an existing branch is never moved.
/// </summary>
public sealed class LocalBranchSkill : ISkill
{
    private readonly ISourceVersionControl _git;
    private readonly SourceVersioningOptions _options;

    public LocalBranchSkill(ISourceVersionControl git, IOptions<AgentOptions> options)
    {
        _git = git;
        _options = options.Value.SourceVersioning;
    }

    public string SkillId => VersionSkillIds.LocalBranch;
    public string DisplayName => "Local Branch";
    public AgentPermission RequiredPermissions => AgentPermission.CreateLocalBranch;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) => permissions.IsAllowed(RequiredPermissions);

    public static bool ReproducedWithEvidence(RuntimeContext ctx) =>
        ctx.ReproductionOutcome == ReproductionOutcome.Reproduced &&
        ctx.Evidence.Any(e => e.Kind == "screenshot" && !string.IsNullOrWhiteSpace(e.Path));

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        if (request.Permissions is null || !request.Permissions.IsAllowed(AgentPermission.CreateLocalBranch))
            return Note(ctx, request, "Branch creation is disabled.");
        if (!ReproducedWithEvidence(ctx))
            return Note(ctx, request, "No branch: the bug was not reproduced with evidence.");
        if (string.IsNullOrWhiteSpace(ctx.SourceVersionRef) || string.IsNullOrWhiteSpace(ctx.JiraTicketKey))
            return Note(ctx, request, "No branch: " + (ctx.SourceVersionNote ?? "the matching version branch is unknown."));
        if (!_options.NewBranchTemplate.Contains("{IssueKey}", StringComparison.Ordinal))
            return Note(ctx, request, "No branch: Agent:SourceVersioning:NewBranchTemplate must contain {IssueKey}.");

        var name = _options.NewBranchTemplate.Replace("{IssueKey}", ctx.JiraTicketKey!.ToUpperInvariant());
        var result = await _git.CreateBranchAsync(ctx.WorkspacePath!, name, ctx.SourceVersionRef!, cancellationToken);
        ctx.CreatedBranch = result.Outcome is BranchCreation.Created or BranchCreation.AlreadyExists ? result.BranchName : null;
        return Note(ctx, request, result.Message);
    }

    private static SkillResult Note(RuntimeContext ctx, SkillExecutionRequest request, string message)
    {
        ctx.BranchNote = message;
        request.Progress?.Report(message);
        return SkillResult.Ok(message);
    }
}
