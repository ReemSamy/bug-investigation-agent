namespace BugInvestigationAgent.Core.Repositories;

public sealed record VersionRef(string RefName, string Commit, string MatchedPattern);

public enum BranchCreation
{
    Created,
    AlreadyExists,
    Refused
}

public sealed record BranchResult(BranchCreation Outcome, string BranchName, string Message);

/// <summary>
/// Access to the local source clone for version-aware investigation. Every operation is read-only except
/// <see cref="CreateBranchAsync"/>, which only adds a NEW branch ref pointing at an existing commit: it never checks out,
/// commits, pushes, fetches, deletes, moves an existing branch or changes a file.
/// </summary>
public interface ISourceVersionControl
{
    /// <summary>Source branch matching the deployed version (first configured pattern that exists), or null.</summary>
    Task<VersionRef?> ResolveVersionRefAsync(string workspace, string version, IReadOnlyList<string> patterns, CancellationToken cancellationToken = default);

    /// <summary>"abc1234 2026-03-01 subject" for commits reachable from <paramref name="refName"/> mentioning any of the keys.</summary>
    Task<IReadOnlyList<string>> FindCommitsMentioningAsync(string workspace, string refName, IReadOnlyList<string> keys, CancellationToken cancellationToken = default);

    /// <summary>Matching lines from files at <paramref name="refName"/> (never the working tree), formatted "--- path" + lines.</summary>
    Task<string> GrepAsync(string workspace, string refName, IReadOnlyList<string> keywords, int maxFiles, CancellationToken cancellationToken = default);

    Task<BranchResult> CreateBranchAsync(string workspace, string branchName, string fromRef, CancellationToken cancellationToken = default);
}
