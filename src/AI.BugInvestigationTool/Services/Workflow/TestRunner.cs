using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AI.BugInvestigationTool.Models;

namespace AI.BugInvestigationTool.Services.Workflow;

/// <summary>
/// Runs existing tests related to a PR's changed files in a user-configured local clone.
/// The command is fixed (dotnet test &lt;project&gt;) and never influenced by Claude. The working tree is not modified:
/// tests run against whatever is checked out, and the report states whether that matches the PR commit.
/// </summary>
public static partial class TestRunner
{
    [GeneratedRegex(@"(Passed|Failed)!\s*-\s*Failed:\s*(?<f>\d+),\s*Passed:\s*(?<p>\d+),\s*Skipped:\s*(?<s>\d+)")]
    private static partial Regex SummaryRegex();

    public static List<string> FindRelatedTestProjects(string repoRoot, IEnumerable<string> changedPaths)
    {
        var allProjects = Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        var testProjects = allProjects.Where(p => Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase)).ToList();

        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var changed in changedPaths)
        {
            var full = Path.GetFullPath(Path.Combine(repoRoot, changed.TrimStart('/', '\\')));
            // Nearest project containing the changed file.
            var owner = allProjects.Where(p => full.StartsWith(Path.GetDirectoryName(p)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Length).FirstOrDefault();
            if (owner is null) continue;
            var ownerName = Path.GetFileNameWithoutExtension(owner);
            if (testProjects.Contains(owner)) { related.Add(owner); continue; }
            foreach (var t in testProjects.Where(t => Path.GetFileNameWithoutExtension(t).StartsWith(ownerName + ".", StringComparison.OrdinalIgnoreCase) ||
                                                      Path.GetFileNameWithoutExtension(t).Equals(ownerName + "Tests", StringComparison.OrdinalIgnoreCase)))
                related.Add(t);
        }
        return related.OrderBy(p => p).ToList();
    }

    public static async Task<TestRunResult> RunAsync(string? repoRoot, PullRequestData pr, string evidenceDir, int timeoutSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            return new() { Summary = "No local repository configured (Tool:LocalRepositoryPath); existing tests were not run." };
        if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
            return new() { Summary = $"'{repoRoot}' is not a git repository; tests were not run." };

        var head = (await RunProcessAsync("git", ["-C", repoRoot, "rev-parse", "HEAD"], repoRoot, 30, ct)).Output.Trim();
        var projects = FindRelatedTestProjects(repoRoot, pr.Files.Select(f => f.Path));
        if (projects.Count == 0)
            return new() { Summary = "No test projects related to the changed files were found in the local repository." };

        var output = new StringBuilder();
        var summaries = new List<string>();
        int? exit = 0;
        foreach (var project in projects.Take(5))
        {
            var run = await RunProcessAsync("dotnet", ["test", project, "--nologo", "--verbosity", "minimal"], repoRoot, timeoutSeconds, ct);
            output.AppendLine($"===== dotnet test {project} (exit {run.ExitCode}) =====").AppendLine(run.Output);
            var m = SummaryRegex().Match(run.Output);
            summaries.Add($"{Path.GetFileNameWithoutExtension(project)}: " + (m.Success
                ? $"passed {m.Groups["p"].Value}, failed {m.Groups["f"].Value}, skipped {m.Groups["s"].Value}"
                : run.TimedOut ? "timed out" : $"exit code {run.ExitCode}"));
            if (run.ExitCode != 0) exit = run.ExitCode;
        }

        Directory.CreateDirectory(evidenceDir);
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, "test-output.txt"), output.ToString(), ct);
        var matches = head.Length > 0 && pr.SourceCommit.StartsWith(head, StringComparison.OrdinalIgnoreCase);
        var note = matches ? "Local HEAD matches the PR source commit." :
            $"WARNING: local HEAD {head[..Math.Min(10, head.Length)]} is not the PR source commit {pr.SourceCommit[..Math.Min(10, pr.SourceCommit.Length)]}; results reflect the local checkout, not the PR.";
        return new() { Executed = true, ExitCode = exit, OutputFile = "test-output.txt", Summary = $"{string.Join("; ", summaries)}. {note}" };
    }

    internal sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);

    internal static async Task<ProcessResult> RunProcessAsync(string file, IEnumerable<string> args, string workDir, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Exception ex) { return new(-1, $"Could not start {file}: {ex.Message}", false); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
            return new(p.ExitCode, sb.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            return new(-1, sb.ToString(), true);
        }
    }
}
