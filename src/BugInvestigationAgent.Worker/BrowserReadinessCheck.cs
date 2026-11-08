using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Worker;

/// <summary>Worker readiness: Chromium can actually be launched (result cached — launching is expensive).</summary>
public sealed class BrowserReadinessCheck : IHealthCheck
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (DateTimeOffset At, HealthCheckResult Result)? _cached;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_cached is { } c && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(10))
            return c.Result;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } c2 && DateTimeOffset.UtcNow - c2.At < TimeSpan.FromMinutes(10))
                return c2.Result;
            HealthCheckResult result;
            try
            {
                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
                result = HealthCheckResult.Healthy($"chromium {browser.Version}");
            }
            catch (Exception ex)
            {
                result = HealthCheckResult.Unhealthy("browser cannot start: " + ex.GetType().Name);
            }
            _cached = (DateTimeOffset.UtcNow, result);
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }
}
