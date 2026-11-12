using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services.Browser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AI.BugInvestigationTool.Tests;

/// <summary>
/// A controlled test environment: a tiny web app with a login form and an Orders page whose Save button
/// fails with HTTP 500 and a console error — a reproducible defect for the browser and end-to-end tests.
/// </summary>
public sealed class FixtureSite : IAsyncLifetime
{
    private WebApplication? _app;
    private string _contentRoot = "";
    public string BaseUrl { get; private set; } = "";

    private const string LoginPage = """
        <!doctype html><html><head><title>Shop QA - Sign in</title></head><body>
        <h1>Sign in</h1>
        <form method="post" action="/login">
          <label>User <input id="username" name="username" type="text"></label>
          <label>Password <input id="password" name="password" type="password"></label>
          <button type="submit">Sign in</button>
        </form>{0}</body></html>
        """;

    private const string AppPage = """
        <!doctype html><html><head><title>Shop QA - Orders</title></head><body>
        <h1>Orders</h1>
        <p>Order #1001 for ACME Ltd.</p>
        <button id="save">Save order</button>
        <div id="status"></div>
        <a id="external" href="https://example.org/steal">External link</a>
        <img src="http://10.255.255.1/tracker.png" alt="">
        <script>
          document.getElementById('save').addEventListener('click', async () => {
            const res = await fetch('/api/orders', { method: 'POST' });
            if (!res.ok) {
              console.error('Save failed: ' + res.status);
              document.getElementById('status').textContent = 'Could not save order';
            } else {
              document.getElementById('status').textContent = 'Order saved';
            }
          });
        </script></body></html>
        """;

    public async Task InitializeAsync()
    {
        // Empty content root: the fixture must not pick up the tool's appsettings.json from the test output folder.
        _contentRoot = Directory.CreateTempSubdirectory("bit-fixture-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _contentRoot, EnvironmentName = "Fixture" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapGet("/", () => Results.Content(string.Format(LoginPage, ""), "text/html"));
        _app.MapPost("/login", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            if (form["username"] == "tester" && form["password"] == "P@ssw0rd-secret")
            {
                ctx.Response.Cookies.Append("auth", "1");
                return Results.Redirect("/app");
            }
            return Results.Content(string.Format(LoginPage, "<p class='error'>Invalid credentials</p>"), "text/html");
        });
        _app.MapGet("/app", (HttpContext ctx) => ctx.Request.Cookies.ContainsKey("auth") ? Results.Content(AppPage, "text/html") : Results.Redirect("/"));
        _app.MapPost("/api/orders", () => Results.StatusCode(500));
        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_contentRoot, true); } catch (IOException) { }
    }
}

[Collection("browser")]
public class BrowserTests(FixtureSite site) : IClassFixture<FixtureSite>
{
    private async Task<IBrowserSession> LaunchAsync(string evidenceDir) =>
        await new PlaywrightBrowserLauncher().LaunchAsync(
            new BrowserLaunchOptions(new Uri(site.BaseUrl + "/"), ["127.0.0.1"], evidenceDir, Headless: true, TimeoutSeconds: 10, RedactSelectors: ["#username"]),
            CancellationToken.None);

    [Fact]
    public async Task Logs_in_reproduces_and_captures_console_network_and_screenshots()
    {
        using var dir = new TempDir();
        await using var session = await LaunchAsync(dir.Path);

        Assert.True(await session.LoginAsync("tester", "P@ssw0rd-secret", null, null, null, CancellationToken.None));
        Assert.EndsWith("/app", session.CurrentUrl);
        Assert.StartsWith("OK", await session.ClickAsync("#save", CancellationToken.None));
        var page = await session.ReadPageAsync(CancellationToken.None);
        var shot = await session.ScreenshotAsync("Save error", CancellationToken.None);

        Assert.Contains("Could not save order", page);
        Assert.Contains("selector='#save' label='Save order'", page);
        Assert.Contains(session.ConsoleErrors, e => e.Contains("Save failed: 500"));
        Assert.Contains(session.NetworkFailures, n => n.Contains("/api/orders → HTTP 500"));
        Assert.Contains(session.NetworkFailures, n => n.Contains("BLOCKED request to private address 10.255.255.1"));

        var file = Path.Combine(dir.Path, shot.File!);
        Assert.True(File.Exists(file));
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.True(bytes.Length > 1000);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]); // PNG signature
        Assert.Equal("screenshots/01-save-error.png", shot.File);
    }

    [Fact]
    public async Task Blocks_navigation_outside_allowed_hosts()
    {
        using var dir = new TempDir();
        await using var session = await LaunchAsync(dir.Path);
        await session.LoginAsync("tester", "P@ssw0rd-secret", null, null, null, CancellationToken.None);

        Assert.StartsWith("Rejected", await session.NavigateAsync("https://example.org/", CancellationToken.None));
        Assert.StartsWith("Rejected", await session.NavigateAsync("file:///c:/windows/win.ini", CancellationToken.None));
        await session.ClickAsync("#external", CancellationToken.None); // link to another host
        Assert.Contains("127.0.0.1", session.CurrentUrl);
        Assert.Contains(session.NetworkFailures, n => n.Contains("BLOCKED navigation to non-allowed host example.org"));
        Assert.StartsWith("Navigated", await session.NavigateAsync("/app", CancellationToken.None)); // relative paths on the allowed host work
    }

    [Fact]
    public async Task Wrong_password_fails_login_and_password_fields_cannot_be_filled_by_claude()
    {
        using var dir = new TempDir();
        await using var session = await LaunchAsync(dir.Path);

        Assert.False(await session.LoginAsync("tester", "wrong", null, null, null, CancellationToken.None));
        var fill = await session.FillAsync("#password", "guess", CancellationToken.None);
        Assert.StartsWith("FAILED", fill);
        Assert.Contains("password", fill);
        Assert.Contains("[password field]", await session.ReadPageAsync(CancellationToken.None));
    }
}

[CollectionDefinition("browser", DisableParallelization = true)]
public class BrowserCollection;

/// <summary>
/// Reproducible end-to-end scenario: the real engine, real Playwright and the controlled fixture site.
/// Jira and Claude are replaced by local fixtures; Claude's scripted tool calls drive the real browser.
/// </summary>
[Collection("browser")]
public class EndToEndTests(FixtureSite site) : IClassFixture<FixtureSite>
{
    [Fact]
    public async Task Jira_bug_is_reproduced_in_a_real_browser_and_reported_as_unresolved()
    {
        using var dir = new TempDir();
        var http = EngineWorkflowTests.JiraFake();
        var claude = new ClaudeScript()
            .ThenToolUse("submit_ticket_understanding", EngineWorkflowTests.Understanding())
            .ThenToolUse("browser_read_page", new { })
            .ThenToolUse("browser_click", new { target = "text=Save order" })
            .ThenToolUse("browser_screenshot", new { label = "Error after clicking Save" })
            .ThenToolUse("browser_get_errors", new { })
            .Then(req =>
            {
                // Claude only reports "reproduced" after seeing the real page output and errors.
                var transcript = req.ToJsonString();
                Assert.Contains("Could not save order", transcript);
                Assert.Contains("Save failed: 500", transcript);
                return ClaudeScript.ToolUse("submit_reproduction", EngineWorkflowTests.Repro("yes"));
            })
            .ThenToolUse("submit_investigation_report", EngineWorkflowTests.Report("Unresolved"));
        claude.Register(http);
        var engine = TestEngine.Create(http, dir.Path, new PlaywrightBrowserLauncher());
        var creds = TestEngine.Credentials(envUrl: site.BaseUrl + "/");
        creds.AdoPat = null;

        var inv = await engine.InvestigateAsync("https://acme.atlassian.net/browse/SHOP-42", creds, null, CancellationToken.None);

        Assert.Null(inv.Error);
        Assert.Equal(InvestigationStep.Completed, inv.Step);
        Assert.True(inv.Browser!.LoginSucceeded);
        Assert.Contains(inv.Browser.ConsoleErrors, e => e.Contains("Save failed: 500"));
        Assert.Equal(InvestigationOutcome.Unresolved, inv.Report!.Outcome);

        var shots = inv.Evidence.Where(e => e.Kind == "screenshot").ToList();
        Assert.Equal(["start page", "after login", "Error after clicking Save", "final state"], shots.Select(s => s.Label));
        Assert.All(shots, s => Assert.NotNull(engine.Store.ResolveEvidenceFile(inv.Id, s.File!)));

        var report = await File.ReadAllTextAsync(Path.Combine(engine.Store.FolderOf(inv.Id), "report.md"));
        Assert.Contains("UNRESOLVED", report);
        Assert.DoesNotContain("P@ssw0rd-secret", report);
        Assert.Contains("03-error-after-clicking-save.png", inv.JiraCommentDraft);
    }
}
