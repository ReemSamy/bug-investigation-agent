using BugInvestigationAgent.Core.Browser;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Browser;

/// <summary>
/// Keeps one Playwright browser/page alive for a workflow session so login is not lost between skills.
/// </summary>
public interface IPlaywrightSessionHub : IBrowserSessionCloser
{
    Task<IPage> StartAsync(Guid sessionId, CancellationToken cancellationToken = default);
    bool TryGetPage(Guid sessionId, out IPage page);
}

public sealed class PlaywrightSessionHub : IPlaywrightSessionHub, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SessionEntry> _sessions = new();

    public async Task<IPage> StartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await CloseAsync(sessionId);

        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
        });
        var page = await context.NewPageAsync();

        lock (_gate)
        {
            _sessions[sessionId] = new SessionEntry(playwright, browser, context, page);
        }

        return page;
    }

    public bool TryGetPage(Guid sessionId, out IPage page)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var entry))
            {
                page = entry.Page;
                return true;
            }
        }

        page = null!;
        return false;
    }

    public async Task CloseAsync(Guid sessionId)
    {
        SessionEntry? entry;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out entry))
                return;
        }

        await DisposeEntryAsync(entry);
    }

    public async ValueTask DisposeAsync()
    {
        List<SessionEntry> entries;
        lock (_gate)
        {
            entries = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var entry in entries)
            await DisposeEntryAsync(entry);
    }

    private static async Task DisposeEntryAsync(SessionEntry entry)
    {
        try { await entry.Context.CloseAsync(); } catch { /* ignore */ }
        try { await entry.Browser.CloseAsync(); } catch { /* ignore */ }
        entry.Playwright.Dispose();
    }

    private sealed record SessionEntry(IPlaywright Playwright, IBrowser Browser, IBrowserContext Context, IPage Page);
}
