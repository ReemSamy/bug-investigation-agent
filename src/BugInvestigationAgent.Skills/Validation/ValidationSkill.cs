using System.Diagnostics;
using System.Text;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Skills.Validation;

public sealed class ValidationSkill : ISkill
{
    private readonly AgentOptions _options;
    private readonly ISecretRedactor _redactor;

    public ValidationSkill(IOptions<AgentOptions> options, ISecretRedactor redactor)
    {
        _options = options.Value;
        _redactor = redactor;
    }

    public string SkillId => SkillIds.Validation;
    public string DisplayName => "Validation";
    public AgentPermission RequiredPermissions => AgentPermission.RunValidation;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions) && !string.IsNullOrWhiteSpace(context.WorkspacePath);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        var workspace = ctx.WorkspacePath!;
        ctx.ValidationResults.Clear();

        var allowlist = _options.ValidationCommandAllowlist ?? new List<string>();
        if (allowlist.Count == 0)
        {
            request.Progress?.Report("No validation commands are allowlisted.");
            return SkillResult.Ok("Validation skipped — empty allowlist.");
        }

        foreach (var command in allowlist)
        {
            if (!IsAllowlisted(command, allowlist))
            {
                request.Progress?.Report($"Rejected non-allowlisted command: {command}");
                continue;
            }

            request.Progress?.Report($"Running: {command}");
            var result = await RunCommandAsync(command, workspace, cancellationToken);
            result.StdOut = _redactor.Redact(result.StdOut);
            result.StdErr = _redactor.Redact(result.StdErr);
            ctx.ValidationResults.Add(result);
        }

        var failed = ctx.ValidationResults.Any(r => !r.Succeeded);
        return failed
            ? SkillResult.Ok("Validation completed with failures.")
            : SkillResult.Ok("Validation completed successfully.");
    }

    public static bool IsAllowlisted(string command, IEnumerable<string> allowlist)
    {
        var normalized = command.Trim();
        return allowlist.Any(a =>
            normalized.Equals(a.Trim(), StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(a.Trim() + " ", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ValidationResultItem> RunCommandAsync(string command, string workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-lc \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return new ValidationResultItem
        {
            Command = command,
            ExitCode = process.ExitCode,
            StdOut = Truncate(stdout.ToString(), 8000),
            StdErr = Truncate(stderr.ToString(), 4000)
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n...[truncated]...";
}
