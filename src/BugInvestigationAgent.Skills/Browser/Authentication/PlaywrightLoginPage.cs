using BugInvestigationAgent.Core.Configuration;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Browser.Authentication;

/// <summary><see cref="ILoginPage"/> over a live Playwright page.</summary>
public sealed class PlaywrightLoginPage : ILoginPage
{
    private readonly IPage _page;
    private readonly BrowserTimingOptions _timing;

    public PlaywrightLoginPage(IPage page, BrowserTimingOptions timing)
    {
        _page = page;
        _timing = timing;
    }

    public string Url => _page.Url;

    public async Task<ILoginField?> FindVisibleAsync(IReadOnlyList<string> selectors, CancellationToken cancellationToken = default)
    {
        foreach (var sel in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ILocator loc;
            int count;
            try
            {
                loc = _page.Locator(sel);
                count = await loc.CountAsync();
            }
            catch (PlaywrightException)
            {
                continue; // invalid selector in configuration — skip it
            }

            for (var i = 0; i < count; i++)
            {
                var candidate = loc.Nth(i);
                try
                {
                    if (await candidate.IsVisibleAsync())
                        return new Field(candidate, _timing);
                }
                catch (PlaywrightException)
                {
                }
            }
        }

        return null;
    }

    public async Task<bool> AnyVisibleAsync(IReadOnlyList<string> selectors, CancellationToken cancellationToken = default) =>
        await FindVisibleAsync(selectors, cancellationToken) is not null;

    public async Task<string> GetVisibleTextAsync(CancellationToken cancellationToken = default)
    {
        try { return await _page.InnerTextAsync("body", new PageInnerTextOptions { Timeout = 5_000 }); }
        catch (PlaywrightException) { return ""; }
        catch (TimeoutException) { return ""; }
    }

    public Task WaitForSettledAsync(CancellationToken cancellationToken = default) =>
        BrowserWaits.WaitForPageReadyAsync(_page, _timing);

    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken = default) =>
        Task.Delay(milliseconds, cancellationToken);

    private sealed class Field : ILoginField
    {
        private readonly ILocator _locator;
        private readonly BrowserTimingOptions _timing;

        public Field(ILocator locator, BrowserTimingOptions timing)
        {
            _locator = locator;
            _timing = timing;
        }

        public Task FillAsync(string value, CancellationToken cancellationToken = default) =>
            _locator.FillAsync(value, new LocatorFillOptions { Timeout = _timing.ActionTimeoutMs });

        public Task ClickAsync(CancellationToken cancellationToken = default) =>
            _locator.ClickAsync(new LocatorClickOptions { Timeout = _timing.ActionTimeoutMs });

        public Task PressAsync(string key, CancellationToken cancellationToken = default) =>
            _locator.PressAsync(key, new LocatorPressOptions { Timeout = _timing.ActionTimeoutMs });
    }
}
