namespace BugInvestigationAgent.Core.Rules;

/// <summary>
/// Explicit engineering instructions (not skills, not memory).
/// </summary>
public interface IRulesProvider
{
    Task<IReadOnlyList<string>> GetAppSafetyRulesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> LoadWorkspaceRulesAsync(string? workspacePath, CancellationToken cancellationToken = default);
}

public sealed class DefaultRulesProvider : IRulesProvider
{
    public Task<IReadOnlyList<string>> GetAppSafetyRulesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> rules =
        [
            "Never include passwords, tokens, or API keys in prompts, logs, reports, or Jira comments.",
            "Ask only for information that is missing after inspecting the Jira ticket.",
            "Reproduce using the same user/role specified by the ticket whenever possible.",
            "Do not report NOT_REPRODUCED when the failure was access, environment, browser, or technical.",
            "Propose a fix and obtain explicit approval before modifying code.",
            "Make the smallest reasonable change; no unrelated refactoring or dependency upgrades.",
            "Run only allowlisted validation commands.",
            "Do not commit, push, create PRs, trigger CI, or deploy unless those permissions are explicitly enabled.",
            "For Bootstrap UI work, prefer Bootstrap 5 patterns and never introduce Bootstrap 3 obsolete patterns.",
            "Respect workspace architecture and coding conventions found in project rule/documentation files."
        ];
        return Task.FromResult(rules);
    }

    public async Task<IReadOnlyList<string>> LoadWorkspaceRulesAsync(string? workspacePath, CancellationToken cancellationToken = default)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return results;

        var candidates = new List<string>();
        void AddIfExists(string path)
        {
            if (File.Exists(path))
                candidates.Add(path);
        }

        AddIfExists(Path.Combine(workspacePath, "AGENTS.md"));
        AddIfExists(Path.Combine(workspacePath, "CLAUDE.md"));
        AddIfExists(Path.Combine(workspacePath, "UI-STYLE-GUIDE.md"));

        var cursorRules = Path.Combine(workspacePath, ".cursor", "rules");
        if (Directory.Exists(cursorRules))
        {
            candidates.AddRange(Directory.EnumerateFiles(cursorRules, "*.md", SearchOption.AllDirectories));
            candidates.AddRange(Directory.EnumerateFiles(cursorRules, "*.mdc", SearchOption.AllDirectories));
        }

        foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(40))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = await File.ReadAllTextAsync(file, cancellationToken);
                if (text.Length > 12_000)
                    text = text[..12_000] + "\n...[truncated]...";
                results.Add($"# From {Path.GetRelativePath(workspacePath, file)}\n{text}");
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return results;
    }
}
