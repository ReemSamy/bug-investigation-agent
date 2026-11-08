using Azure.Monitor.OpenTelemetry.AspNetCore;
using BugInvestigationAgent.Core;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Infrastructure;
using BugInvestigationAgent.Infrastructure.Hosting;
using BugInvestigationAgent.Infrastructure.Persistence;
using BugInvestigationAgent.Providers;
using BugInvestigationAgent.Skills;
using Microsoft.EntityFrameworkCore;

// Background investigation worker: runs queued Jira investigations (Playwright + LLM + Azure DevOps), read-only.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddHostedSecrets();
BugInvestigationAgent.Infrastructure.Hosting.HostedConfigurationValidator.ThrowIfInvalid(
    builder.Configuration, builder.Environment.IsDevelopment(), isApi: false);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<JiraOptions>(builder.Configuration.GetSection(JiraOptions.SectionName));
builder.Services.Configure<LlmProviderOptions>(builder.Configuration.GetSection(LlmProviderOptions.SectionName));
builder.Services.Configure<EnvironmentRegistryOptions>(builder.Configuration.GetSection(EnvironmentRegistryOptions.SectionName));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));

builder.Services.AddBugInvestigationCore();
builder.Services.AddBugInvestigationProviders();
builder.Services.AddBugInvestigationSkills();
builder.Services.AddBugInvestigationInfrastructure(builder.Environment.ContentRootPath, builder.Configuration);
builder.Services.AddBugInvestigationHostedMode();
builder.Services.AddScoped<BugInvestigationAgent.Core.Skills.ISkill, BugInvestigationAgent.Skills.Code.SourceRepositorySkill>();

builder.Services.AddSingleton<InvestigationJobRunner>();
builder.Services.AddHealthChecks().AddCheck<BugInvestigationAgent.Worker.BrowserReadinessCheck>("browser", tags: [ReadinessExtensions.ReadyTag]);
builder.Services.AddHostedService<InvestigationWorkerService>();
builder.Services.AddHostedService<JobRecoveryService>();

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

var app = builder.Build();

// Container Apps probes only (no business endpoints on the worker; ingress disabled in IaC).
// Liveness: process up. Readiness: real dependencies (database + schema, key ring, storage, queue[, browser]).
app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/readyz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains(BugInvestigationAgent.Infrastructure.Hosting.ReadinessExtensions.ReadyTag),
    ResponseWriter = BugInvestigationAgent.Infrastructure.Hosting.ReadinessExtensions.WriteResponse
});

app.Run();

public partial class Program;
