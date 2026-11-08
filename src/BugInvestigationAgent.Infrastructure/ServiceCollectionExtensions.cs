using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Jira;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Knowledge;
using BugInvestigationAgent.Core.Memory;
using BugInvestigationAgent.Core.Repositories;
using BugInvestigationAgent.Infrastructure.Credentials;
using BugInvestigationAgent.Infrastructure.Evidence;
using BugInvestigationAgent.Infrastructure.Forge;
using BugInvestigationAgent.Infrastructure.Hosting;
using BugInvestigationAgent.Infrastructure.Jobs;
using BugInvestigationAgent.Infrastructure.Knowledge;
using BugInvestigationAgent.Infrastructure.Memory;
using BugInvestigationAgent.Infrastructure.Persistence;
using BugInvestigationAgent.Infrastructure.Repositories;
using BugInvestigationAgent.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BugInvestigationAgent.Infrastructure;

/// <summary>Database / secret / storage settings for hosted deployments ("Hosting" section).</summary>
public sealed class HostingOptions
{
    public const string SectionName = "Hosting";
    /// <summary>"Sqlite" (default, local) or "PostgreSQL" (Azure Database for PostgreSQL).</summary>
    public string DatabaseProvider { get; set; } = "Sqlite";
    /// <summary>PostgreSQL connection string without password when <see cref="UseManagedIdentityForDatabase"/> is true.</summary>
    public string? DatabaseConnectionString { get; set; }
    public bool UseManagedIdentityForDatabase { get; set; }
    /// <summary>Client id of the user-assigned managed identity used for every Azure resource.</summary>
    public string? ManagedIdentityClientId { get; set; }
    /// <summary>Key Vault URI — secrets are loaded as configuration (e.g. secret "Forge--AppId").</summary>
    public string? KeyVaultUri { get; set; }
    /// <summary>Service Bus fully-qualified namespace (e.g. bia.servicebus.windows.net). Empty = database queue.</summary>
    public string? ServiceBusNamespace { get; set; }
    public string ServiceBusQueueName { get; set; } = "investigations";
    /// <summary>Blob service URI (https://acct.blob.core.windows.net). Empty = local evidence folder.</summary>
    public string? BlobServiceUri { get; set; }
    public string EvidenceContainer { get; set; } = "evidence";
    public string DataProtectionContainer { get; set; } = "dataprotection";
    /// <summary>Key Vault key id used to wrap the Data Protection key ring in hosted mode.</summary>
    public string? DataProtectionKeyId { get; set; }

    public bool IsPostgres => string.Equals(DatabaseProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);
}

public static class ServiceCollectionExtensions
{
    /// <summary>Loads Key Vault secrets into configuration when Hosting:KeyVaultUri is set (managed identity).</summary>
    public static IConfigurationBuilder AddHostedSecrets(this IConfigurationManager configuration)
    {
        var hosting = configuration.GetSection(HostingOptions.SectionName).Get<HostingOptions>() ?? new HostingOptions();
        if (!string.IsNullOrWhiteSpace(hosting.KeyVaultUri))
            configuration.AddAzureKeyVault(new Uri(hosting.KeyVaultUri), CreateCredential(hosting));
        return configuration;
    }

    public static TokenCredential CreateCredential(HostingOptions hosting) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = string.IsNullOrWhiteSpace(hosting.ManagedIdentityClientId) ? null : hosting.ManagedIdentityClientId,
            ExcludeInteractiveBrowserCredential = true
        });

    public static IServiceCollection AddBugInvestigationInfrastructure(
        this IServiceCollection services, string contentRoot, IConfiguration? configuration = null)
    {
        var hosting = configuration?.GetSection(HostingOptions.SectionName).Get<HostingOptions>() ?? new HostingOptions();
        services.AddSingleton(Options.Create(hosting));
        if (configuration is not null)
        {
            services.Configure<ForgeOptions>(configuration.GetSection(ForgeOptions.SectionName));
            services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));
            services.Configure<ReportPublishingOptions>(configuration.GetSection("ReportPublishing"));
            services.Configure<RepositoryMappingOptions>(configuration.GetSection(RepositoryMappingOptions.SectionName));
        }

        var credential = CreateCredential(hosting);
        services.TryAddSingleton<TokenCredential>(credential);

        services.AddSingleton<IHostEnvironmentAccessor>(new ContentRootAccessor(contentRoot));
        services.AddSingleton<LocalSettingsService>();
        services.AddSingleton<IJiraConnectionSettings>(sp => sp.GetRequiredService<LocalSettingsService>());
        services.AddSingleton<IWorkspaceDefaults>(sp => sp.GetRequiredService<LocalSettingsService>());
        services.AddHttpClient("jira-test");
        services.AddHttpClient("jira-cloud");
        services.AddHttpClient("forge-jwks");
        services.AddHttpClient("azure-devops");
        services.AddTransient<JiraConnectionTester>();

        // ── Data Protection (credential vault + job secrets) ──
        var dataProtection = services.AddDataProtection().SetApplicationName("BugInvestigationAgent");
        if (!string.IsNullOrWhiteSpace(hosting.BlobServiceUri) && !string.IsNullOrWhiteSpace(hosting.DataProtectionKeyId))
        {
            // Hosted: key ring in private Blob Storage, wrapped by a Key Vault key — shared by API and workers.
            var blob = new BlobServiceClient(new Uri(hosting.BlobServiceUri), credential)
                .GetBlobContainerClient(hosting.DataProtectionContainer).GetBlobClient("keys.xml");
            dataProtection.PersistKeysToAzureBlobStorage(blob).ProtectKeysWithAzureKeyVault(new Uri(hosting.DataProtectionKeyId), credential);
        }
        else
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(contentRoot, "App_Data", "keys")));
            // Encrypt the key ring at rest with the Windows user's DPAPI key.
            if (OperatingSystem.IsWindows())
                dataProtection.ProtectKeysWithDpapi();
        }

        // ── Database ──
        services.AddDbContextFactory<AgentDbContext>((sp, options) =>
        {
            if (hosting.IsPostgres)
            {
                options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>());
                return;
            }

            var agentOptions = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var dataDir = Path.IsPathRooted(agentOptions.DataDirectory)
                ? agentOptions.DataDirectory
                : Path.Combine(contentRoot, agentOptions.DataDirectory);
            Directory.CreateDirectory(dataDir);
            options.UseSqlite($"Data Source={Path.Combine(dataDir, "agent.db")};Default Timeout=30");
        });
        if (hosting.IsPostgres)
        {
            services.AddSingleton(_ =>
            {
                if (string.IsNullOrWhiteSpace(hosting.DatabaseConnectionString))
                    throw new InvalidOperationException("Hosting:DatabaseConnectionString is required for PostgreSQL.");
                var builder = new NpgsqlDataSourceBuilder(hosting.DatabaseConnectionString);
                if (hosting.UseManagedIdentityForDatabase)
                {
                    // Entra ID authentication: short-lived tokens instead of a stored database password.
                    builder.UsePeriodicPasswordProvider(async (_, ct) =>
                        (await credential.GetTokenAsync(new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), ct)).Token,
                        TimeSpan.FromMinutes(50), TimeSpan.FromSeconds(10));
                }
                return builder.Build();
            });
        }
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AgentDbContext>>().CreateDbContext());

        // ── Credentials ──
        services.AddSingleton<LocalCurrentUserAccessor>();
        services.AddSingleton<ICurrentUserAccessor>(sp => new HostedCurrentUserAccessor(sp.GetRequiredService<LocalCurrentUserAccessor>()));
        services.AddScoped<ICredentialVault, DataProtectionCredentialVault>();

        // ── Jobs, audit, evidence ──
        services.AddSingleton<IInvestigationJobStore, EfInvestigationJobStore>();
        services.AddSingleton<IJobSecretStore, DataProtectionJobSecretStore>();
        services.AddSingleton<IAuditLog, EfAuditLog>();
        services.AddSingleton<Hosting.JobInputService>();
        if (!string.IsNullOrWhiteSpace(hosting.ServiceBusNamespace))
        {
            services.AddSingleton(_ => new ServiceBusClient(hosting.ServiceBusNamespace, credential));
            services.AddSingleton<IInvestigationQueue>(sp =>
                new ServiceBusInvestigationQueue(sp.GetRequiredService<ServiceBusClient>(), hosting.ServiceBusQueueName));
        }
        else
        {
            services.AddSingleton<IInvestigationQueue, DatabaseInvestigationQueue>();
        }

        if (!string.IsNullOrWhiteSpace(hosting.BlobServiceUri))
        {
            services.AddSingleton<IEvidenceStore>(_ => new BlobEvidenceStore(
                new BlobServiceClient(new Uri(hosting.BlobServiceUri), credential).GetBlobContainerClient(hosting.EvidenceContainer)));
        }
        else
        {
            services.AddSingleton<IEvidenceStore>(sp =>
            {
                var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
                var root = Path.IsPathRooted(o.EvidenceDirectory) ? o.EvidenceDirectory : Path.Combine(Directory.GetCurrentDirectory(), o.EvidenceDirectory);
                return new LocalEvidenceStore(root);
            });
        }

        // ── Jira Cloud (Forge) ──
        services.AddSingleton<ConfiguredJiraAccessProvider>();
        services.Replace(ServiceDescriptor.Singleton<IJiraAccessProvider, HostedJiraAccessProvider>());
        services.AddSingleton<JiraCloudClient>();
        services.AddSingleton<JiraReportPublisher>();
        services.AddSingleton<IForgeJwksProvider, HttpForgeJwksProvider>();
        services.AddSingleton<ForgeInvocationTokenValidator>();

        // ── Azure DevOps (read-only) ──
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ISourceRepositoryProvider, AzureDevOpsRepositoryProvider>();

        services.AddScoped<IEngineeringMemoryStore, EfEngineeringMemoryStore>();
        services.AddSingleton<MarkdownKnowledgeStore>();
        services.AddSingleton<IKnowledgeIngestionService>(sp => sp.GetRequiredService<MarkdownKnowledgeStore>());
        services.AddSingleton<IKnowledgeRetriever>(sp => sp.GetRequiredService<MarkdownKnowledgeStore>());
        services.AddSingleton<IEmbeddingProvider, NullEmbeddingProvider>();
        services.AddHostedService<DatabaseInitializer>();
        services.AddReadinessChecks(hosting);
        return services;
    }

    /// <summary>
    /// Hosted (Jira-invoked, multi-user) mode: Jira is accessed per job with the invoking user's Forge token, so the
    /// local "Sign in / Account" API token is not used.
    /// </summary>
    public static IServiceCollection AddBugInvestigationHostedMode(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IJiraConnectionSettings, HostedJiraConnectionSettings>());
        // Identity = verified Atlassian user of the current job only; never the OS account.
        services.Replace(ServiceDescriptor.Singleton<ICurrentUserAccessor, Hosting.HostedOnlyCurrentUserAccessor>());
        // Read-only rollout enforced in code, independent of configuration.
        services.Replace(ServiceDescriptor.Singleton<Core.Permissions.IPermissionGate>(sp =>
        {
            var configured = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Permissions.ToFlags();
            return Hosting.HostedPermissions.Mask(new Core.Permissions.PermissionGate(configured));
        }));
        return services;
    }
}

/// <summary>In hosted mode Jira is "connected" through the per-job Forge user token.</summary>
public sealed class HostedJiraConnectionSettings : IJiraConnectionSettings
{
    public bool IsConfigured => true;
    public bool IsSignedIn => true;
    public string? SignedInDisplayName => HostedJobScope.Current?.AccountId;
    public string? SignedInEmail => null;
    public JiraOptions GetEffective() => new() { BaseUrl = HostedJobScope.Current?.SiteUrl ?? "" };
}

internal sealed class DatabaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DatabaseInitializer(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        // Versioned migrations (safe when several replicas start at once).
        await DatabaseMigrator.MigrateAsync(db, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
