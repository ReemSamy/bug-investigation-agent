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

        services.AddSingleton<IAgentRuntime>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAiCompatibleRuntime.Id);
            return new OpenAiCompatibleRuntime(
                http,
                sp.GetRequiredService<IOptions<LlmProviderOptions>>(),
                sp.GetRequiredService<ILogger<OpenAiCompatibleRuntime>>());
        });

        services.AddSingleton<IAgentRuntime>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(AnthropicRuntime.Id);
            return new AnthropicRuntime(
                http,
                sp.GetRequiredService<IOptions<LlmProviderOptions>>(),
                sp.GetRequiredService<ILogger<AnthropicRuntime>>());
        });

        services.AddSingleton<IAgentRuntime, AzureOpenAiRuntime>();
        services.AddSingleton<IAgentRuntime, CursorRuntime>();
        services.AddSingleton<IAgentRuntime, NullAgentRuntime>();
        return services;
    }
}
