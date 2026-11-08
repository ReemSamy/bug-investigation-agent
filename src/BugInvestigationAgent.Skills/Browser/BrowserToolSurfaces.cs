using BugInvestigationAgent.Core.Browser;
using BugInvestigationAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Browser;

/// <summary>Wraps the existing Playwright session hub for deterministic automation.</summary>
public sealed class DeterministicPlaywrightBrowserSurface : IBrowserToolSurface
{
    private readonly IPlaywrightSessionHub _sessions;
    private readonly BrowserTimingOptions _timing;

    public DeterministicPlaywrightBrowserSurface(
        IPlaywrightSessionHub sessions,
        IOptions<AgentOptions> options)
    {
        _sessions = sessions;
        _timing = options.Value.Browser;
    }

    public string BackendId => "playwright-deterministic";
    public bool IsAvailable => true;

    // Session id is set by callers via AsyncLocal or by using hub directly for V1 login flow.
    public Guid? ActiveSessionId { get; set; }

    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        var page = RequirePage();
        await page.GotoAsync(url, new PageGotoOptions { Timeout = _timing.NavigationTimeoutMs, WaitUntil = WaitUntilState.DOMContentLoaded });
    }

    public async Task<string> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var page = RequirePage();
        var title = await page.TitleAsync();
        var url = page.Url;
        string body;
        try { body = await page.InnerTextAsync("body"); }
        catch { body = ""; }
        if (body.Length > 2000)
            body = body[..2000];
        return $"URL={url}\nTitle={title}\nBody:\n{body}";
    }

    public async Task ClickAsync(string targetDescription, CancellationToken cancellationToken = default)
    {
        var page = RequirePage();
        var loc = page.GetByText(targetDescription, new PageGetByTextOptions { Exact = false });
        await loc.First.ClickAsync(new LocatorClickOptions { Timeout = Math.Min(_timing.ActionTimeoutMs, 15_000) });
    }

    public async Task<string> TakeScreenshotAsync(string path, CancellationToken cancellationToken = default)
    {
        var page = RequirePage();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            FullPage = true,
            Mask = Evidence.EvidenceScreenshots.MaskSelectors(null).Select(s => page.Locator(s)).ToArray()
        });
        return path;
    }

    private IPage RequirePage()
    {
        if (ActiveSessionId is null || !_sessions.TryGetPage(ActiveSessionId.Value, out var page) || page is null)
            throw new InvalidOperationException("No active Playwright page. Run BrowserAutomation login first.");
        return page;
    }
}

/// <summary>
/// Placeholder for Playwright MCP adaptive browser control.
/// When Mode=Adaptive/Hybrid and MCP is not wired, callers must fall back to Deterministic.
/// See docs/adaptive-browser-mcp.md.
/// </summary>
public sealed class PlaywrightMcpBrowserSurface : IBrowserToolSurface
{
    private readonly ILogger<PlaywrightMcpBrowserSurface> _logger;
    private readonly BrowserTimingOptions _options;

    public PlaywrightMcpBrowserSurface(IOptions<AgentOptions> options, ILogger<PlaywrightMcpBrowserSurface> logger)
    {
        _options = options.Value.Browser;
        _logger = logger;
    }

    public string BackendId => "playwright-mcp";
    public bool IsAvailable =>
        string.Equals(_options.Mode, "Adaptive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_options.Mode, "Hybrid", StringComparison.OrdinalIgnoreCase);

    public Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Playwright MCP surface is a spike stub. Falling back path should use DeterministicPlaywrightBrowserSurface. Target URL={Url}", url);
        throw new NotSupportedException(
            "Playwright MCP is not fully wired yet. Set Agent:Browser:Mode=Deterministic, or see docs/adaptive-browser-mcp.md to connect @playwright/mcp.");
    }

    public Task<string> SnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("MCP snapshot unavailable (stub). Use deterministic Playwright.");

    public Task ClickAsync(string targetDescription, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Playwright MCP click is not wired yet.");

    public Task<string> TakeScreenshotAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Playwright MCP screenshot is not wired yet.");
}

/// <summary>Resolves which browser surface to use based on Agent:Browser:Mode.</summary>
public interface IBrowserToolSurfaceResolver
{
    IBrowserToolSurface Resolve();
}

public sealed class BrowserToolSurfaceResolver : IBrowserToolSurfaceResolver
{
    private readonly DeterministicPlaywrightBrowserSurface _deterministic;
    private readonly PlaywrightMcpBrowserSurface _mcp;
    private readonly BrowserTimingOptions _options;

    public BrowserToolSurfaceResolver(
        DeterministicPlaywrightBrowserSurface deterministic,
        PlaywrightMcpBrowserSurface mcp,
        IOptions<AgentOptions> options)
    {
        _deterministic = deterministic;
        _mcp = mcp;
        _options = options.Value.Browser;
    }

    public IBrowserToolSurface Resolve()
    {
        if (string.Equals(_options.Mode, "Adaptive", StringComparison.OrdinalIgnoreCase) && _mcp.IsAvailable)
        {
            // Spike: prefer MCP when explicitly Adaptive, but MCP throws until wired —
            // callers should catch and fall back. Hybrid uses deterministic first.
            return _mcp;
        }

        return _deterministic;
    }
}
