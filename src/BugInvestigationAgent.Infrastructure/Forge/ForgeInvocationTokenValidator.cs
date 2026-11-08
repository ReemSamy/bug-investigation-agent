using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BugInvestigationAgent.Infrastructure.Forge;

public sealed class ForgeOptions
{
    public const string SectionName = "Forge";

    /// <summary>Forge app id (UUID part of ari:cloud:ecosystem::app/{AppId}). Required.</summary>
    public string AppId { get; set; } = "";
    /// <summary>Forge environment id, used to build settings-page links ({siteUrl}/jira/settings/personal/apps/{appId}/{envId}).</summary>
    public string? EnvironmentId { get; set; }
    public string JwksUrl { get; set; } = "https://forge.cdn.prod.atlassian-dev.net/.well-known/jwks.json";
    public string Issuer { get; set; } = "forge/invocation-token";
    /// <summary>Optional allow-list of Jira Cloud ids (only your site). Empty = any site where the app is installed.</summary>
    public List<string> AllowedCloudIds { get; set; } = new();
    public int ClockSkewSeconds { get; set; } = 30;
    /// <summary>Origins allowed to call the direct credential endpoints (Forge Custom UI is served from Atlassian's CDN).</summary>
    public List<string> CustomUiOrigins { get; set; } = new() { "https://*.cdn.prod.atlassian-dev.net" };

    public string Audience => $"ari:cloud:ecosystem::app/{AppId}";
}

/// <summary>Validated claims of a Forge Invocation Token. Built only after signature, issuer, audience and lifetime checks.</summary>
public sealed class ForgeInvocation
{
    public required string InstallationId { get; init; }
    public string? AppId { get; init; }
    public string? EnvironmentId { get; init; }
    public string? ApiBaseUrl { get; init; }
    public string? CloudId { get; init; }
    public string? SiteUrl { get; init; }
    public string? ModuleKey { get; init; }
    /// <summary>Atlassian accountId of the invoking user (absent for system invocations, e.g. scheduled triggers).</summary>
    public string? PrincipalAccountId { get; init; }
    public string? TokenId { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public interface IForgeJwksProvider
{
    Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken = default);
}

/// <summary>Fetches and caches Atlassian's Forge JWKS; refreshes on unknown key ids (rate limited).</summary>
public sealed class HttpForgeJwksProvider : IForgeJwksProvider
{
    private readonly IHttpClientFactory _http;
    private readonly ForgeOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SecurityKey> _keys = Array.Empty<SecurityKey>();
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public HttpForgeJwksProvider(IHttpClientFactory http, IOptions<ForgeOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var age = DateTimeOffset.UtcNow - _fetchedAt;
        if (_keys.Count > 0 && age < TimeSpan.FromHours(1) && !(forceRefresh && age > TimeSpan.FromMinutes(1)))
            return _keys;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            age = DateTimeOffset.UtcNow - _fetchedAt;
            if (_keys.Count > 0 && age < TimeSpan.FromHours(1) && !(forceRefresh && age > TimeSpan.FromMinutes(1)))
                return _keys;
            var json = await _http.CreateClient("forge-jwks").GetStringAsync(_options.JwksUrl, cancellationToken);
            _keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
            _fetchedAt = DateTimeOffset.UtcNow;
            return _keys;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class ForgeTokenValidationResult
{
    public ForgeInvocation? Invocation { get; init; }
    public string? Error { get; init; }
    public bool IsValid => Invocation is not null;
}

/// <summary>
/// Validates Forge Invocation Tokens (FIT): RS256 signature against Atlassian's JWKS, issuer "forge/invocation-token",
/// audience = this app's ARI, expiry/not-before with small skew, and (optionally) an allow-listed cloud id.
/// Nothing from the request body is trusted for tenant or user identity.
/// </summary>
public sealed class ForgeInvocationTokenValidator
{
    private readonly IForgeJwksProvider _jwks;
    private readonly ForgeOptions _options;
    private readonly ILogger<ForgeInvocationTokenValidator> _logger;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public ForgeInvocationTokenValidator(IForgeJwksProvider jwks, IOptions<ForgeOptions> options, ILogger<ForgeInvocationTokenValidator> logger)
    {
        _jwks = jwks;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ForgeTokenValidationResult> ValidateAsync(string? authorizationHeader, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AppId))
            return Fail("Forge AppId is not configured on the server.");
        if (string.IsNullOrWhiteSpace(authorizationHeader) || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Fail("Missing Forge Invocation Token.");

        var token = authorizationHeader["Bearer ".Length..].Trim();
        var result = await ValidateWithKeysAsync(token, forceRefresh: false, cancellationToken);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            result = await ValidateWithKeysAsync(token, forceRefresh: true, cancellationToken); // key rotation

        if (!result.IsValid)
        {
            // Log the failure type only — never the token.
            _logger.LogWarning("Rejected Forge invocation: {Reason}", result.Exception?.GetType().Name ?? "invalid");
            return Fail("Invalid Forge Invocation Token.");
        }

        var jwt = (JsonWebToken)result.SecurityToken;
        var invocation = Parse(jwt);
        if (invocation is null)
            return Fail("Forge Invocation Token is missing the installation context.");

        if (_options.AllowedCloudIds.Count > 0 &&
            (invocation.CloudId is null || !_options.AllowedCloudIds.Contains(invocation.CloudId, StringComparer.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Rejected Forge invocation from a Jira site that is not allow-listed.");
            return Fail("This Jira site is not allowed to use the agent.");
        }

        return new ForgeTokenValidationResult { Invocation = invocation };
    }

    private async Task<TokenValidationResult> ValidateWithKeysAsync(string token, bool forceRefresh, CancellationToken cancellationToken)
    {
        var keys = await _jwks.GetSigningKeysAsync(forceRefresh, cancellationToken);
        return await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds)
        });
    }

    internal static ForgeInvocation? Parse(JsonWebToken jwt)
    {
        using var doc = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
        var root = doc.RootElement;
        if (!root.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.Object)
            return null;
        var installationId = Str(app, "installationId");
        if (string.IsNullOrWhiteSpace(installationId))
            return null;

        root.TryGetProperty("context", out var context);
        var cloudId = context.ValueKind == JsonValueKind.Object ? Str(context, "cloudId") : null;
        string? envId = app.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object ? Str(env, "id") : null;
        string? moduleKey = app.TryGetProperty("module", out var module) && module.ValueKind == JsonValueKind.Object ? Str(module, "key") : null;

        // principal may be a plain accountId string or an object depending on the invocation surface.
        string? principal = null;
        if (root.TryGetProperty("principal", out var p))
            principal = p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind == JsonValueKind.Object ? Str(p, "accountId") : null;

        return new ForgeInvocation
        {
            InstallationId = installationId!,
            AppId = Str(app, "id"),
            EnvironmentId = envId,
            ApiBaseUrl = Str(app, "apiBaseUrl"),
            CloudId = cloudId,
            SiteUrl = context.ValueKind == JsonValueKind.Object ? Str(context, "siteUrl") : null,
            ModuleKey = moduleKey,
            PrincipalAccountId = string.IsNullOrWhiteSpace(principal) ? null : principal,
            TokenId = Str(root, "jti"),
            ExpiresAt = jwt.ValidTo == DateTime.MinValue ? DateTimeOffset.UtcNow : new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero)
        };
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ForgeTokenValidationResult Fail(string error) => new() { Error = error };
}
