using BugInvestigationAgent.App.Components;
using BugInvestigationAgent.Core;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Infrastructure;
using BugInvestigationAgent.Providers;
using BugInvestigationAgent.Skills;

var builder = WebApplication.CreateBuilder(args);

// Machine-local overrides (environment URLs, login selectors). Git-ignored; never put credentials here.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<BugInvestigationAgent.Core.Environments.EnvironmentRegistryOptions>(
    builder.Configuration.GetSection(BugInvestigationAgent.Core.Environments.EnvironmentRegistryOptions.SectionName));
builder.Services.Configure<JiraOptions>(builder.Configuration.GetSection(JiraOptions.SectionName));
builder.Services.Configure<LlmProviderOptions>(builder.Configuration.GetSection(LlmProviderOptions.SectionName));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddBugInvestigationCore();
builder.Services.AddBugInvestigationProviders();
builder.Services.AddBugInvestigationSkills();
builder.Services.AddBugInvestigationInfrastructure(builder.Environment.ContentRootPath, builder.Configuration);
// Local app: the LLM provider, keys and models come from the Settings page (App_Data/local-settings.json).
builder.Services.AddSingleton<BugInvestigationAgent.Core.Abstractions.ILlmSettingsSource>(
    sp => sp.GetRequiredService<BugInvestigationAgent.Infrastructure.Settings.LocalSettingsService>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// Serve captured Playwright screenshots so the Evidence panel can show them inline.
var evidenceRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "App_Data", "evidence"));
Directory.CreateDirectory(evidenceRoot);
app.MapGet("/evidence/{sessionFolder}/{fileName}", (string sessionFolder, string fileName) =>
{
    if (string.IsNullOrWhiteSpace(sessionFolder) || string.IsNullOrWhiteSpace(fileName)
        || sessionFolder.Contains("..", StringComparison.Ordinal)
        || fileName.Contains("..", StringComparison.Ordinal)
        || sessionFolder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        return Results.BadRequest();

    var full = Path.GetFullPath(Path.Combine(evidenceRoot, sessionFolder, fileName));
    if (!full.StartsWith(evidenceRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        return Results.NotFound();

    var contentType = Path.GetExtension(full).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".md" => "text/markdown; charset=utf-8",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
    // Reports download with a readable name (e.g. VKMS-5102_report.md); images stay inline.
    var downloadName = fileName.StartsWith("report.", StringComparison.OrdinalIgnoreCase) ? BugInvestigationAgent.Skills.Evidence.ReportFiles.DownloadName(full) : null;
    return Results.File(full, contentType, downloadName);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
