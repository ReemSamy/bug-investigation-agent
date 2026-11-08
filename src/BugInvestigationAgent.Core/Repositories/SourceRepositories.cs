namespace BugInvestigationAgent.Core.Repositories;

/// <summary>Repository to investigate, resolved from Jira project/component via configuration.</summary>
public sealed class RepositorySelection
{
    public required string Provider { get; init; }          // "AzureDevOps"
    public required string Organization { get; init; }
    public required string Project { get; init; }
    public required string Repository { get; init; }
    public required string Branch { get; init; }
    public string? ApplicationId { get; init; }
    public string Display => $"{Organization}/{Project}/{Repository}@{Branch}";
}

public sealed class CodeSearchHit
{
    public required string Path { get; init; }
    public string? Snippet { get; init; }
}

public sealed class CommitInfo
{
    public required string CommitId { get; init; }
    public string? Comment { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset? Date { get; init; }
    public string? Url { get; init; }
}

/// <summary>Read-only source repository access (initial rollout: no push, no PR, no branch creation).</summary>
public interface ISourceRepositoryProvider
{
    string ProviderId { get; }
    /// <summary>Shallow, read-only checkout of the branch into <paramref name="targetDirectory"/>.</summary>
    Task CheckoutAsync(RepositorySelection selection, string targetDirectory, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CodeSearchHit>> SearchCodeAsync(RepositorySelection selection, string query, int max = 10, CancellationToken cancellationToken = default);
    /// <summary>Commits on the branch whose message mentions the Jira key (e.g. "VKMS-123").</summary>
    Task<IReadOnlyList<CommitInfo>> FindCommitsMentioningAsync(RepositorySelection selection, string issueKey, int max = 10, CancellationToken cancellationToken = default);
}

public sealed class RepositoryMappingOptions
{
    public const string SectionName = "Repositories";
    public List<RepositoryMapping> Mappings { get; set; } = new();
    /// <summary>Root folder for per-investigation checkouts (deleted after the job).</summary>
    public string? CheckoutRoot { get; set; }

    public string CheckoutDirectoryFor(Guid investigationId) =>
        Path.Combine(
            string.IsNullOrWhiteSpace(CheckoutRoot) ? Path.Combine(Path.GetTempPath(), "bia-checkouts") : CheckoutRoot,
            investigationId.ToString("N"));
}

public sealed class RepositoryMapping
{
    /// <summary>Jira project key, e.g. "VKMS". "*" matches any project.</summary>
    public string JiraProject { get; set; } = "*";
    /// <summary>Optional Jira component name; when set the mapping only applies to issues with that component.</summary>
    public string? Component { get; set; }
    public string? ApplicationId { get; set; }
    public string Provider { get; set; } = "AzureDevOps";
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Branch { get; set; } = "main";
}

public interface IRepositoryResolver
{
    RepositorySelection? Resolve(string? issueKey, IEnumerable<string>? components, string? applicationId);
}

public sealed class ConfiguredRepositoryResolver : IRepositoryResolver
{
    private readonly Func<RepositoryMappingOptions> _options;

    public ConfiguredRepositoryResolver(Microsoft.Extensions.Options.IOptionsMonitor<RepositoryMappingOptions> options) => _options = () => options.CurrentValue;
    public ConfiguredRepositoryResolver(RepositoryMappingOptions options) => _options = () => options;

    public RepositorySelection? Resolve(string? issueKey, IEnumerable<string>? components, string? applicationId)
    {
        var project = issueKey?.Split('-')[0];
        var componentSet = new HashSet<string>(components ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var candidates = _options().Mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.Organization) && !string.IsNullOrWhiteSpace(m.Project) && !string.IsNullOrWhiteSpace(m.Repository))
            .Where(m => m.JiraProject == "*" || string.Equals(m.JiraProject, project, StringComparison.OrdinalIgnoreCase))
            .Where(m => string.IsNullOrWhiteSpace(m.Component) || componentSet.Contains(m.Component))
            .Where(m => string.IsNullOrWhiteSpace(m.ApplicationId) || string.IsNullOrWhiteSpace(applicationId)
                        || string.Equals(m.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase))
            // Most specific first: component match, then exact project, then application.
            .OrderByDescending(m => string.IsNullOrWhiteSpace(m.Component) ? 0 : 4)
            .ThenByDescending(m => m.JiraProject == "*" ? 0 : 2)
            .ThenByDescending(m => string.IsNullOrWhiteSpace(m.ApplicationId) ? 0 : 1)
            .ToList();

        var best = candidates.FirstOrDefault();
        return best is null
            ? null
            : new RepositorySelection
            {
                Provider = best.Provider,
                Organization = best.Organization,
                Project = best.Project,
                Repository = best.Repository,
                Branch = string.IsNullOrWhiteSpace(best.Branch) ? "main" : best.Branch,
                ApplicationId = best.ApplicationId
            };
    }
}
