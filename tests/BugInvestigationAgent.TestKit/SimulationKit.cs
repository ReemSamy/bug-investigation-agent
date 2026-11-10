extern alias api;
extern alias worker;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Infrastructure.Forge;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BugInvestigationAgent.TestKit;

public sealed class Fit
{
    public const string AppId = "11111111-2222-3333-4444-555555555555";
    public const string Installation = "ari:cloud:ecosystem::installation/it-install";
    public const string CloudId = "it-cloud";
    public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = "it-kid" };

    public string Mint(string? principal)
    {
        var now = DateTime.UtcNow;
        var payload = new Dictionary<string, object>
        {
            ["app"] = new Dictionary<string, object>
            {
                ["installationId"] = Installation,
                ["apiBaseUrl"] = $"https://api.atlassian.com/ex/jira/{CloudId}",
                ["environment"] = new Dictionary<string, object> { ["id"] = "it-env" }
            },
            ["context"] = new Dictionary<string, object> { ["cloudId"] = CloudId, ["siteUrl"] = "https://it-site.atlassian.net" }
        };
        if (principal is not null)
            payload["principal"] = principal;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "forge/invocation-token",
            Audience = "ari:cloud:ecosystem::app/" + AppId,
            IssuedAt = now.AddSeconds(-5),
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(1),
            Claims = payload,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)
        });
    }
}

public sealed class FixedJwks : IForgeJwksProvider
{
    private readonly SecurityKey _key;
    public FixedJwks(SecurityKey key) => _key = key;
    public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SecurityKey>>([_key]);
}

/// <summary>
/// SIMULATED Jira Cloud REST API (we are not authorized to call the real site). Enforces per-user issue
/// permissions by bearer token, serves issues/comments to user tokens, accepts report comments/attachments only
/// with the app system token.
/// </summary>
public sealed class SimulatedJira : HttpMessageHandler
{
    public const string SystemToken = "it-system-token";
    private readonly ConcurrentDictionary<string, string> _tokenToAccount = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _accountIssues = new();
    public ConcurrentQueue<string> Requests { get; } = new();
    public ConcurrentQueue<(string IssueId, JsonObject Body)> Comments { get; } = new();
    public ConcurrentQueue<(string IssueId, string FileName, int Bytes)> Attachments { get; } = new();

    /// <summary>Optional per-issue content (summary, description); otherwise a generic bug is returned.</summary>
    public ConcurrentDictionary<string, (string Summary, string Description)> Issues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Grant(string token, string account, params string[] issueKeys)
    {
        _tokenToAccount[token] = account;
        _accountIssues[account] = new HashSet<string>(issueKeys, StringComparer.OrdinalIgnoreCase);
    }

    private static string IdOf(string key) => "100" + key.Split('-')[1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.Authorization?.Parameter ?? "";
        var path = request.RequestUri!.AbsolutePath;
        var who = token == SystemToken ? "system" : _tokenToAccount.GetValueOrDefault(token, "anonymous");
        Requests.Enqueue($"{request.Method} {path} as {who}");

        if (path.EndsWith("/rest/api/3/myself"))
            return _tokenToAccount.TryGetValue(token, out var acc) ? Json(new { accountId = acc, displayName = acc }) : Status(HttpStatusCode.Unauthorized);

        var m = Regex.Match(path, @"/rest/api/3/issue/([^/]+)(/(comment|attachments))?$");
        if (!m.Success)
            return Status(HttpStatusCode.NotFound); // e.g. search: history search soft-fails

        var keyOrId = Uri.UnescapeDataString(m.Groups[1].Value);
        var sub = m.Groups[3].Value;

        if (token == SystemToken)
        {
            if (sub == "comment" && request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Comments.Enqueue((keyOrId, body));
                return Json(new { id = "c" + Comments.Count });
            }
            if (sub == "comment")
            {
                var list = new JsonArray(Comments.Where(c => c.IssueId == keyOrId).Select((c, i) => (JsonNode)new JsonObject
                {
                    ["id"] = "c" + (i + 1), ["properties"] = c.Body["properties"]!.DeepClone()
                }).ToArray());
                return Json(new JsonObject { ["comments"] = list });
            }
            if (sub == "attachments")
            {
                var raw = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                var fileName = Regex.Match(Encoding.Latin1.GetString(raw), @"filename=""?([^"";\r\n]+)").Groups[1].Value;
                Attachments.Enqueue((keyOrId, fileName, raw.Length));
                return Json(new object[] { new { id = "a" } });
            }
            return Json(new { id = keyOrId, fields = new { attachment = Array.Empty<object>() } });
        }

        if (!_tokenToAccount.TryGetValue(token, out var account))
            return Status(HttpStatusCode.Unauthorized);
        var key = _accountIssues.GetValueOrDefault(account)?.FirstOrDefault(k => k.Equals(keyOrId, StringComparison.OrdinalIgnoreCase) || IdOf(k) == keyOrId);
        if (key is null)
            return Status(HttpStatusCode.NotFound); // Jira hides issues the user cannot browse

        if (sub == "comment")
            return Json(new { comments = Array.Empty<object>() });
        var content = Issues.TryGetValue(key, out var c)
            ? c
            : ($"{key}: Saving an incident shows an error toast", "Steps to reproduce:\n1. Open the Incident page\n2. Observe the error toast");
        return Json(new
        {
            id = IdOf(key),
            key,
            fields = new
            {
                summary = content.Item1,
                description = content.Item2,
                project = new { key = key.Split('-')[0] },
                components = Array.Empty<object>(),
                attachment = Array.Empty<object>()
            }
        });
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{}") };
}

/// <summary>Settings shared by the real API host and the real worker host (same database, key ring, environments).</summary>
public sealed class SharedSetup
{
    /// <summary>PostgreSQL connection string; null = shared SQLite file under <see cref="EvidenceRoot"/>.</summary>
    public string? ConnectionString { get; init; }
    public required string EvidenceRoot { get; init; }
    public required TestPortal Portal { get; init; }
    public required Fit Fit { get; init; }
    public required SimulatedJira Jira { get; init; }
    public IDataProtectionProvider DataProtection { get; } = new EphemeralDataProtectionProvider();

    public void Apply(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var settings = new Dictionary<string, string>
        {
            ["Hosting:DatabaseProvider"] = ConnectionString is null ? "Sqlite" : "PostgreSQL",
            ["Hosting:DatabaseConnectionString"] = ConnectionString ?? "",
            ["Agent:DataDirectory"] = EvidenceRoot,
            ["Agent:EvidenceDirectory"] = EvidenceRoot,
            ["Agent:ActiveProviderId"] = "null",
            ["Agent:Browser:Headless"] = "true",
            ["Agent:Browser:NavigationTimeoutMs"] = "15000",
            ["Agent:Browser:ActionTimeoutMs"] = "5000",
            ["Agent:Browser:SettleTimeoutMs"] = "3000",
            ["Agent:Browser:PostActionDelayMs"] = "150",
            ["Agent:Browser:SpinnerTimeoutMs"] = "500",
            ["Agent:Browser:ProbeTimeoutMs"] = "400",
            ["Forge:AppId"] = Fit.AppId,
            ["Forge:EnvironmentId"] = "it-env",
            ["Forge:PublicBaseUrl"] = "https://agent.it.test",
            ["Worker:MaxConcurrentJobs"] = "1",
            ["Worker:PollIntervalMs"] = "100",
            ["Worker:LeaseSeconds"] = "30",
            ["Repositories:CheckoutRoot"] = Path.Combine(EvidenceRoot, "checkouts"),
            ["EnvironmentRegistry:DefaultLogin:UsernameSelectors:0"] = "#UserName",
            ["EnvironmentRegistry:DefaultLogin:PasswordSelectors:0"] = "#Password",
            ["EnvironmentRegistry:DefaultLogin:SuccessSelectors:0"] = "#user-menu",
            ["EnvironmentRegistry:DefaultLogin:OutcomeTimeoutMs"] = "8000",
            ["EnvironmentRegistry:Environments:KMSQA:BaseUrl"] = Portal.EnvUrl("qa"),
            ["EnvironmentRegistry:Environments:KMSRC:BaseUrl"] = Portal.EnvUrl("rc"),
            ["EnvironmentRegistry:Environments:KMSPROD:ApplicationId"] = "KMS",
            ["EnvironmentRegistry:Environments:KMSPROD:Type"] = "Production",
            ["EnvironmentRegistry:Environments:KMSPROD:BaseUrl"] = Portal.EnvUrl("prod")
        };
        foreach (var (k, v) in settings)
            builder.UseSetting(k, v);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton(DataProtection); // API and worker share one key ring (as with Blob + Key Vault in Azure)
            services.RemoveAll<IForgeJwksProvider>();
            services.AddSingleton<IForgeJwksProvider>(new FixedJwks(Fit.Key));
            services.AddHttpClient("jira-cloud").ConfigurePrimaryHttpMessageHandler(() => Jira);
            services.AddHttpClient("jira").ConfigurePrimaryHttpMessageHandler(() => Jira);
        });
    }
}

public sealed class ApiHost : WebApplicationFactory<api::Program>
{
    private readonly SharedSetup _setup;
    public ApiHost(SharedSetup setup) => _setup = setup;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(ProjectPaths.Find("BugInvestigationAgent.Api"));
        _setup.Apply(builder);
    }
}

public sealed class WorkerHost : WebApplicationFactory<worker::Program>
{
    private readonly SharedSetup _setup;
    public WorkerHost(SharedSetup setup) => _setup = setup;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(ProjectPaths.Find("BugInvestigationAgent.Worker"));
        _setup.Apply(builder);
    }
}

/// <summary>Locates src/{project} from the running binary, so hosts work from any test project.</summary>
public static class ProjectPaths
{
    public static string Find(string project)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", project);
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException($"Could not find src/{project} above {AppContext.BaseDirectory}.");
    }
}
