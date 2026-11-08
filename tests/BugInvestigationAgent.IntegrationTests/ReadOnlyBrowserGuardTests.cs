using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Skills.Browser;
using BugInvestigationAgent.Skills.Browser.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BugInvestigationAgent.IntegrationTests;

/// <summary>
/// REAL Chromium (Playwright) against the REAL local portal: the production read-only guard blocks form submissions
/// and fetch DELETEs at the network level — the server never receives them — while the same actions reach a
/// non-production environment. Login verification uses the configured success selector.
/// </summary>
[NonParallelizable]
public class ReadOnlyBrowserGuardTests
{
    private TestPortal _portal = null!;
    private PlaywrightSessionHub _hub = null!;
    private string _evidence = null!;

    private sealed class NoVault : ICredentialVault
    {
        public Task<IReadOnlyList<CredentialProfile>> ListProfilesAsync(CancellationToken c = default) => Task.FromResult<IReadOnlyList<CredentialProfile>>([]);
        public Task<CredentialProfile?> FindForEnvironmentAsync(string e, string? u = null, CancellationToken c = default) => Task.FromResult<CredentialProfile?>(null);
        public Task<CredentialProfile?> GetProfileAsync(Guid id, CancellationToken c = default) => Task.FromResult<CredentialProfile?>(null);
        public Task<CredentialProfile?> FindMatchAsync(string? s, string? e, string? u, string? r, CancellationToken c = default) => Task.FromResult<CredentialProfile?>(null);
        public Task<CredentialProfile> SaveProfileAsync(CredentialProfile p, CredentialSecret s, CancellationToken c = default) => throw new NotSupportedException();
        public Task<CredentialSecret?> ResolveSecretAsync(Guid id, CancellationToken c = default) => Task.FromResult<CredentialSecret?>(null);
        public Task DeleteProfileAsync(Guid id, CancellationToken c = default) => Task.CompletedTask;
    }

    [SetUp]
    public async Task SetUp()
    {
        _portal = await TestPortal.StartAsync();
        _hub = new PlaywrightSessionHub();
        _evidence = Path.Combine(Path.GetTempPath(), "bia-guard-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _hub.DisposeAsync();
        await _portal.DisposeAsync();
        try { Directory.Delete(_evidence, true); } catch { }
    }

    private (BrowserAutomationSkill Skill, EnvironmentSelectionService Envs, InMemorySessionCredentialStore Store) Create()
    {
        var registry = new EnvironmentRegistryOptions
        {
            DefaultLogin = new LoginFormOptions
            {
                UsernameSelectors = ["#UserName"], PasswordSelectors = ["#Password"], SuccessSelectors = ["#user-menu"], OutcomeTimeoutMs = 8000
            },
            Environments = new(StringComparer.OrdinalIgnoreCase)
            {
                ["KMSPROD"] = new() { ApplicationId = "KMS", BaseUrl = _portal.EnvUrl("prod"), Type = EnvironmentType.Production },
                ["KMSQA"] = new() { ApplicationId = "KMS", BaseUrl = _portal.EnvUrl("qa"), Type = EnvironmentType.QA }
            }
        };
        var envs = new EnvironmentSelectionService(new ConfigurationEnvironmentRegistry(registry));
        var store = new InMemorySessionCredentialStore();
        var options = new AgentOptions
        {
            EvidenceDirectory = _evidence,
            Browser = new BrowserTimingOptions { NavigationTimeoutMs = 15000, ActionTimeoutMs = 5000, SettleTimeoutMs = 3000, PostActionDelayMs = 150, SpinnerTimeoutMs = 500 }
        };
        var skill = new BrowserAutomationSkill(new NoVault(), store, envs, new SecretRedactor(), Options.Create(options), _hub, new LoginFlow(),
            NullLogger<BrowserAutomationSkill>.Instance);
        return (skill, envs, store);
    }

    private static SkillExecutionRequest Request(RuntimeContext ctx) => new() { Context = ctx, Permissions = new PermissionGate((AgentPermission)int.MaxValue) };

    [Test]
    public async Task Portal_serves_its_login_page()
    {
        using var http = new HttpClient();
        var resp = await http.GetAsync(_portal.EnvUrl("qa"));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That((int)resp.StatusCode, Is.EqualTo(200), body);
        Assert.That(body, Does.Contain("id=\"UserName\""));
    }

    [Test]
    public async Task Production_blocks_form_submission_and_fetch_delete_before_they_reach_the_server()
    {
        var (skill, envs, store) = Create();
        var ctx = new RuntimeContext { JiraTicketKey = "ABC-9" };
        envs.Select(ctx, "KMSPROD");
        Assert.That(ctx.IsReadOnlyEnvironment, Is.True);
        store.Set(ctx.InvestigationId, "KMSPROD", new CredentialSecret { Username = "viewer", Password = "prod-secret-V4!" });

        await skill.ExecuteAsync(Request(ctx));
        Assert.That(ctx.AuthenticationStatus, Is.EqualTo(AuthenticationStatus.Succeeded), ctx.AuthenticationMessage);
        Assert.That(_portal.Hits.Any(h => h is { Env: "prod", Method: "POST", Path: "/prod/login" }), Is.True, "login POST allowed during sign-in");

        Assert.That(_hub.TryGetPage(ctx.InvestigationId, out var page), Is.True);
        await page.ClickAsync("#delete");                     // fetch DELETE (page stays)
        await page.WaitForTimeoutAsync(500);
        await page.ClickAsync("#save");                       // form submission (POST) — aborted navigation
        await page.WaitForTimeoutAsync(1000);
        await page.GotoAsync(_portal.EnvUrl("prod") + "/incident"); // reads still work

        Assert.That(_portal.Hits.Any(h => h.Path == "/prod/save"), Is.False, "the POST never reached the server");
        Assert.That(_portal.Hits.Any(h => h.Method == "DELETE"), Is.False, "the DELETE never reached the server");
        Assert.That(_portal.Hits.Any(h => h is { Path: "/prod/incident", User: "viewer" }), Is.True);
        Assert.That(ctx.BlockedBrowserRequests, Has.Some.Contains("POST /prod/save").And.Some.Contains("DELETE /prod/item/1"));
    }

    [Test]
    public async Task Non_production_environment_is_not_restricted()
    {
        var (skill, envs, store) = Create();
        var ctx = new RuntimeContext { JiraTicketKey = "ABC-9" };
        envs.Select(ctx, "KMSQA");
        store.Set(ctx.InvestigationId, "KMSQA", new CredentialSecret { Username = "carol_qa", Password = "qa-secret-C3!" });

        await skill.ExecuteAsync(Request(ctx));
        Assert.That(ctx.AuthenticationStatus, Is.EqualTo(AuthenticationStatus.Succeeded), ctx.AuthenticationMessage);
        _hub.TryGetPage(ctx.InvestigationId, out var page);
        await page.ClickAsync("#save");
        await page.WaitForSelectorAsync("#saved");
        Assert.That(_portal.Hits.Any(h => h is { Path: "/qa/save", Method: "POST", User: "carol_qa" }), Is.True);
        Assert.That(ctx.BlockedBrowserRequests, Is.Empty);
    }

    [Test]
    public async Task Wrong_password_is_detected_as_invalid_credentials_by_a_real_login()
    {
        var (skill, envs, store) = Create();
        var ctx = new RuntimeContext { JiraTicketKey = "ABC-9" };
        envs.Select(ctx, "KMSQA");
        store.Set(ctx.InvestigationId, "KMSQA", new CredentialSecret { Username = "carol_qa", Password = "not-the-password" });

        await skill.ExecuteAsync(Request(ctx));

        Assert.That(ctx.AuthenticationStatus, Is.EqualTo(AuthenticationStatus.InvalidCredentials));
        Assert.That(_portal.Hits.Any(h => h.User == "carol_qa"), Is.False, "never signed in");
        var shots = Directory.GetFiles(Path.Combine(_evidence, ctx.InvestigationId.ToString("N")), "*.png");
        Assert.That(shots.Select(Path.GetFileName), Has.Some.Contains("ABC-9_KMSQA_02-login-rejected"));
    }
}
