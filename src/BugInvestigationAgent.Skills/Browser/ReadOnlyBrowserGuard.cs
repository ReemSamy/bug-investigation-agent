using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Environments;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Browser;

/// <summary>
/// Action-level (network-level) enforcement of read-only investigations. This does NOT depend on step text or LLM
/// output: on a read-only environment, once sign-in has succeeded, every browser request that is not
/// GET/HEAD/OPTIONS (form submissions, XHR/fetch writes, deletes) is aborted before it leaves the browser,
/// unless its path matches an administrator-configured read-only API pattern
/// (<see cref="LoginFormOptions.ReadOnlyAllowedWritePaths"/>), or the environment explicitly allows user-confirmed
/// state changes AND the user confirmed them for this investigation.
/// </summary>
public static class ReadOnlyRequestPolicy
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public static (bool Allowed, string Reason) Evaluate(string method, string url, RuntimeContext ctx, AppEnvironment env)
    {
        if (!ctx.IsReadOnlyEnvironment)
            return (true, "environment is not read-only");
        if (SafeMethods.Contains(method))
            return (true, "read-only method");
        if (ctx.AuthenticationStatus != AuthenticationStatus.Succeeded)
            return (true, "sign-in phase (login form submission)");
        if (env.AllowUserConfirmedStateChanges && ctx.StateChangingActionsConfirmed)
            return (true, "state changes explicitly allowed for this environment and confirmed by the user");

        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        foreach (var pattern in env.Login.ReadOnlyAllowedWritePaths)
        {
            if (!string.IsNullOrWhiteSpace(pattern) && Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)))
                return (true, "allow-listed read-only API");
        }

        return (false, $"blocked {method.ToUpperInvariant()} {path} (read-only environment)");
    }
}

public static class ReadOnlyBrowserGuard
{
    /// <summary>Installs the guard on the browser context of an investigation (all pages, frames and workers).</summary>
    public static async Task InstallAsync(IPage page, RuntimeContext ctx, AppEnvironment env, IProgress<string>? progress = null)
    {
        if (!ctx.IsReadOnlyEnvironment)
            return;

        await page.Context.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            var (allowed, reason) = ReadOnlyRequestPolicy.Evaluate(request.Method, request.Url, ctx, env);
            if (allowed)
            {
                await route.ContinueAsync();
                return;
            }

            ctx.BlockedBrowserRequests.Add(reason);
            progress?.Report("Read-only guard: " + reason);
            await route.AbortAsync("blockedbyclient");
        });
        progress?.Report($"Read-only guard active for {env.DisplayName}: non-GET requests are blocked after sign-in.");
    }
}
