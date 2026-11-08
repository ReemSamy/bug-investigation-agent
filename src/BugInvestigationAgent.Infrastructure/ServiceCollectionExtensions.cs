using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Knowledge;
using BugInvestigationAgent.Core.Memory;
using BugInvestigationAgent.Infrastructure.Credentials;
using BugInvestigationAgent.Infrastructure.Knowledge;
using BugInvestigationAgent.Infrastructure.Memory;
using BugInvestigationAgent.Infrastructure.Persistence;
using BugInvestigationAgent.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBugInvestigationInfrastructure(this IServiceCollection services, string contentRoot)
    {
        services.AddSingleton<IHostEnvironmentAccessor>(new ContentRootAccessor(contentRoot));
        services.AddSingleton<LocalSettingsService>();
        services.AddSingleton<IJiraConnectionSettings>(sp => sp.GetRequiredService<LocalSettingsService>());
        services.AddSingleton<IWorkspaceDefaults>(sp => sp.GetRequiredService<LocalSettingsService>());
        services.AddHttpClient("jira-test");
        services.AddTransient<JiraConnectionTester>();

        var dataProtection = services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(contentRoot, "App_Data", "keys")))
            .SetApplicationName("BugInvestigationAgent");
        // Encrypt the key ring at rest with the Windows user's DPAPI key, so copying App_Data alone
        // (or another Windows account) cannot decrypt saved credentials.
        if (OperatingSystem.IsWindows())
            dataProtection.ProtectKeysWithDpapi();

        services.AddDbContext<AgentDbContext>((sp, options) =>
        {
            var agentOptions = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var dataDir = Path.IsPathRooted(agentOptions.DataDirectory)
                ? agentOptions.DataDirectory
                : Path.Combine(contentRoot, agentOptions.DataDirectory);
            Directory.CreateDirectory(dataDir);
            var dbPath = Path.Combine(dataDir, "agent.db");
            options.UseSqlite($"Data Source={dbPath}");
        });

        services.AddSingleton<ICurrentUserAccessor, LocalCurrentUserAccessor>();
        services.AddScoped<ICredentialVault, DataProtectionCredentialVault>();
        services.AddScoped<IEngineeringMemoryStore, EfEngineeringMemoryStore>();
        services.AddSingleton<MarkdownKnowledgeStore>();
        services.AddSingleton<IKnowledgeIngestionService>(sp => sp.GetRequiredService<MarkdownKnowledgeStore>());
        services.AddSingleton<IKnowledgeRetriever>(sp => sp.GetRequiredService<MarkdownKnowledgeStore>());
        services.AddSingleton<IEmbeddingProvider, NullEmbeddingProvider>();
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }
}

internal sealed class DatabaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DatabaseInitializer(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await SchemaUpgrader.UpgradeAsync(db, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
