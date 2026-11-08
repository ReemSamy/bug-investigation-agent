using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Infrastructure.Hosting;

/// <summary>Database reachable AND schema at the latest migration version.</summary>
public sealed class DatabaseReadinessCheck : IHealthCheck
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    public DatabaseReadinessCheck(IDbContextFactory<AgentDbContext> factory) => _factory = factory;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("database unreachable");
            var applied = await DatabaseMigrator.AppliedVersionsAsync(db, cancellationToken);
            var latest = DatabaseMigrator.Migrations.Max(m => m.Version);
            return applied.Contains(latest)
                ? HealthCheckResult.Healthy($"schema v{latest}")
                : HealthCheckResult.Unhealthy($"schema not migrated (latest v{latest})");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("database error: " + ex.GetType().Name);
        }
    }
}

/// <summary>Key ring loads and can protect/unprotect (in Azure: Blob key ring + Key Vault key unwrap work).</summary>
public sealed class DataProtectionReadinessCheck : IHealthCheck
{
    private readonly IDataProtectionProvider _provider;
    public DataProtectionReadinessCheck(IDataProtectionProvider provider) => _provider = provider;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var p = _provider.CreateProtector("BugInvestigationAgent.Readiness");
            return Task.FromResult(p.Unprotect(p.Protect("ok")) == "ok"
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("key ring round-trip mismatch"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("key ring unavailable: " + ex.GetType().Name));
        }
    }
}

public sealed class BlobReadinessCheck : IHealthCheck
{
    private readonly BlobContainerClient _container;
    public BlobReadinessCheck(BlobContainerClient container) => _container = container;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _container.GetPropertiesAsync(cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("evidence storage unavailable: " + ex.GetType().Name);
        }
    }
}

public sealed class ServiceBusReadinessCheck : IHealthCheck
{
    private readonly ServiceBusClient _client;
    private readonly string _queue;
    public ServiceBusReadinessCheck(ServiceBusClient client, string queue) { _client = client; _queue = queue; }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var receiver = _client.CreateReceiver(_queue);
            await receiver.PeekMessageAsync(cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("queue unavailable: " + ex.GetType().Name);
        }
    }
}

public static class ReadinessExtensions
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddReadinessChecks(this IServiceCollection services, HostingOptions hosting)
    {
        var checks = services.AddHealthChecks()
            .AddCheck<DatabaseReadinessCheck>("database", tags: [ReadyTag])
            .AddCheck<DataProtectionReadinessCheck>("key-ring", tags: [ReadyTag]);
        if (!string.IsNullOrWhiteSpace(hosting.BlobServiceUri))
            checks.Add(new HealthCheckRegistration("evidence-storage",
                sp => new BlobReadinessCheck(new BlobServiceClient(new Uri(hosting.BlobServiceUri), sp.GetRequiredService<Azure.Core.TokenCredential>())
                    .GetBlobContainerClient(hosting.EvidenceContainer)), null, [ReadyTag]));
        if (!string.IsNullOrWhiteSpace(hosting.ServiceBusNamespace))
            checks.Add(new HealthCheckRegistration("queue",
                sp => new ServiceBusReadinessCheck(sp.GetRequiredService<ServiceBusClient>(), hosting.ServiceBusQueueName), null, [ReadyTag]));
        return services;
    }

    /// <summary>Compact JSON: overall status and per-dependency status (no exception details or secrets).</summary>
    public static Task WriteResponse(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), detail = e.Value.Description })
        }));
    }
}
