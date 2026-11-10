using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Rules;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BugInvestigationAgent.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBugInvestigationCore(this IServiceCollection services)
    {
        services.AddSingleton<IAgentSessionStore, InMemoryAgentSessionStore>();
        services.AddSingleton<ISecretRedactor, SecretRedactor>();
        services.AddOptions<Environments.EnvironmentRegistryOptions>();
        services.AddSingleton<Environments.IEnvironmentRegistry, Environments.ConfigurationEnvironmentRegistry>();
        services.AddSingleton<Environments.IEnvironmentSelectionService, Environments.EnvironmentSelectionService>();
        services.AddSingleton<Credentials.ISessionCredentialStore, Credentials.InMemorySessionCredentialStore>();
        services.AddSingleton<IMissingInfoResolver, MissingInfoResolver>();
        services.AddSingleton<IRulesProvider, DefaultRulesProvider>();
        services.TryAddSingleton<Abstractions.ILlmSettingsSource, Abstractions.OptionsLlmSettingsSource>();
        services.AddSingleton<IAgentRuntimeResolver, AgentRuntimeResolver>();
        services.AddSingleton<IPermissionGate>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            return new PermissionGate(options.Permissions.ToFlags());
        });
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<Jira.IJiraAccessProvider, Jira.ConfiguredJiraAccessProvider>();
        services.AddOptions<Repositories.RepositoryMappingOptions>();
        services.AddSingleton<Repositories.IRepositoryResolver, Repositories.ConfiguredRepositoryResolver>();
        return services;
    }
}
