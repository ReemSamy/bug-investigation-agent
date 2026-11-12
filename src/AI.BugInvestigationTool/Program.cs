using AI.BugInvestigationTool;
using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Services.Browser;
using AI.BugInvestigationTool.Services.Workflow;
using AI.BugInvestigationTool.Storage;

var isCli = args.Length > 0 && Cli.Commands.Contains(args[0].ToLowerInvariant());

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = isCli ? [] : args,
    ContentRootPath = AppContext.BaseDirectory,
});
// Secrets live outside source control: user-secrets (any environment) and an optional git-ignored local file.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);
builder.Configuration.AddEnvironmentVariables();

if (isCli)
{
    // Keep stdout clean for the report; diagnostics go to stderr only at warning level.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

builder.Services.AddOptions<ToolOptions>().Bind(builder.Configuration.GetSection(ToolOptions.Section));
builder.Services.AddHttpClient("jira", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient("ado", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient("claude", c => c.Timeout = Timeout.InfiniteTimeSpan); // per-request timeout in ClaudeClient
builder.Services.AddSingleton<InvestigationStore>();
builder.Services.AddSingleton<IBrowserLauncher, PlaywrightBrowserLauncher>();
builder.Services.AddScoped<InvestigationEngine>();
builder.Services.AddSingleton<InvestigationRunner>();
builder.Services.AddRazorPages();

// Never log outgoing request URLs/headers from HttpClient at Information level.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var app = builder.Build();

if (isCli)
{
    using var scope = app.Services.CreateScope();
    return await Cli.RunAsync(args, scope.ServiceProvider, app.Configuration);
}

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

// Evidence files (screenshots) for the report page. Path is confined to the investigation folder.
app.MapGet("/evidence/{id}/{**path}", (string id, string? path, InvestigationStore store) =>
{
    string? file;
    try { file = string.IsNullOrEmpty(path) ? null : store.ResolveEvidenceFile(id, path); }
    catch (ArgumentException) { file = null; }
    if (file is null) return Results.NotFound();
    var type = Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        _ => "text/plain",
    };
    return Results.File(file, type);
});

// Progress endpoint polled by the investigation page. Contains no secrets.
app.MapGet("/api/investigations/{id}/status", async (string id, InvestigationStore store, InvestigationRunner runner) =>
{
    var inv = await store.LoadAsync(id);
    return inv is null ? Results.NotFound() : Results.Json(new { step = inv.Step.ToString(), running = runner.IsRunning(id), progress = inv.Progress.TakeLast(40) });
});

app.Run();
return 0;

public partial class Program;
