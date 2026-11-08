using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using BugInvestigationAgent.Api.A2A;
using BugInvestigationAgent.Api.Credentials;
using BugInvestigationAgent.Core;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Infrastructure;
using BugInvestigationAgent.Infrastructure.Forge;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddHostedSecrets();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<EnvironmentRegistryOptions>(builder.Configuration.GetSection(EnvironmentRegistryOptions.SectionName));
builder.Services.AddBugInvestigationCore();
builder.Services.AddBugInvestigationInfrastructure(builder.Environment.ContentRootPath, builder.Configuration);
builder.Services.AddBugInvestigationHostedMode();
// The API never runs browsers (workers do); the orchestrator registered by Core only needs a closer.
builder.Services.AddSingleton<BugInvestigationAgent.Core.Browser.IBrowserSessionCloser, NoBrowserSessions>();
builder.Services.AddSingleton<A2AAgentService>();
builder.Services.AddSingleton<CredentialChannelService>();
builder.Services.AddHealthChecks();

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

// Direct credential submission comes from the Forge Custom UI iframe (Atlassian CDN origin) — nothing else.
builder.Services.AddCors(o => o.AddPolicy("forge-custom-ui", policy =>
{
    var patterns = (builder.Configuration.GetSection("Forge:CustomUiOrigins").Get<string[]>() ?? ["https://*.cdn.prod.atlassian-dev.net"])
        .Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", "[a-z0-9-]+") + "$", RegexOptions.IgnoreCase))
        .ToList();
    policy.SetIsOriginAllowed(origin => patterns.Any(r => r.IsMatch(origin)))
        .WithMethods("POST")
        .WithHeaders("Content-Type", "X-Submission-Ticket");
}));

var app = builder.Build();
app.UseCors();

// ── Health (unauthenticated; no data) ──
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", async (IDbContextFactory<AgentDbContext> db, CancellationToken ct) =>
{
    await using var ctx = await db.CreateDbContextAsync(ct);
    return await ctx.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
});

// ── Forge-authenticated endpoints (Forge Invocation Token required) ──
var forge = app.MapGroup("").AddEndpointFilter<ForgeInvocationFilter>();

forge.MapPost("/a2a/json-rpc", async (HttpContext http, A2AAgentService agent, CancellationToken ct) =>
{
    JsonDocument doc;
    try { doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct); }
    catch (JsonException) { return Results.Json(A2AJson.Error(null, A2AErrors.ParseError, "Invalid JSON.")); }
    using (doc)
    {
        var tokens = new ForgeTokens(
            http.Request.Headers["x-forge-oauth-user"].FirstOrDefault(),
            http.Request.Headers["x-forge-oauth-system"].FirstOrDefault());
        return Results.Json(await agent.HandleAsync(doc, ForgeInvocationFilter.Get(http), tokens, ct));
    }
});

forge.MapPost("/atlassian/installed", async (HttpContext http, IInvestigationJobStore jobs, IAuditLog audit, CancellationToken ct) =>
{
    var fit = ForgeInvocationFilter.Get(http);
    string? cloudId = fit.CloudId, installer = null;
    try
    {
        using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
        if (cloudId is null && body.RootElement.TryGetProperty("context", out var c) && c.GetString() is { } ari)
            cloudId = ari.Split('/').LastOrDefault(); // ari:cloud:jira::site/{cloudId}
        if (body.RootElement.TryGetProperty("installerAccountId", out var i))
            installer = i.GetString();
    }
    catch (JsonException) { }
    if (string.IsNullOrWhiteSpace(cloudId))
        return Results.BadRequest();

    await jobs.UpsertInstallationAsync(new JiraInstallation
    {
        InstallationId = fit.InstallationId, CloudId = cloudId!, SiteUrl = fit.SiteUrl, ApiBaseUrl = fit.ApiBaseUrl, InstallerAccountId = installer
    }, ct);
    await audit.WriteAsync(new AuditEntry { TenantId = fit.InstallationId, AccountId = installer, Action = AuditActions.AppInstalled, Detail = $"Installed on site {cloudId}." }, ct);
    return Results.Ok();
});

forge.MapPost("/forge/credentials/pending", async (HttpContext http, CredentialChannelService channel, IOptions<HostingOptions> hosting, IConfiguration config, CancellationToken ct) =>
{
    var fit = ForgeInvocationFilter.Get(http);
    if (fit.PrincipalAccountId is null)
        return Results.Unauthorized();
    var items = await channel.ListPendingAsync(fit, ct);
    return Results.Ok(new { submitBaseUrl = config["Forge:PublicBaseUrl"], items });
});

// ── Direct submissions from the Custom UI (one-time ticket required) ──
app.MapPost("/api/jobs/{jobId:guid}/credentials", async (Guid jobId, HttpContext http, CredentialSubmission body, CredentialChannelService channel, CancellationToken ct) =>
    ToResult(await channel.SubmitCredentialsAsync(jobId, http.Request.Headers["X-Submission-Ticket"].FirstOrDefault(), body, ct)))
    .RequireCors("forge-custom-ui");

app.MapPost("/api/jobs/{jobId:guid}/mfa-code", async (Guid jobId, HttpContext http, MfaSubmission body, CredentialChannelService channel, CancellationToken ct) =>
    ToResult(await channel.SubmitMfaCodeAsync(jobId, http.Request.Headers["X-Submission-Ticket"].FirstOrDefault(), body, ct)))
    .RequireCors("forge-custom-ui");

app.Run();

static IResult ToResult(SubmissionResult r) => r switch
{
    SubmissionResult.Accepted => Results.Ok(new { accepted = true }),
    SubmissionResult.Unauthorized => Results.Unauthorized(),
    SubmissionResult.NotAwaiting => Results.Conflict(new { error = "This investigation is not waiting for that input." }),
    _ => Results.BadRequest(new { error = "Invalid submission." })
};

/// <summary>Validates the Forge Invocation Token on every request; rejects with 401 before any handler runs.</summary>
public sealed class ForgeInvocationFilter : IEndpointFilter
{
    private const string ItemKey = "forge-invocation";
    private readonly ForgeInvocationTokenValidator _validator;

    public ForgeInvocationFilter(ForgeInvocationTokenValidator validator) => _validator = validator;

    public static ForgeInvocation Get(HttpContext http) => (ForgeInvocation)http.Items[ItemKey]!;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var result = await _validator.ValidateAsync(http.Request.Headers.Authorization.FirstOrDefault(), http.RequestAborted);
        if (!result.IsValid)
            return Results.Unauthorized();
        http.Items[ItemKey] = result.Invocation;
        return await next(context);
    }
}

public partial class Program;

internal sealed class NoBrowserSessions : BugInvestigationAgent.Core.Browser.IBrowserSessionCloser
{
    public Task CloseAsync(Guid sessionId) => Task.CompletedTask;
}
