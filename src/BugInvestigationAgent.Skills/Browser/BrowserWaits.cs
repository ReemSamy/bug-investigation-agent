using BugInvestigationAgent.Core.Configuration;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Browser;

internal static class BrowserWaits
{
    public static async Task WaitForPageReadyAsync(IPage page, BrowserTimingOptions timing)
    {
        try
        {
            await page.WaitForLoadStateAsync(
                LoadState.DOMContentLoaded,
                new PageWaitForLoadStateOptions { Timeout = timing.SettleTimeoutMs });
        }
        catch (TimeoutException) { /* continue */ }
        catch (PlaywrightException) { /* continue */ }

        // Prefer "load" over networkidle — KMS keeps websockets/polling alive.
        try
        {
            await page.WaitForLoadStateAsync(
                LoadState.Load,
                new PageWaitForLoadStateOptions { Timeout = timing.SettleTimeoutMs });
        }
        catch (TimeoutException) { /* continue */ }
        catch (PlaywrightException) { /* continue */ }

        await WaitForSpinnersAsync(page, timing);
        await page.WaitForTimeoutAsync(Math.Max(500, timing.PostActionDelayMs));
    }

    public static async Task WaitForSpinnersAsync(IPage page, BrowserTimingOptions timing)
    {
        // Keep selectors specific — broad matches like [class*='spinner'] can match chrome icons forever.
        var selectors = new[]
        {
            ".k-loading-mask",
            ".k-loading-image",
            ".k-i-loading:visible",
            ".k-loading:visible"
        };

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Min(timing.SpinnerTimeoutMs, 20_000));
        while (DateTime.UtcNow < deadline)
        {
            var anyVisible = false;
            foreach (var sel in selectors)
            {
                try
                {
                    var loc = page.Locator(sel);
                    var count = await loc.CountAsync();
                    for (var i = 0; i < Math.Min(count, 3); i++)
                    {
                        if (await loc.Nth(i).IsVisibleAsync())
                        {
                            anyVisible = true;
                            break;
                        }
                    }
                }
                catch (PlaywrightException)
                {
                    // ignore
                }

                if (anyVisible)
                    break;
            }

            if (!anyVisible)
                return;

            await page.WaitForTimeoutAsync(500);
        }
    }
}