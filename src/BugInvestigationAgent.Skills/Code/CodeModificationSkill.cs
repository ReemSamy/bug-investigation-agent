using System.Text;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Skills;

namespace BugInvestigationAgent.Skills.Code;

public sealed class CodeModificationSkill : ISkill
{
    public string SkillId => SkillIds.CodeModification;
    public string DisplayName => "Code Modification";
    public AgentPermission RequiredPermissions => AgentPermission.ModifyCode;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions)
        && context.FixApproved
        && !string.IsNullOrWhiteSpace(context.WorkspacePath);

    public Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        // Defense in depth: even if called directly, never write to the target repository without the permission.
        if (request.Permissions is null || !request.Permissions.IsAllowed(AgentPermission.ModifyCode))
            return Task.FromResult(SkillResult.Fail("Code modification is disabled (read-only investigation)."));
        if (!ctx.FixApproved)
            return Task.FromResult(SkillResult.Fail("Fix has not been approved."));

        var apply = request.Inputs is not null &&
                    request.Inputs.TryGetValue("apply", out var a) &&
                    string.Equals(a, "true", StringComparison.OrdinalIgnoreCase);
        if (!apply)
            return Task.FromResult(SkillResult.Fail("Apply flag was not set."));

        if (string.IsNullOrWhiteSpace(ctx.ProposedDiff))
        {
            request.Progress?.Report("No machine-applicable diff was proposed; leaving workspace unchanged.");
            ctx.AppliedDiff = "";
            return Task.FromResult(SkillResult.Ok("No diff to apply."));
        }

        try
        {
            var applied = ApplyUnifiedDiff(ctx.WorkspacePath!, ctx.ProposedDiff);
            ctx.AppliedDiff = applied;
            request.Progress?.Report(applied);
            return Task.FromResult(SkillResult.Ok(applied));
        }
        catch (Exception ex)
        {
            return Task.FromResult(SkillResult.Fail($"Failed to apply diff: {ex.Message}"));
        }
    }

    /// <summary>
    /// Minimal unified-diff applier for single-file hunks. Safe no-op if parsing fails.
    /// </summary>
    internal static string ApplyUnifiedDiff(string workspace, string diff)
    {
        var fileMatch = Regex.Match(diff, @"^\+\+\+\s+[ab]/(.+)$", RegexOptions.Multiline);
        if (!fileMatch.Success)
            return "Diff did not contain a recognizable +++ file header; no files changed.";

        var relative = fileMatch.Groups[1].Value.Trim();
        var fullPath = Path.GetFullPath(Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to write outside the workspace.");

        if (!File.Exists(fullPath))
            return $"Target file not found: {relative}; no files changed.";

        var original = File.ReadAllLines(fullPath).ToList();
        var hunks = Regex.Split(diff, @"(?=^@@ )", RegexOptions.Multiline)
            .Where(h => h.StartsWith("@@", StringComparison.Ordinal))
            .ToList();

        if (hunks.Count == 0)
            return "No hunks found; no files changed.";

        var working = original.ToList();
        foreach (var hunk in hunks)
        {
            var header = Regex.Match(hunk, @"@@\s+-(\d+)(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@");
            if (!header.Success)
                continue;
            var start = int.Parse(header.Groups[1].Value) - 1;
            var lines = hunk.Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).ToList();
            var newSegment = new List<string>();
            var oldCount = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("+", StringComparison.Ordinal))
                    newSegment.Add(line[1..]);
                else if (line.StartsWith("-", StringComparison.Ordinal))
                    oldCount++;
                else if (line.StartsWith(" ", StringComparison.Ordinal))
                {
                    newSegment.Add(line[1..]);
                    oldCount++;
                }
                else if (string.IsNullOrEmpty(line))
                {
                    // ignore
                }
            }

            if (start < 0 || start > working.Count)
                continue;
            var remove = Math.Min(oldCount, working.Count - start);
            working.RemoveRange(start, remove);
            working.InsertRange(start, newSegment);
        }

        File.WriteAllLines(fullPath, working, Encoding.UTF8);
        return $"Applied diff to {relative}.";
    }
}
