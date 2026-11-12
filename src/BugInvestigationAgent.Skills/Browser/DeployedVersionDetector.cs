using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Environments;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Browser;

/// <summary>
/// Identifies the KMS version deployed on the target environment after sign-in. Read-only: it only reads the current
/// page, or opens <c>Metadata:VersionPath</c> in a separate tab (GET) and closes it.
/// Per-environment configuration (EnvironmentRegistry:Environments:&lt;id&gt;:Metadata):
/// <c>VersionPath</c> (optional page, e.g. "/About"), <c>VersionSelector</c> (optional CSS selector of the element showing
/// the version), <c>VersionPattern</c> (optional regex with one group capturing the version).
/// Without configuration, the visible page text is searched for "Version 12.28.4"-style text.
/// </summary>
public static class DeployedVersionDetector
{
    public const string PathKey = "VersionPath";
    public const string SelectorKey = "VersionSelector";
    public const string PatternKey = "VersionPattern";

    public const string ClickSelectorKey = "VersionClickSelector";

    // Build/release identifiers carry the exact release ("kms14-suite-release-v14.1.0-20261110.3" → 14.1.0); a displayed
    // "KMS Version: 14.0" may be rounded. So the release form is preferred over the generic "Version x.y" form.
    private static readonly Regex ReleasePattern = new(@"(?i)release-v?(\d{1,3}\.\d{1,3}(?:\.\d{1,4}){0,2})(?!\d)", RegexOptions.Compiled);
    // Requires the word "version" (or "ver"/"v") next to the number so dates and counters are not mistaken for versions.
    private static readonly Regex DefaultPattern = new(@"(?i)\b(?:version|ver\.?|v)\s*[:\-#]?\s*(\d{1,3}\.\d{1,3}(?:\.\d{1,4}){0,2})\b", RegexOptions.Compiled);

    /// <summary>Elements that typically open an "About" dialog showing the version (overridable per environment).</summary>
    private static readonly string[] DefaultAboutSelectors =
    [
        "[title*='About' i]", "[aria-label*='About' i]", "[data-original-title*='About' i]", "[data-bs-original-title*='About' i]",
        "a:has(.fa-info-circle)", "button:has(.fa-info-circle)", "a:has(.fa-circle-info)", "a:has(.bi-info-circle)",
        "img[alt*='About' i]", "img[alt*='info' i]", "[class*='about' i]"
    ];

    /// <summary>The version in <paramref name="text"/> using <paramref name="pattern"/>, or the release form then "Version x.y.z".</summary>
    public static string? Match(string? text, string? pattern = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var patterns = string.IsNullOrWhiteSpace(pattern)
            ? new[] { ReleasePattern, DefaultPattern }
            : new[] { new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)) };
        foreach (var regex in patterns)
        {
            var m = regex.Match(text);
            if (m.Success)
                return (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
        }
        return null;
    }

    /// <summary>
    /// Read-only. Order: configured page/selector; otherwise the signed-in page (visible and hidden text); otherwise the
    /// application's About dialog, opened in a SEPARATE tab of the same session and closed again, so the page the
    /// reproduction starts from is never touched.
    /// </summary>
    public static async Task<(string? Version, string Source)> DetectAsync(IPage page, AppEnvironment env, int timeoutMs)
    {
        env.Metadata.TryGetValue(PathKey, out var path);
        env.Metadata.TryGetValue(SelectorKey, out var selector);
        env.Metadata.TryGetValue(PatternKey, out var pattern);
        env.Metadata.TryGetValue(ClickSelectorKey, out var clickSelector);
        try
        {
            if (!string.IsNullOrWhiteSpace(pattern))
                _ = new Regex(pattern); // validate the configured regex once
        }
        catch (ArgumentException)
        {
            return (null, $"VersionPattern for {env.DisplayName} is not a valid regular expression.");
        }

        try
        {
            // 1) Configured page (separate tab) or the current page.
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(env.BaseUrl))
            {
                var fromPath = await InTabAsync(page, Url(env.BaseUrl, path), timeoutMs, tab => ReadAsync(tab, selector, timeoutMs));
                if (Match(fromPath, pattern) is { } v1)
                    return (v1, $"page {path}");
            }
            else if (Match(await ReadAsync(page, selector, timeoutMs), pattern) is { } v2)
            {
                return (v2, string.IsNullOrWhiteSpace(selector) ? "text on the signed-in page" : $"element {selector} on the signed-in page");
            }

            // 2) About dialog, in a separate tab on the application home page.
            if (!string.IsNullOrWhiteSpace(env.BaseUrl))
            {
                var selectors = string.IsNullOrWhiteSpace(clickSelector) ? DefaultAboutSelectors : new[] { clickSelector };
                var about = await InTabAsync(page, env.BaseUrl, timeoutMs, tab => ReadAboutDialogAsync(tab, selectors, timeoutMs));
                if (Match(about, pattern) is { } v3)
                    return (v3, "the application's About dialog");
            }

            return (null, $"No version was found on the signed-in page or in an About dialog of {env.DisplayName}; " +
                          $"configure Metadata:{ClickSelectorKey} (the element that opens About) or {PathKey}/{SelectorKey} for this environment.");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or UriFormatException)
        {
            return (null, $"Reading the version failed: {ex.Message.Split('\n')[0]}");
        }
    }

    private static string Url(string baseUrl, string path) => new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/')).ToString();

    private static async Task<string> InTabAsync(IPage page, string url, int timeoutMs, Func<IPage, Task<string>> read)
    {
        var tab = await page.Context.NewPageAsync();
        try
        {
            await tab.GotoAsync(url, new PageGotoOptions { Timeout = timeoutMs, WaitUntil = WaitUntilState.DOMContentLoaded });
            await tab.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = Math.Min(timeoutMs, 10_000) })
                .ContinueWith(_ => { }); // best effort
            return await read(tab);
        }
        finally
        {
            await tab.CloseAsync();
        }
    }

    private static async Task<string> ReadAsync(IPage target, string? selector, int timeoutMs)
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var loc = target.Locator(selector).First;
            if (await loc.CountAsync() == 0)
                return "";
            return string.Join("\n", await loc.TextContentAsync(new LocatorTextContentOptions { Timeout = timeoutMs }) ?? "",
                await loc.GetAttributeAsync("title") ?? "", await loc.GetAttributeAsync("content") ?? "");
        }
        // Visible text, hidden text (e.g. a pre-rendered About dialog), title and version meta tags.
        return await target.EvaluateAsync<string>(@"() => [
            document.body ? document.body.innerText : '',
            document.body ? document.body.textContent : '',
            document.title,
            ...[...document.querySelectorAll('meta[name*=version i], [data-version]')].map(e => e.getAttribute('content') || e.getAttribute('data-version') || '')
          ].join('\n')");
    }

    private static async Task<string> ReadAboutDialogAsync(IPage tab, IReadOnlyList<string> selectors, int timeoutMs)
    {
        foreach (var selector in selectors)
        {
            var loc = tab.Locator(selector);
            var count = await loc.CountAsync();
            for (var i = 0; i < Math.Min(count, 3); i++)
            {
                var candidate = loc.Nth(i);
                if (!await candidate.IsVisibleAsync())
                    continue;
                await candidate.ClickAsync(new LocatorClickOptions { Timeout = Math.Min(timeoutMs, 5_000) });
                try
                {
                    await tab.GetByText(new Regex("version|build", RegexOptions.IgnoreCase)).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 3_000 });
                }
                catch (TimeoutException)
                {
                    continue;
                }
                var text = await tab.InnerTextAsync("body", new PageInnerTextOptions { Timeout = timeoutMs });
                if (Match(text) is not null)
                    return text;
            }
        }
        return "";
    }
}
