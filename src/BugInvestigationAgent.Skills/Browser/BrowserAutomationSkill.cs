using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Browser;

public sealed class BrowserAutomationSkill : ISkill
{
    private readonly ICredentialVault _vault;
    private readonly ISecretRedactor _redactor;
    private readonly AgentOptions _options;
    private readonly IPlaywrightSessionHub _sessions;
    private readonly ILogger<BrowserAutomationSkill> _logger;

    public BrowserAutomationSkill(
        ICredentialVault vault,
        ISecretRedactor redactor,
        IOptions<AgentOptions> options,
        IPlaywrightSessionHub sessions,
        ILogger<BrowserAutomationSkill> logger)
    {
        _vault = vault;
        _redactor = redactor;
        _options = options.Value;
        _sessions = sessions;
        _logger = logger;
    }

    public string SkillId => SkillIds.BrowserAutomation;
    public string DisplayName => "Browser Automation";
    public AgentPermission RequiredPermissions => AgentPermission.UseBrowser;

    public bool CanExecute(RuntimeContext context, IPermissionGate permissions) =>
        permissions.IsAllowed(RequiredPermissions);

    public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context;
        if (string.IsNullOrWhiteSpace(ctx.EnvironmentUrl))
        {
            ctx.ReproductionOutcome = ReproductionOutcome.Blocked;
            ctx.ReproductionNotes = "Environment URL is required before browser automation.";
            return SkillResult.Ok(ctx.ReproductionNotes);
        }

        if (ctx.CredentialProfileId is null)
        {
            ctx.ReproductionOutcome = ReproductionOutcome.Blocked;
            ctx.ReproductionNotes = "Environment login credentials are required before opening the portal.";
            return SkillResult.Ok(ctx.ReproductionNotes);
        }

        var secret = await _vault.ResolveSecretAsync(ctx.CredentialProfileId.Value, cancellationToken);
        if (secret is null)
        {
            ctx.ReproductionOutcome = ReproductionOutcome.Blocked;
            ctx.ReproductionNotes = "Could not resolve credentials from vault. Re-enter the portal password.";
            ctx.CredentialProfileId = null;
            return SkillResult.Ok(ctx.ReproductionNotes);
        }

        _redactor.RegisterSecret(secret.Password);
        _redactor.RegisterSecret(secret.Token);

        var evidenceDir = ResolveEvidenceDir(ctx.SessionId);
        Directory.CreateDirectory(evidenceDir);

        try
        {
            var page = await _sessions.StartAsync(ctx.SessionId, cancellationToken);
            page.SetDefaultTimeout(_options.Browser.ActionTimeoutMs);
            page.SetDefaultNavigationTimeout(_options.Browser.NavigationTimeoutMs);
            request.Progress?.Report("Navigating to environment URL.");
            var response = await page.GotoAsync(ctx.EnvironmentUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.Browser.NavigationTimeoutMs
            });
            if (response is null || (int)response.Status >= 500)
            {
                ctx.ReproductionOutcome = ReproductionOutcome.EnvironmentUnavailable;
                ctx.ReproductionNotes = $"Environment returned status {(response?.Status)?.ToString() ?? "none"}.";
                await _sessions.CloseAsync(ctx.SessionId);
                return SkillResult.Ok(ctx.ReproductionNotes);
            }

            await TryLoginAsync(page, secret, ctx, request.Progress, _options.Browser);
            await BrowserWaits.WaitForPageReadyAsync(page, _options.Browser);
            request.Progress?.Report("Waiting for home/assignments content to finish loading…");
            await BrowserWaits.WaitForSpinnersAsync(page, _options.Browser);
            var screenshot = Path.Combine(evidenceDir, "login-or-home.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshot, FullPage = true });
            ctx.Evidence.Add(new EvidenceItem
            {
                Kind = "screenshot",
                Path = screenshot,
                Caption = "After login / connect — home page before navigating into the ticket feature"
            });
            request.Progress?.Report("Browser session ready (kept open for reproduction).");
            return SkillResult.Ok("Connected to environment.");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            await _sessions.CloseAsync(ctx.SessionId);
            ctx.ReproductionOutcome = ReproductionOutcome.Error;
            ctx.ReproductionNotes = "Playwright browsers not installed. Run: pwsh bin/Debug/net8.0/playwright.ps1 install chromium";
            _logger.LogWarning(ex, "Playwright browser missing");
            return SkillResult.Ok(ctx.ReproductionNotes);
        }
        catch (Exception ex)
        {
            await _sessions.CloseAsync(ctx.SessionId);
            ctx.ReproductionOutcome = ReproductionOutcome.Error;
            ctx.ReproductionNotes = SummarizeBrowserError(_redactor.Redact(ex.Message));
            _logger.LogWarning(ex, "Browser automation failed");
            return SkillResult.Ok("Browser automation encountered an error (classified, not as NOT_REPRODUCED).");
        }
    }

    private static async Task TryLoginAsync(
        IPage page,
        CredentialSecret secret,
        RuntimeContext ctx,
        IProgress<string>? progress,
        BrowserTimingOptions timing)
    {
        var userSelectors = new[]
        {
            "#UserName",
            "input[name='UserName']",
            "input[autocomplete='username']",
            "input[name*=user i]",
            "input[id*=user i]",
            "input[type=text]",
            "input[type=email]"
        };
        var passSelectors = new[]
        {
            "#Password",
            "input[name='Password']",
            "input[autocomplete='current-password']",
            "input[type=password]"
        };
        var submitSelectors = new[]
        {
            "button[type=submit]:visible",
            "input[type=submit]:visible",
            "#btnSubmit",
            "button:has-text('Log in')",
            "button:has-text('Sign in')",
            "button:has-text('Submit')"
        };

        var user = await FindVisibleAsync(page, userSelectors);
        var pass = await FindVisibleAsync(page, passSelectors);

        if (user is null || pass is null)
        {
            progress?.Report("No visible login form detected; continuing with current page.");
            return;
        }

        progress?.Report($"Logging in as '{ctx.Username ?? secret.Username}' (role: {ctx.Role ?? "n/a"}).");
        await user.FillAsync(secret.Username, new LocatorFillOptions { Timeout = timing.ActionTimeoutMs });
        await pass.FillAsync(secret.Password ?? secret.Token ?? "", new LocatorFillOptions { Timeout = timing.ActionTimeoutMs });

        foreach (var sel in submitSelectors)
        {
            var btn = page.Locator(sel).First;
            if (await btn.CountAsync() == 0 || !await btn.IsVisibleAsync())
                continue;

            await btn.ClickAsync(new LocatorClickOptions { Timeout = timing.ActionTimeoutMs });
            await BrowserWaits.WaitForPageReadyAsync(page, timing);
            return;
        }
    }

    private static async Task<ILocator?> FindVisibleAsync(IPage page, IEnumerable<string> selectors)
    {
        foreach (var sel in selectors)
        {
            var loc = page.Locator(sel);
            var count = await loc.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var candidate = loc.Nth(i);
                try
                {
                    if (await candidate.IsVisibleAsync())
                        return candidate;
                }
                catch (PlaywrightException)
                {
                }
            }
        }

        return null;
    }

    internal static string SummarizeBrowserError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Browser automation failed.";

        var first = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith('-'))
            ?? message.Split('\n')[0].Trim();

        if (message.Contains("not visible", StringComparison.OrdinalIgnoreCase))
            return "Login failed: targeted a hidden form field. Retry Continue — the agent now prefers the visible username/password fields.";

        return first.Length > 240 ? first[..240] + "…" : first;
    }

    private string ResolveEvidenceDir(Guid sessionId)
    {
        var root = Path.IsPathRooted(_options.EvidenceDirectory)
            ? _options.EvidenceDirectory
            : Path.Combine(Directory.GetCurrentDirectory(), _options.EvidenceDirectory);
        return Path.Combine(root, sessionId.ToString("N"));
    }
}
