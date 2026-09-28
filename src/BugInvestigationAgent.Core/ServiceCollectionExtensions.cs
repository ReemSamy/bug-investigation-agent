using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Rules;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace BugInvestigationAgent.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBugInvestigationCore(this IServiceCollection services)
    {
        services.AddSingleton<IAgentSessionStore, InMemoryAgentSessionStore>();
        services.AddSingleton<ISecretRedactor, SecretRedactor>();
        services.AddSingleton<IMissingInfoResolver, MissingInfoResolver>();
        services.AddSingleton<IRulesProvider, DefaultRulesProvider>();
        services.AddSingleton<IAgentRuntimeResolver, AgentRuntimeResolver>();
        services.AddSingleton<IPermissionGate>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            return new PermissionGate(options.Permissions.ToFlags());
        });
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        return services;
    }
}
