namespace AI.BugInvestigationTool.Models;

public sealed class JiraIssue
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Url { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Priority { get; set; } = "";
    public string Status { get; set; } = "";
    public string IssueType { get; set; } = "";
    public List<string> AffectedVersions { get; set; } = [];
    public List<string> FixVersions { get; set; } = [];
    public List<string> Components { get; set; } = [];
    public List<string> Labels { get; set; } = [];
    public string? Assignee { get; set; }
    public string? Reporter { get; set; }
    public string? Parent { get; set; }
    public List<string> Subtasks { get; set; } = [];
    public List<string> LinkedIssues { get; set; } = [];
    public List<string> Sprints { get; set; } = [];
    public List<JiraComment> Comments { get; set; } = [];
    public List<JiraAttachment> Attachments { get; set; } = [];
    public List<string> History { get; set; } = [];
    /// <summary>Custom fields by display name (resolved through the issue's "names" map, not hard-coded IDs).</summary>
    public Dictionary<string, string> CustomFields { get; set; } = new();
}

public sealed class JiraComment
{
    public string Author { get; set; } = "";
    public DateTimeOffset? Created { get; set; }
    public string Body { get; set; } = "";
}

public sealed class JiraAttachment
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public string ContentUrl { get; set; } = "";
    /// <summary>Local path relative to the evidence folder once downloaded.</summary>
    public string? LocalFile { get; set; }
    /// <summary>Extracted text for text-like attachments (truncated).</summary>
    public string? Text { get; set; }
}

public sealed class JiraTransition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ToStatus { get; set; } = "";
}

public sealed class PullRequestData
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Status { get; set; } = "";
    public string SourceBranch { get; set; } = "";
    public string TargetBranch { get; set; } = "";
    public string SourceCommit { get; set; } = "";
    public string BaseCommit { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public List<ChangedFile> Files { get; set; } = [];
    public List<string> LinkedWorkItems { get; set; } = [];
    public List<string> LinkedJiraKeys { get; set; } = [];
    public int Additions => Files.Sum(f => f.Additions);
    public int Deletions => Files.Sum(f => f.Deletions);
}

public sealed class ChangedFile
{
    public string Path { get; set; } = "";
    public string ChangeType { get; set; } = "";
    public int Additions { get; set; }
    public int Deletions { get; set; }
    /// <summary>Unified diff with real line numbers. Empty when the file was skipped (binary/too large).</summary>
    public string Diff { get; set; } = "";
    /// <summary>New-side line ranges touched by the change, used to validate Claude's line references.</summary>
    public List<LineRange> ChangedRanges { get; set; } = [];
    public string? SkippedReason { get; set; }
}

public sealed class RepositoryInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DefaultBranch { get; set; } = "";
}

public sealed class CodeSearchHit
{
    public string Repository { get; set; } = "";
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed record LineRange(int Start, int End);
