using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using BugInvestigationAgent.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace BugInvestigationAgent.Infrastructure.Repositories;

public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";
    /// <summary>Client id of the user-assigned managed identity used for Azure DevOps (optional; system-assigned otherwise).</summary>
    public string? ManagedIdentityClientId { get; set; }
    public string ServiceHost { get; set; } = "https://dev.azure.com";
    public string SearchHost { get; set; } = "https://almsearch.dev.azure.com";
    public int CheckoutTimeoutSeconds { get; set; } = 300;
}

/// <summary>Runs external processes (git). Abstracted for tests; secrets are passed via environment, never arguments.</summary>
public interface IProcessRunner
{
    Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in environment)
            psi.Environment[k] = v;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}

/// <summary>
/// Read-only Azure DevOps Repos provider. Authenticates with an Entra ID token (managed identity in Azure — no
/// personal access tokens). Grant the identity only "Reader" on the mapped repositories (and Code Search access).
/// </summary>
public sealed class AzureDevOpsRepositoryProvider : ISourceRepositoryProvider
{
    /// <summary>Entra resource id of Azure DevOps.</summary>
    public const string AzureDevOpsScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _http;
    private readonly IProcessRunner _process;
    private readonly AzureDevOpsOptions _options;
    private readonly ILogger<AzureDevOpsRepositoryProvider> _logger;

    public AzureDevOpsRepositoryProvider(
        TokenCredential credential,
        IHttpClientFactory http,
        IProcessRunner process,
        Microsoft.Extensions.Options.IOptions<AzureDevOpsOptions> options,
        ILogger<AzureDevOpsRepositoryProvider> logger)
    {
        _credential = credential;
        _http = http;
        _process = process;
        _options = options.Value;
        _logger = logger;
    }

    public string ProviderId => "AzureDevOps";

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
        (await _credential.GetTokenAsync(new TokenRequestContext([AzureDevOpsScope]), cancellationToken)).Token;

    private string Seg(string s) => Uri.EscapeDataString(s);

    public string CloneUrl(RepositorySelection s) =>
        $"{_options.ServiceHost.TrimEnd('/')}/{Seg(s.Organization)}/{Seg(s.Project)}/_git/{Seg(s.Repository)}";

    public async Task CheckoutAsync(RepositorySelection selection, string targetDirectory, CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetDirectory))!);

        // Token goes through git's environment-based config (GIT_CONFIG_*), never the command line or remote URL,
        // so it is not visible in process listings and is not persisted in .git/config.
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: bearer " + token
        };
        var args = new List<string>
        {
            "clone", "--depth", "1", "--single-branch", "--no-tags",
            "--branch", selection.Branch,
            CloneUrl(selection),
            targetDirectory
        };

        var (exit, output) = await _process.RunAsync("git", args, env, TimeSpan.FromSeconds(_options.CheckoutTimeoutSeconds), cancellationToken);
        if (exit != 0)
        {
            var safe = output.Replace(token, "[REDACTED]", StringComparison.Ordinal);
            _logger.LogWarning("git clone of {Repo} failed with exit code {Exit}.", selection.Display, exit);
            throw new InvalidOperationException($"Checkout of {selection.Display} failed (exit {exit}): {Truncate(safe, 300)}");
        }
    }

    public async Task<IReadOnlyList<CodeSearchHit>> SearchCodeAsync(RepositorySelection selection, string query, int max = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<CodeSearchHit>();

        var client = await ClientAsync(cancellationToken);
        var url = $"{_options.SearchHost.TrimEnd('/')}/{Seg(selection.Organization)}/{Seg(selection.Project)}/_apis/search/codesearchresults?api-version=7.1";
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["searchText"] = query,
            ["$top"] = Math.Clamp(max, 1, 50),
            ["filters"] = new Dictionary<string, string[]>
            {
                ["Repository"] = [selection.Repository],
                ["Branch"] = [selection.Branch]
            }
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(url, content, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogInformation("Azure DevOps code search returned HTTP {Status} for {Repo}.", (int)resp.StatusCode, selection.Display);
            return Array.Empty<CodeSearchHit>();
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("results", out var results))
            return Array.Empty<CodeSearchHit>();
        return results.EnumerateArray()
            .Select(r => new CodeSearchHit
            {
                Path = r.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                Snippet = r.TryGetProperty("fileName", out var f) ? f.GetString() : null
            })
            .Where(h => h.Path.Length > 0)
            .Take(max)
            .ToList();
    }

    public async Task<IReadOnlyList<CommitInfo>> FindCommitsMentioningAsync(RepositorySelection selection, string issueKey, int max = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            return Array.Empty<CommitInfo>();

        var client = await ClientAsync(cancellationToken);
        var url = $"{_options.ServiceHost.TrimEnd('/')}/{Seg(selection.Organization)}/{Seg(selection.Project)}/_apis/git/repositories/{Seg(selection.Repository)}/commits" +
                  $"?searchCriteria.itemVersion.version={Seg(selection.Branch)}&searchCriteria.$top=500&api-version=7.1";
        using var resp = await client.GetAsync(url, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogInformation("Azure DevOps commit listing returned HTTP {Status} for {Repo}.", (int)resp.StatusCode, selection.Display);
            return Array.Empty<CommitInfo>();
        }

        var pattern = new Regex($@"(?<![A-Za-z0-9]){Regex.Escape(issueKey)}(?![0-9])", RegexOptions.IgnoreCase);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("value", out var value))
            return Array.Empty<CommitInfo>();

        return value.EnumerateArray()
            .Select(c => new CommitInfo
            {
                CommitId = c.GetProperty("commitId").GetString()!,
                Comment = c.TryGetProperty("comment", out var m) ? m.GetString() : null,
                Author = c.TryGetProperty("author", out var a) && a.TryGetProperty("name", out var n) ? n.GetString() : null,
                Date = c.TryGetProperty("author", out var a2) && a2.TryGetProperty("date", out var d) && d.TryGetDateTimeOffset(out var dt) ? dt : null,
                Url = c.TryGetProperty("remoteUrl", out var u) ? u.GetString() : null
            })
            .Where(c => c.Comment is not null && pattern.IsMatch(c.Comment))
            .Take(max)
            .ToList();
    }

    private async Task<HttpClient> ClientAsync(CancellationToken cancellationToken)
    {
        var client = _http.CreateClient("azure-devops");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(cancellationToken));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
