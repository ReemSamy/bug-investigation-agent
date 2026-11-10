using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Repositories;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Infrastructure.Repositories;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Infrastructure.Settings;

public sealed record DiagnosticResult(string Area, bool Ok, string Detail);

/// <summary>
/// Read-only self-checks for the local app's integrations. Nothing here writes to Jira, the source repository or
/// any environment; the LLM check sends a fixed text that contains no company data.
/// </summary>
public sealed class IntegrationDiagnostics
{
    private readonly IEnumerable<IAgentRuntime> _runtimes;
    private readonly ILlmSettingsSource _llm;
    private readonly IWorkspaceDefaults _workspace;
    private readonly IOptions<AgentOptions> _agent;
    private readonly IOptions<RepositoryMappingOptions> _repositories;
    private readonly IEnvironmentRegistry _environments;
    private readonly IPermissionGate _permissions;
    private readonly IProcessRunner _process;
    private readonly ISecretRedactor _redactor;

    public IntegrationDiagnostics(IEnumerable<IAgentRuntime> runtimes, ILlmSettingsSource llm, IWorkspaceDefaults workspace,
        IOptions<AgentOptions> agent, IOptions<RepositoryMappingOptions> repositories, IEnvironmentRegistry environments,
        IPermissionGate permissions, IProcessRunner process, ISecretRedactor redactor)
    {
        _runtimes = runtimes;
        _llm = llm;
        _workspace = workspace;
        _agent = agent;
        _repositories = repositories;
        _environments = environments;
        _permissions = permissions;
        _process = process;
        _redactor = redactor;
    }

    /// <summary>Sends "Reply with OK" to the selected provider. No ticket text or code is included.</summary>
    public async Task<DiagnosticResult> TestLlmAsync(CancellationToken cancellationToken = default)
    {
        var id = _llm.ActiveProviderId;
        var runtime = _runtimes.FirstOrDefault(r => string.Equals(r.ProviderId, id, StringComparison.OrdinalIgnoreCase));
        if (runtime is null || id is "null" or "cursor" or "azure-openai")
            return new("LLM", false, $"Provider '{id}' cannot be tested: choose anthropic or openai-compatible.");
        var started = DateTimeOffset.UtcNow;
        try
        {
            var result = await runtime.CompleteAsync(new AgentTurnRequest
            {
                SystemPrompt = "This is a connectivity check.",
                Messages = [new AgentMessage { Role = "user", Content = "Reply with the single word OK." }],
                MaxTokens = 64
            }, cancellationToken);
            var model = id == "anthropic" ? _llm.Current.Anthropic.Model : _llm.Current.OpenAiCompatible.Model;
            var ms = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            return new("LLM", true, $"'{id}' answered with model '{model}' in {ms:0} ms (reply: \"{Short(result.Content)}\")." +
                (_llm.ExternalDataSharingApproved ? "" : " Investigations will still NOT use it until company approval is recorded."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new("LLM", false, $"'{id}' failed: {_redactor.Redact(ex.Message)}");
        }
    }

    public async Task<IReadOnlyList<DiagnosticResult>> CheckLocalAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DiagnosticResult>();

        var forbidden = ReadOnlyRollout.Forbidden;
        results.Add(new("Read-only", (_permissions.Allowed & forbidden) == 0,
            (_permissions.Allowed & forbidden) == 0
                ? "Code modification, validation commands, Git and deployment are disabled at runtime."
                : "WARNING: a code-changing permission is enabled."));

        // Workspace (local source clone used for code analysis) — read-only git commands only.
        var path = _workspace.DefaultWorkspacePath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            results.Add(new("Source workspace", false, string.IsNullOrWhiteSpace(path) ? "No workspace folder is set." : $"Folder not found: {path}"));
        else
        {
            var branch = await Git(path, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD");
            var head = await Git(path, cancellationToken, "log", "-1", "--format=%h %cs");
            var dirty = await Git(path, cancellationToken, "status", "--porcelain");
            var remote = await Git(path, cancellationToken, "remote", "get-url", "origin");
            results.Add(branch.ok
                ? new("Source workspace", true,
                    $"{path} — branch {branch.output}, HEAD {head.output}, {(string.IsNullOrWhiteSpace(dirty.output) ? "no local changes" : "has local changes")}, " +
                    $"origin {RemoteHost(remote.output)}. The agent only reads this folder.")
                : new("Source workspace", false, $"{path} is not a Git repository ({branch.output})."));
        }

        var mappings = _repositories.Value.Mappings;
        results.Add(new("Azure DevOps mapping", mappings.Any(m => !string.IsNullOrWhiteSpace(m.Organization) && !string.IsNullOrWhiteSpace(m.Repository)),
            mappings.Count == 0
                ? "No Repositories:Mappings configured. The local app uses the source workspace above instead (Azure DevOps checkout is used by the hosted worker)."
                : string.Join("; ", mappings.Select(m => $"{m.JiraProject} → {(string.IsNullOrWhiteSpace(m.Organization) ? "(organization not set)" : $"{m.Organization}/{m.Project}/{m.Repository}@{m.Branch}")}"))));

        var docs = _agent.Value.KmsDocs;
        if (string.IsNullOrWhiteSpace(docs.RootPath) || !Directory.Exists(docs.RootPath))
            results.Add(new("KMS documentation", false,
                string.IsNullOrWhiteSpace(docs.RootPath) ? "Agent:KmsDocs:RootPath is not set — documentation retrieval is skipped." : $"Folder not found: {docs.RootPath}"));
        else
        {
            var count = Directory.EnumerateFiles(docs.RootPath, "*.md", SearchOption.AllDirectories).Take(10_000).Count();
            results.Add(new("KMS documentation", count > 0, $"{docs.RootPath}: {count} markdown file(s)."));
        }

        var envs = _environments.List().ToList();
        results.Add(new("Environments", envs.Any(e => e.Enabled && !e.IsProduction && !string.IsNullOrWhiteSpace(e.BaseUrl)),
            envs.Count == 0
                ? "No environments registered."
                : string.Join("; ", envs.Select(e => $"{e.DisplayName} ({e.Type}{(e.IsProduction ? ", PRODUCTION — read-only" : "")}): " +
                                                     (string.IsNullOrWhiteSpace(e.BaseUrl) ? "no URL" : e.SafeBaseUrl) +
                                                     (e.Enabled ? "" : ", disabled") + (e.Login.CanVerifyLogin ? "" : ", sign-in not verifiable")))
                  + (_environments.AllowAdHocUrl ? "; ad-hoc URLs allowed" + (_environments.TreatAdHocAsProduction ? " (treated as Production)" : "") : "")));

        results.Add(new("LLM configuration", (_llm.ActiveProviderId is "anthropic" or "openai-compatible") && _llm.ExternalDataSharingApproved,
            $"Provider: {_llm.ActiveProviderId}. Company approval to send data: {(_llm.ExternalDataSharingApproved ? "recorded" : "NOT recorded — investigations run without the LLM")}."));
        return results;
    }

    private async Task<(bool ok, string output)> Git(string path, CancellationToken cancellationToken, params string[] args)
    {
        try
        {
            var (code, output) = await _process.RunAsync("git", ["-C", path, .. args], new Dictionary<string, string>(), TimeSpan.FromSeconds(20), cancellationToken);
            return (code == 0, output.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }

    private static string RemoteHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host}{u.AbsolutePath}" : "(none)"; // never echo credentials in URLs

    private static string Short(string s) => s.Length <= 40 ? s.Trim() : s[..40].Trim() + "…";
}
