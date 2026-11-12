using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Skills.Browser;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Tests;

/// <summary>
/// Real Chromium against a fake KMS site served by request routing (no network, no real environment). Reproduces the
/// kms14-portal layout: no version on the home page, an ⓘ icon that opens an "About" dialog with the build number.
/// </summary>
public class DeployedVersionDetectorBrowserTests
{
    private const string Site = "https://kms.example.test";
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private const string HomeHtml = """
        <html><body>
          <a href="#" title="About" id="info" onclick="document.getElementById('about').style.display='block';return false;">ⓘ</a>
          <h1>VisiumKMS</h1><table><tr><td>MOC-26-81</td></tr></table>
          <footer>© 2026 - VisiumKMS</footer>
          <div id="about" style="display:none">
            <h2>About</h2>
            <div>KMS Version: 14.0</div>
            <div>Build Number: kms14-suite-release-v14.1.0-20261110.3</div>
          </div>
        </body></html>
        """;

    [OneTimeSetUp]
    public async Task Launch()
    {
        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex)
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

    private static AppEnvironment Env(Dictionary<string, string>? metadata = null) => new()
    {
        Id = "KMS14REL", DisplayName = "kms14-portal", BaseUrl = Site, Type = EnvironmentType.ReleaseCandidate, Enabled = true,
        Metadata = metadata ?? new Dictionary<string, string>(), Login = new LoginFormOptions()
    };

    private async Task<(IPage Page, IBrowserContext Context)> OpenHomeAsync(string html)
    {
        var context = await _browser!.NewContextAsync();
        await context.RouteAsync(Site + "/**", route => route.FulfillAsync(new RouteFulfillOptions { ContentType = "text/html", Body = html }));
        var page = await context.NewPageAsync();
        await page.GotoAsync(Site + "/");
        return (page, context);
    }

    [Test]
    public async Task Version_is_read_from_the_about_dialog_in_a_separate_tab_and_the_page_is_untouched()
    {
        var (page, context) = await OpenHomeAsync(HomeHtml);
        await page.EvaluateAsync("() => document.getElementById('about').remove()"); // the live page has no pre-rendered dialog text

        var (version, source) = await DeployedVersionDetector.DetectAsync(page, Env(), 10_000);

        Assert.That(version, Is.EqualTo("14.1.0"), source);
        Assert.That(source, Does.Contain("About"));
        Assert.That(context.Pages, Has.Count.EqualTo(1), "the probe tab was closed");
        Assert.That(page.Url, Is.EqualTo(Site + "/"), "the reproduction page did not move");
        await context.CloseAsync();
    }

    [Test]
    public async Task Pre_rendered_hidden_about_text_is_used_without_clicking()
    {
        var (page, context) = await OpenHomeAsync(HomeHtml);
        var (version, source) = await DeployedVersionDetector.DetectAsync(page, Env(), 10_000);
        Assert.That(version, Is.EqualTo("14.1.0"));
        Assert.That(source, Does.Contain("signed-in page"));
        await context.CloseAsync();
    }

    [Test]
    public async Task No_version_anywhere_gives_a_clear_reason_not_an_error()
    {
        var (page, context) = await OpenHomeAsync("<html><body><h1>VisiumKMS</h1><a title='Help'>?</a></body></html>");
        var (version, source) = await DeployedVersionDetector.DetectAsync(page, Env(), 5_000);
        Assert.That(version, Is.Null);
        Assert.That(source, Does.StartWith("No version was found"));
        await context.CloseAsync();
    }
}
