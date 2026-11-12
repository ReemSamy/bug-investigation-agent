using System.Text;
using System.Text.Json;
using AI.BugInvestigationTool.Models;
using Microsoft.Playwright;

namespace AI.BugInvestigationTool.Services.Browser;

public interface IBrowserLauncher
{
    Task<IBrowserSession> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct);
}

public sealed record BrowserLaunchOptions(
    Uri StartUrl,
    IReadOnlyCollection<string> AllowedHosts,
    string EvidenceDirectory,
    bool Headless,
    int TimeoutSeconds,
    IReadOnlyCollection<string> RedactSelectors);

public interface IBrowserSession : IAsyncDisposable
{
    IReadOnlyList<string> ConsoleErrors { get; }
    IReadOnlyList<string> NetworkFailures { get; }
    IReadOnlyList<EvidenceItem> Screenshots { get; }
    string CurrentUrl { get; }
    Task<string> NavigateAsync(string url, CancellationToken ct);
    Task<bool> LoginAsync(string username, string password, string? usernameSelector, string? passwordSelector, string? submitSelector, CancellationToken ct);
    Task<string> ClickAsync(string target, CancellationToken ct);
    Task<string> FillAsync(string target, string value, CancellationToken ct);
    Task<string> SelectAsync(string target, string value, CancellationToken ct);
    Task<string> PressAsync(string key, CancellationToken ct);
    Task<string> ReadPageAsync(CancellationToken ct);
    Task<EvidenceItem> ScreenshotAsync(string label, CancellationToken ct);
}

public sealed class PlaywrightBrowserLauncher : IBrowserLauncher
{
    public async Task<IBrowserSession> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
    {
        IPlaywright playwright;
        try { playwright = await Playwright.CreateAsync(); }
        catch (Exception ex) { throw new InvalidOperationException($"Playwright could not start: {ex.Message}"); }

        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new() { Headless = options.Headless });
            // A fresh, isolated context per investigation: no shared cookies, storage or cache.
            var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 1440, Height = 900 },
                IgnoreHTTPSErrors = false,
                AcceptDownloads = false,
            });
            context.SetDefaultTimeout(options.TimeoutSeconds * 1000);
            var session = new PlaywrightBrowserSession(playwright, browser, context, options);
            try
            {
                await session.InitializeAsync();
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
            return session;
        }
        catch (PlaywrightException ex)
        {
            playwright.Dispose();
            throw new InvalidOperationException(
                $"Chromium could not be launched ({ex.Message.Split('\n')[0]}). Run: pwsh src/AI.BugInvestigationTool/bin/Debug/net10.0/playwright.ps1 install chromium");
        }
    }
}

internal sealed class PlaywrightBrowserSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, BrowserLaunchOptions options) : IBrowserSession
{
    private readonly List<string> _console = [];
    private readonly List<string> _network = [];
    private readonly List<EvidenceItem> _shots = [];
    private IPage _page = null!;

    public IReadOnlyList<string> ConsoleErrors => _console;
    public IReadOnlyList<string> NetworkFailures => _network;
    public IReadOnlyList<EvidenceItem> Screenshots => _shots;
    public string CurrentUrl => _page.Url;

    public async Task InitializeAsync()
    {
        // SSRF / arbitrary navigation guard: top-level navigations only to allow-listed hosts,
        // and no sub-requests to private/loopback addresses unless that host is allow-listed.
        await context.RouteAsync("**/*", async route =>
        {
            var req = route.Request;
            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var u)) { await route.ContinueAsync(); return; }
            if (u.Scheme is "data" or "blob" or "about") { await route.ContinueAsync(); return; }
            var allowed = HostPolicy.IsAllowed(u.Host, options.AllowedHosts);
            if (req.IsNavigationRequest && req.Frame.ParentFrame is null && !allowed)
            {
                _network.Add($"BLOCKED navigation to non-allowed host {u.Host}");
                await route.FulfillAsync(new() { Status = 204 }); // 204 = the browser stays on the current page
                return;
            }
            if (!allowed && HostPolicy.IsPrivateOrLoopback(u.Host))
            {
                _network.Add($"BLOCKED request to private address {u.Host}");
                await route.AbortAsync("blockedbyclient");
                return;
            }
            await route.ContinueAsync();
        });

        _page = await context.NewPageAsync();
        _page.Console += (_, msg) =>
        {
            if (msg.Type == "error" && _console.Count < 200) _console.Add(Trim(msg.Text, 500));
        };
        _page.PageError += (_, err) => { if (_console.Count < 200) _console.Add("Uncaught: " + Trim(err, 500)); };
        _page.RequestFailed += (_, r) =>
        {
            if (_network.Count < 200) _network.Add($"{r.Method} {StripQuery(r.Url)} failed: {r.Failure}");
        };
        _page.Response += (_, r) =>
        {
            if (r.Status >= 400 && _network.Count < 200) _network.Add($"{r.Request.Method} {StripQuery(r.Url)} → HTTP {r.Status}");
        };

        var start = await NavigateAsync(options.StartUrl.ToString(), CancellationToken.None);
        if (!start.StartsWith("Navigated", StringComparison.Ordinal))
            throw new InvalidOperationException($"Could not open the environment URL: {start}");
    }

    public async Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(_page.Url is { Length: > 0 } cur && cur != "about:blank" ? new Uri(cur) : options.StartUrl, url, out var target) ||
            target.Scheme is not ("http" or "https"))
            return "Rejected: not an http(s) URL.";
        if (!HostPolicy.IsAllowed(target.Host, options.AllowedHosts))
            return $"Rejected: host '{target.Host}' is not in the allowed environment hosts.";
        try
        {
            var resp = await _page.GotoAsync(target.ToString(), new() { WaitUntil = WaitUntilState.Load });
            await SettleAsync();
            return $"Navigated to {StripQuery(_page.Url)} (HTTP {resp?.Status.ToString() ?? "?"}), title: {await _page.TitleAsync()}";
        }
        catch (PlaywrightException ex)
        {
            return $"Navigation failed: {ex.Message.Split('\n')[0]}";
        }
    }

    public async Task<bool> LoginAsync(string username, string password, string? usernameSelector, string? passwordSelector, string? submitSelector, CancellationToken ct)
    {
        const string defaultUser = "input[type=email], input[autocomplete=username], input[name*=user i], input[id*=user i], input[name*=email i], input[id*=email i], input[name*=login i], input[type=text]";
        const string defaultPass = "input[type=password]";
        const string defaultSubmit = "button[type=submit], input[type=submit], button:has-text('Sign in'), button:has-text('Log in'), button:has-text('Login'), button:has-text('Next')";

        try
        {
            for (var stage = 0; stage < 2; stage++) // supports one- and two-step (username → next → password) forms
            {
                ct.ThrowIfCancellationRequested();
                var pass = _page.Locator(passwordSelector ?? defaultPass).First;
                var user = _page.Locator(usernameSelector ?? defaultUser).First;
                var passVisible = await pass.IsVisibleAsync();
                if (await user.IsVisibleAsync()) await user.FillAsync(username);
                if (passVisible) await pass.FillAsync(password);

                var submit = _page.Locator(submitSelector ?? defaultSubmit).First;
                if (await submit.IsVisibleAsync()) await submit.ClickAsync();
                else if (passVisible) await pass.PressAsync("Enter");
                else await user.PressAsync("Enter");
                await SettleAsync();

                if (passVisible) break;
            }
            return !await _page.Locator(passwordSelector ?? defaultPass).First.IsVisibleAsync();
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return false;
        }
    }

    public Task<string> ClickAsync(string target, CancellationToken ct) => ActAsync($"click {target}", async () =>
    {
        await Locate(target).ClickAsync();
        await SettleAsync();
    }, ct);

    public Task<string> FillAsync(string target, string value, CancellationToken ct) => ActAsync($"fill {target}", async () =>
    {
        var loc = Locate(target);
        if (await loc.GetAttributeAsync("type") == "password") throw new InvalidOperationException("Filling password fields is not allowed; use browser_login.");
        await loc.FillAsync(value);
    }, ct);

    public Task<string> SelectAsync(string target, string value, CancellationToken ct) => ActAsync($"select '{value}' in {target}", async () =>
    {
        await Locate(target).SelectOptionAsync(value);
        await SettleAsync();
    }, ct);

    public Task<string> PressAsync(string key, CancellationToken ct) => ActAsync($"press {key}", async () =>
    {
        await _page.Keyboard.PressAsync(key);
        await SettleAsync();
    }, ct);

    public async Task<string> ReadPageAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = await _page.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''");
        var elements = await _page.EvaluateAsync<JsonElement>(@"() => {
            const out = [];
            const els = document.querySelectorAll('a, button, input, select, textarea, [role=button], [role=link], [role=tab], [role=menuitem]');
            for (const el of els) {
                if (out.length >= 80) break;
                const r = el.getBoundingClientRect();
                if (r.width === 0 || r.height === 0) continue;
                const label = (el.getAttribute('aria-label') || el.innerText || el.value || el.placeholder || el.name || '').trim().slice(0, 60);
                let sel = el.id ? '#' + CSS.escape(el.id) : (el.name ? el.tagName.toLowerCase() + '[name=""' + el.name + '""]' : '');
                if (el.type === 'password') { out.push({ tag: 'input', type: 'password', selector: sel, label: '[password field]' }); continue; }
                out.push({ tag: el.tagName.toLowerCase(), type: el.type || el.getAttribute('role') || '', selector: sel, label });
            }
            return out;
        }");
        var sb = new StringBuilder();
        sb.AppendLine($"URL: {StripQuery(_page.Url)}");
        sb.AppendLine($"Title: {await _page.TitleAsync()}");
        sb.AppendLine("Interactive elements (use selector, or text=<label>):");
        foreach (var e in elements.EnumerateArray())
            sb.AppendLine($"- {e.GetProperty("tag").GetString()} {e.GetProperty("type").GetString()} selector='{e.GetProperty("selector").GetString()}' label='{e.GetProperty("label").GetString()}'");
        sb.AppendLine("Visible text:");
        sb.AppendLine(Trim(text ?? "", 8000));
        return sb.ToString();
    }

    public async Task<EvidenceItem> ScreenshotAsync(string label, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dir = Path.Combine(options.EvidenceDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        var safe = new string(label.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        if (safe.Length > 40) safe = safe[..40];
        var file = $"{_shots.Count + 1:00}-{(safe.Length == 0 ? "screenshot" : safe)}.png";

        // Redaction: password fields plus configured selectors are painted over before capture.
        var masks = new[] { "input[type=password]", "input[autocomplete*=password]" }.Concat(options.RedactSelectors)
            .Select(s => _page.Locator(s)).ToList();
        await _page.ScreenshotAsync(new() { Path = Path.Combine(dir, file), FullPage = true, Mask = masks, MaskColor = "#000000" });

        var item = new EvidenceItem { Kind = "screenshot", Label = label, File = $"screenshots/{file}", Detail = StripQuery(_page.Url) };
        _shots.Add(item);
        return item;
    }

    private ILocator Locate(string target) => _page.Locator(target).First;

    private static async Task<string> ActAsync(string what, Func<Task> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await action();
            return $"OK: {what}";
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            return $"FAILED: {what}: {ex.Message.Split('\n')[0]}";
        }
    }

    private async Task SettleAsync()
    {
        try { await _page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 }); }
        catch (TimeoutException) { /* long-polling pages never go idle; carry on */ }
        catch (PlaywrightException) { }
    }

    private static string StripQuery(string url) => url.Split('?')[0];
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public async ValueTask DisposeAsync()
    {
        try { await context.CloseAsync(); } catch { }
        try { await browser.CloseAsync(); } catch { }
        playwright.Dispose();
    }
}
