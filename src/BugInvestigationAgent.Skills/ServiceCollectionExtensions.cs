using BugInvestigationAgent.Core.Browser;
using BugInvestigationAgent.Core.Knowledge;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Skills.Browser;
using BugInvestigationAgent.Skills.Code;
using BugInvestigationAgent.Skills.Credentials;
using BugInvestigationAgent.Skills.Environment;
using BugInvestigationAgent.Skills.Evidence;
using BugInvestigationAgent.Skills.Future;
using BugInvestigationAgent.Skills.Jira;
using BugInvestigationAgent.Skills.Knowledge;
using BugInvestigationAgent.Skills.Reproduction;
using BugInvestigationAgent.Skills.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace BugInvestigationAgent.Skills;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBugInvestigationSkills(this IServiceCollection services)
    {
        services.AddHttpClient("jira");
        services.AddSingleton<PlaywrightSessionHub>();
        services.AddSingleton<IPlaywrightSessionHub>(sp => sp.GetRequiredService<PlaywrightSessionHub>());
        services.AddSingleton<BugInvestigationAgent.Core.Browser.IBrowserSessionCloser>(sp => sp.GetRequiredService<PlaywrightSessionHub>());

        services.AddSingleton<BugInvestigationAgent.Skills.Browser.Authentication.LoginFlow>();
        services.AddSingleton<IJiraIssueSearchClient, JiraIssueSearchClient>();
        services.AddSingleton<DeterministicPlaywrightBrowserSurface>();
        services.AddSingleton<PlaywrightMcpBrowserSurface>();
        services.AddSingleton<IBrowserToolSurfaceResolver, BrowserToolSurfaceResolver>();
        services.AddSingleton<IBrowserToolSurface>(sp => sp.GetRequiredService<IBrowserToolSurfaceResolver>().Resolve());

        services.AddScoped<ISkill, JiraSkill>();
        services.AddScoped<ISkill, TicketUnderstandingSkill>();
        services.AddScoped<ISkill, HistoricalJiraSearchSkill>();
        services.AddScoped<ISkill, HistoricalReproductionReuseSkill>();
        services.AddScoped<ISkill, KmsDocumentationSkill>();
        services.AddScoped<ISkill, EnvironmentDiscoverySkill>();
        services.AddScoped<ISkill, CredentialSkill>();
        services.AddScoped<ISkill, BrowserAutomationSkill>();
        services.AddScoped<ISkill, ReproductionSkill>();
        services.AddScoped<ISkill, CodeInvestigationSkill>();
        services.AddScoped<ISkill, CodeModificationSkill>();
        services.AddScoped<ISkill, ValidationSkill>();
        services.AddScoped<ISkill, EvidenceReportingSkill>();
        services.AddScoped<ISkill, GitSkill>();
        services.AddScoped<ISkill, GitHubSkill>();
        services.AddScoped<ISkill, PullRequestSkill>();
        services.AddScoped<ISkill, CiCdSkill>();
        services.AddScoped<ISkill, DeploymentSkill>();
        return services;
    }
}
