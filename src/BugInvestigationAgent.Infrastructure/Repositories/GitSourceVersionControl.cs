using System.Text;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Repositories;

namespace BugInvestigationAgent.Infrastructure.Repositories;

/// <summary>
/// Git access for the local KMS clone through an explicit allowlist. Read commands: rev-parse, for-each-ref, log,
/// grep, check-ref-format. The single write is <c>git branch --no-track &lt;new-name&gt; &lt;ref&gt;</c> for a name that does
/// not exist yet. Anything else (checkout, switch, commit, push, fetch, reset, stash, worktree, merge, rebase, branch
/// -f/-d/-m …) is refused before a process is started.
/// </summary>
public sealed class GitSourceVersionControl : ISourceVersionControl
{
    private static readonly HashSet<string> ReadCommands = new(StringComparer.Ordinal) { "rev-parse", "for-each-ref", "log", "grep", "check-ref-format" };
    private static readonly Regex SafeRef = new(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,200}$", RegexOptions.Compiled);
    private readonly IProcessRunner _process;

    public GitSourceVersionControl(IProcessRunner process) => _process = process;

    /// <summary>Throws unless the git arguments are on the allowlist. Public for tests.</summary>
    public static void EnsureAllowed(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new InvalidOperationException("Empty git command.");
        var command = args[0];
        if (ReadCommands.Contains(command))
        {
            // No option that writes: e.g. "log --output", "grep -O" (opens a pager/editor), "rev-parse" has none.
            if (args.Any(a => a.StartsWith("--output", StringComparison.Ordinal) || a is "-O" || a.StartsWith("--open-files-in-pager", StringComparison.Ordinal)))
                throw new InvalidOperationException($"git {command} option not allowed.");
            return;
        }
        if (command == "branch" && args.Count == 4 && args[1] == "--no-track" && SafeRef.IsMatch(args[2]) && SafeRef.IsMatch(args[3]))
            return; // create one new branch at an existing ref
        throw new InvalidOperationException($"git {string.Join(' ', args)} is not allowed (read-only investigation; only new local branches may be created).");
    }

    public async Task<VersionRef?> ResolveVersionRefAsync(string workspace, string version, IReadOnlyList<string> patterns, CancellationToken cancellationToken = default)
    {
        var parts = version.Split('.');
        string Fill(string pattern) => pattern
            .Replace("{version}", version)
            .Replace("{major}", parts.ElementAtOrDefault(0) ?? "")
            .Replace("{minor}", parts.ElementAtOrDefault(1) ?? "")
            .Replace("{patch}", parts.ElementAtOrDefault(2) ?? "");

        var (code, output) = await RunAsync(workspace, cancellationToken, "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes");
        if (code != 0)
            throw new InvalidOperationException($"{workspace} is not a readable Git repository: {output}");
        var existing = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns)
        {
            if (pattern.Contains("{patch}") && parts.Length < 3)
                continue;
            var candidate = Fill(pattern);
            var match = existing.FirstOrDefault(e => string.Equals(e, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is null || !SafeRef.IsMatch(match))
                continue;
            var (c2, commit) = await RunAsync(workspace, cancellationToken, "rev-parse", "--verify", match + "^{commit}");
            if (c2 == 0)
                return new VersionRef(match, commit.Trim(), pattern);
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> FindCommitsMentioningAsync(string workspace, string refName, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        if (!SafeRef.IsMatch(refName) || keys.Count == 0)
            return Array.Empty<string>();
        var args = new List<string> { "log", refName, "--format=%h %cs %s", "-n", "30", "--regexp-ignore-case", "--fixed-strings" };
        foreach (var k in keys.Where(k => Regex.IsMatch(k, @"^[A-Z][A-Z0-9]+-\d+$", RegexOptions.IgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            args.Add("--grep=" + k);
        if (!args.Any(a => a.StartsWith("--grep=", StringComparison.Ordinal)))
            return Array.Empty<string>();
        var (code, output) = await RunAsync(workspace, cancellationToken, args.ToArray());
        return code == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
    }

    public async Task<string> GrepAsync(string workspace, string refName, IReadOnlyList<string> keywords, int maxFiles, CancellationToken cancellationToken = default)
    {
        var terms = keywords.Where(k => k.Length >= 3 && !k.StartsWith('-')).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        if (!SafeRef.IsMatch(refName) || terms.Count == 0)
            return "";
        var args = new List<string> { "grep", "-n", "-I", "-i", "--fixed-strings", "--max-count=6" };
        foreach (var t in terms)
            args.AddRange(new[] { "-e", t });
        args.AddRange(new[] { refName, "--", ":(exclude)**/bin/**", ":(exclude)**/obj/**", ":(exclude)**/node_modules/**", ":(exclude)**/*.min.js" });
        var (code, output) = await RunAsync(workspace, cancellationToken, args.ToArray());
        if (code is not (0 or 1)) // 1 = no match
            return "";

        // Lines look like "<ref>:<path>:<line>:<text>".
        var prefix = refName + ":";
        var byFile = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l[prefix.Length..])
            .Select(l => (File: l[..Math.Max(0, l.IndexOf(':'))], Text: l[(l.IndexOf(':') + 1)..]))
            .Where(x => x.File.Length > 0)
            .GroupBy(x => x.File)
            .OrderByDescending(g => g.Count())
            .Take(maxFiles);
        var sb = new StringBuilder();
        foreach (var g in byFile)
        {
            sb.AppendLine("--- " + g.Key);
            foreach (var line in g.Take(6).Select(x => x.Text))
                sb.AppendLine(line.Length > 240 ? line[..240] : line);
        }
        return sb.ToString();
    }

    public async Task<BranchResult> CreateBranchAsync(string workspace, string branchName, string fromRef, CancellationToken cancellationToken = default)
    {
        if (!SafeRef.IsMatch(branchName) || !SafeRef.IsMatch(fromRef))
            return new(BranchCreation.Refused, branchName, "Branch or source ref name is not allowed.");
        var (validCode, _) = await RunAsync(workspace, cancellationToken, "check-ref-format", "--branch", branchName);
        if (validCode != 0)
            return new(BranchCreation.Refused, branchName, $"'{branchName}' is not a valid branch name.");
        var (existsCode, existing) = await RunAsync(workspace, cancellationToken, "rev-parse", "--verify", "--quiet", "refs/heads/" + branchName);
        if (existsCode == 0)
            return new(BranchCreation.AlreadyExists, branchName, $"Local branch {branchName} already exists (at {existing.Trim()[..Math.Min(10, existing.Trim().Length)]}); it was not changed.");

        var (code, output) = await RunAsync(workspace, cancellationToken, "branch", "--no-track", branchName, fromRef);
        if (code != 0)
            return new(BranchCreation.Refused, branchName, "git refused to create the branch: " + output.Trim());
        return new(BranchCreation.Created, branchName, $"Created local branch {branchName} from {fromRef} (not checked out, nothing committed or pushed).");
    }

    private async Task<(int Code, string Output)> RunAsync(string workspace, CancellationToken cancellationToken, params string[] args)
    {
        EnsureAllowed(args);
        var full = new List<string> { "-C", workspace, "-c", "core.pager=cat", "-c", "color.ui=false" };
        full.AddRange(args);
        var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_OPTIONAL_LOCKS"] = "0" };
        var (code, output) = await _process.RunAsync("git", full, env, TimeSpan.FromSeconds(60), cancellationToken);
        return (code, output);
    }
}
