using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Providers.Anthropic;
using BugInvestigationAgent.Providers.OpenAi;
using BugInvestigationAgent.Providers.Stubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.Providers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBugInvestigationProviders(this IServiceCollection services)
    {
        services.AddHttpClient();
        // Root-cause analysis on a large prompt can take minutes; the 100 s default would cut it off.
        services.AddHttpClient(OpenAiCompatibleRuntime.Id, c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient(AnthropicRuntime.Id, c => c.Timeout = TimeSpan.FromMinutes(5));

        services.AddSingleton<IAgentRuntime>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAiCompatibleRuntime.Id);
            return new OpenAiCompatibleRuntime(
                http,
                sp.GetRequiredService<ILlmSettingsSource>(),
                sp.GetRequiredService<ILogger<OpenAiCompatibleRuntime>>());
        });

        services.AddSingleton<IAgentRuntime>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(AnthropicRuntime.Id);
            return new AnthropicRuntime(
                http,
                sp.GetRequiredService<ILlmSettingsSource>(),
                sp.GetRequiredService<ILogger<AnthropicRuntime>>());
        });

        services.AddSingleton<IAgentRuntime, AzureOpenAiRuntime>();
        services.AddSingleton<IAgentRuntime, CursorRuntime>();
        services.AddSingleton<IAgentRuntime, NullAgentRuntime>();
        return services;
    }
}
