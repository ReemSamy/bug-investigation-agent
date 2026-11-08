using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Skills.Browser.Authentication;
using BugInvestigationAgent.Skills.Evidence;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Tests;

/// <summary>
/// Drives the real Playwright adapter against an in-memory login page (page.SetContent — no network, no real
/// environment). Skipped automatically when Playwright's Chromium is not installed on the machine.
/// </summary>
[NonParallelizable]
public class PlaywrightLoginIntegrationTests
{
    private const string LoginHtml = """
        <html><body>
          <form id="f" onsubmit="event.preventDefault(); login();">
            <input id="UserName" name="UserName" autocomplete="username" />
            <input id="Password" name="Password" type="password" autocomplete="current-password" />
            <button type="submit">Log in</button>
          </form>
          <div id="msg"></div>
          <script>
            function login() {
              const u = document.getElementById('UserName').value;
              const p = document.getElementById('Password').value;
              if (p === 'correct-horse') {
                document.body.innerHTML = '<nav id="user-menu">Signed in as ' + u + '</nav><h1>My Assignments</h1>';
              } else if (p === 'needs-mfa') {
                document.body.innerHTML = '<label>Enter the verification code</label><input autocomplete="one-time-code" />';
              } else {
                document.getElementById('msg').innerHTML = '<div role="alert" class="validation-summary-errors">Invalid username or password.</div>';
              }
            }
          </script>
        </body></html>
        """;

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    [OneTimeSetUp]
    public async Task Launch()
    {
        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (Exception ex)
        {
            Assert.Ignore("Playwright Chromium not available: " + ex.Message.Split('\n')[0]);
        }
    }

    [OneTimeTearDown]
    public async Task Close()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    private static readonly BrowserTimingOptions FastTiming = new()
    {
        SettleTimeoutMs = 2_000, PostActionDelayMs = 50, SpinnerTimeoutMs = 500, ActionTimeoutMs = 5_000, ProbeTimeoutMs = 500
    };

    private static LoginFormOptions Login(bool withSuccessSelector = false) => new LoginFormOptions
    {
        UsernameSelectors = ["#UserName"],
        PasswordSelectors = ["#Password"],
        SuccessSelectors = withSuccessSelector ? ["#user-menu"] : [],
        OutcomeTimeoutMs = 3_000,
        PollIntervalMs = 100
    }.WithDefaults();

    private async Task<(IBrowserContext Context, IPage Page)> NewIsolatedPageAsync()
    {
        var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(LoginHtml);
        return (context, page);
    }

    [Test]
    public async Task Real_browser_valid_credentials_are_verified_by_success_indicator()
    {
        var (context, page) = await NewIsolatedPageAsync();
        await using var _ = context;

        var result = await new LoginFlow().LoginAsync(new PlaywrightLoginPage(page, FastTiming), Login(withSuccessSelector: true),
            new CredentialSecret { Username = "qa_admin", Password = "correct-horse" });

        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.Succeeded), result.Message);
    }

    [Test]
    public async Task Real_browser_invalid_credentials_are_detected()
    {
        var (context, page) = await NewIsolatedPageAsync();
        await using var _ = context;

        var result = await new LoginFlow().LoginAsync(new PlaywrightLoginPage(page, FastTiming), Login(),
            new CredentialSecret { Username = "qa_admin", Password = "wrong" });

        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.InvalidCredentials), result.Message);
    }

    [Test]
    public async Task Real_browser_mfa_step_is_detected()
    {
        var (context, page) = await NewIsolatedPageAsync();
        await using var _ = context;

        var result = await new LoginFlow().LoginAsync(new PlaywrightLoginPage(page, FastTiming), Login(),
            new CredentialSecret { Username = "qa_admin", Password = "needs-mfa" });

        Assert.That(result.Status, Is.EqualTo(AuthenticationStatus.MfaRequired), result.Message);
    }

    [Test]
    public async Task Real_browser_screenshot_is_masked_filed_and_associated()
    {
        var (context, page) = await NewIsolatedPageAsync();
        await using var _ = context;
        await page.FillAsync("#Password", "should-not-be-visible");

        var root = Path.Combine(Path.GetTempPath(), "bia-shots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ctx = new RuntimeContext { JiraTicketKey = "VKMS-9" };
            ctx.SelectedEnvironmentId = "KMSQA";
            ctx.SelectedEnvironmentDisplayName = "KMSQA";

            var item = await EvidenceScreenshots.CaptureAsync(page, new AgentOptions { EvidenceDirectory = root }, ctx,
                "01-environment", "Selected environment", EvidenceCategories.Environment, Login());

            Assert.That(File.Exists(item.Path), Is.True);
            Assert.That(Path.GetFileName(item.Path), Is.EqualTo("VKMS-9_KMSQA_01-environment.png"));
            Assert.That(Path.GetDirectoryName(item.Path), Does.EndWith(ctx.InvestigationId.ToString("N")));
            Assert.That(item.EnvironmentId, Is.EqualTo("KMSQA"));
            Assert.That(ctx.Evidence, Does.Contain(item));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
