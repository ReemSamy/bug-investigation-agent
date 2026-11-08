using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Infrastructure.Forge;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BugInvestigationAgent.Api.Tests;

/// <summary>Mints Forge Invocation Tokens signed with a test RSA key (stands in for Atlassian's JWKS).</summary>
public sealed class FitFactory
{
    public const string AppId = "11111111-2222-3333-4444-555555555555";
    public const string Installation = "ari:cloud:ecosystem::installation/test-install";
    public const string CloudId = "cloud-test-1";

    public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = "test-kid" };

    public string Mint(string? principal, string installationId = Installation, string? cloudId = CloudId,
        string audience = "ari:cloud:ecosystem::app/" + AppId, string issuer = "forge/invocation-token",
        TimeSpan? lifetime = null, RsaSecurityKey? signingKey = null)
    {
        var now = DateTime.UtcNow;
        var payload = new Dictionary<string, object>
        {
            ["app"] = new Dictionary<string, object>
            {
                ["installationId"] = installationId,
                ["apiBaseUrl"] = $"https://api.atlassian.com/ex/jira/{cloudId}",
                ["id"] = "ari:cloud:ecosystem::app/" + AppId,
                ["environment"] = new Dictionary<string, object> { ["type"] = "DEVELOPMENT", ["id"] = "env-1" },
                ["module"] = new Dictionary<string, object> { ["type"] = "xen:macro", ["key"] = "bug-investigation-agent" }
            },
            ["context"] = cloudId is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { ["cloudId"] = cloudId, ["siteUrl"] = "https://example-site.atlassian.net" }
        };
        if (principal is not null)
            payload["principal"] = principal;

        var exp = now.Add(lifetime ?? TimeSpan.FromMinutes(1));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = exp.AddMinutes(-2),
            NotBefore = exp.AddMinutes(-2),
            Expires = exp,
            Claims = payload,
            SigningCredentials = new SigningCredentials(signingKey ?? Key, SecurityAlgorithms.RsaSha256)
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
/// Simulated Jira Cloud (api.atlassian.com/ex/jira/{cloudId}). Bearer tokens map to users; each user can read only
/// the issues granted to them — so tests prove that permission checks really use the caller's token.
/// </summary>
public sealed class FakeJira : HttpMessageHandler
{
    public ConcurrentDictionary<string, string> TokenToAccount { get; } = new();
    public ConcurrentDictionary<string, HashSet<string>> AccountIssues { get; } = new();
    public const string SystemToken = "system-token-xyz";
    public ConcurrentBag<string> Requests { get; } = new();
    public List<JsonObject> PostedComments { get; } = new();
    public List<string> PostedAttachments { get; } = new();

    public void GrantUser(string token, string accountId, params string[] issueKeys)
    {
        TokenToAccount[token] = accountId;
        AccountIssues[accountId] = new HashSet<string>(issueKeys, StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.Authorization?.Parameter ?? "";
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add($"{request.Method} {path} as {(token == SystemToken ? "system" : TokenToAccount.GetValueOrDefault(token, "?"))}");

        if (path.EndsWith("/rest/api/3/myself"))
            return TokenToAccount.TryGetValue(token, out var acc)
                ? Json(new { accountId = acc, displayName = "User " + acc })
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var issueMatch = System.Text.RegularExpressions.Regex.Match(path, @"/rest/api/3/issue/([^/]+)(/(comment|attachments))?$");
        if (issueMatch.Success)
        {
            var key = Uri.UnescapeDataString(issueMatch.Groups[1].Value);
            var id = key.StartsWith("100") ? key : "100" + key.Split('-').Last();
            var sub = issueMatch.Groups[3].Value;

            if (token == SystemToken)
            {
                if (sub == "comment" && request.Method == HttpMethod.Post)
                {
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                    lock (PostedComments) PostedComments.Add(body);
                    return Json(new { id = "c" + PostedComments.Count });
                }
                if (sub == "comment")
                {
                    JsonArray comments;
                    lock (PostedComments)
                        comments = new JsonArray(PostedComments.Select((c, i) => (JsonNode)new JsonObject
                        {
                            ["id"] = "c" + (i + 1),
                            ["properties"] = c["properties"]!.DeepClone()
                        }).ToArray());
                    return Json(new JsonObject { ["comments"] = comments });
                }
                if (sub == "attachments")
                {
                    lock (PostedAttachments) PostedAttachments.Add(key);
                    return Json(new object[] { new { id = "a1" } });
                }
                return Json(new { id, key = "ABC-1", fields = new { attachment = Array.Empty<object>() } });
            }

            if (!TokenToAccount.TryGetValue(token, out var account))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var allowed = AccountIssues.GetValueOrDefault(account) ?? new HashSet<string>();
            var issueKey = allowed.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase) || "100" + k.Split('-').Last() == key);
            if (issueKey is null)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return Json(new { id = "100" + issueKey.Split('-').Last(), key = issueKey, fields = new { summary = "Save fails on KMSQA", project = new { key = issueKey.Split('-')[0] } } });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FitFactory Fit { get; } = new();
    public FakeJira Jira { get; } = new();
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "bia-api-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataDir);
        builder.UseSetting("Hosting:DatabaseProvider", "Sqlite");
        builder.UseSetting("Agent:DataDirectory", DataDir);
        builder.UseSetting("Agent:EvidenceDirectory", Path.Combine(DataDir, "evidence"));
        builder.UseSetting("Forge:AppId", FitFactory.AppId);
        builder.UseSetting("Forge:PublicBaseUrl", "https://agent.example.test");
        builder.UseSetting("EnvironmentRegistry:Environments:KMSRC:ApplicationId", "KMS");
        builder.UseSetting("EnvironmentRegistry:Environments:KMSRC:BaseUrl", "https://kmsrc.example.test");
        builder.UseSetting("EnvironmentRegistry:Environments:KMSQA:ApplicationId", "KMS");
        builder.UseSetting("EnvironmentRegistry:Environments:KMSQA:BaseUrl", "https://kmsqa.example.test");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IForgeJwksProvider>();
            services.AddSingleton<IForgeJwksProvider>(new FixedJwks(Fit.Key));
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddHttpClient("jira-cloud").ConfigurePrimaryHttpMessageHandler(() => Jira);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDir, true); } catch { /* sqlite file may linger briefly */ }
    }
}
