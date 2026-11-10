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

    // Requires the word "version" (or "ver"/"v") next to the number so dates and counters are not mistaken for versions.
    private static readonly Regex DefaultPattern = new(@"(?i)\b(?:version|ver\.?|v)\s*[:\-#]?\s*(\d{1,3}\.\d{1,3}(?:\.\d{1,4}){0,2})\b", RegexOptions.Compiled);

    /// <summary>The version in <paramref name="text"/> using <paramref name="pattern"/> (or the default "Version x.y.z" pattern).</summary>
    public static string? Match(string? text, string? pattern = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var regex = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        var m = regex.Match(text);
        return !m.Success ? null : (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
    }

    public static async Task<(string? Version, string Source)> DetectAsync(IPage page, AppEnvironment env, int timeoutMs)
    {
        env.Metadata.TryGetValue(PathKey, out var path);
        env.Metadata.TryGetValue(SelectorKey, out var selector);
        env.Metadata.TryGetValue(PatternKey, out var patternText);
        var configured = !string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(selector) || !string.IsNullOrWhiteSpace(patternText);

        Regex pattern;
        try
        {
            pattern = string.IsNullOrWhiteSpace(patternText) ? DefaultPattern : new Regex(patternText, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return (null, $"VersionPattern for {env.DisplayName} is not a valid regular expression.");
        }

        IPage? probePage = null;
        try
        {
            var target = page;
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(env.BaseUrl))
            {
                probePage = await page.Context.NewPageAsync();
                await probePage.GotoAsync(new Uri(new Uri(env.BaseUrl.TrimEnd('/') + "/"), path.TrimStart('/')).ToString(),
                    new PageGotoOptions { Timeout = timeoutMs, WaitUntil = WaitUntilState.DOMContentLoaded });
                target = probePage;
            }

            var texts = new List<string>();
            if (!string.IsNullOrWhiteSpace(selector))
            {
                var loc = target.Locator(selector).First;
                if (await loc.CountAsync() > 0)
                {
                    texts.Add(await loc.InnerTextAsync(new LocatorInnerTextOptions { Timeout = timeoutMs }));
                    texts.Add(await loc.GetAttributeAsync("title") ?? "");
                    texts.Add(await loc.GetAttributeAsync("content") ?? "");
                }
            }
            else
            {
                texts.Add(await target.InnerTextAsync("body", new PageInnerTextOptions { Timeout = timeoutMs }));
                texts.Add(await target.TitleAsync());
                texts.Add(await target.EvaluateAsync<string>(
                    "() => [...document.querySelectorAll('meta[name*=version i], [data-version]')].map(e => (e.getAttribute('content') || e.getAttribute('data-version') || '')).join(' ')"));
            }

            foreach (var text in texts.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                var m = pattern.Match(text);
                if (m.Success)
                {
                    var version = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
                    var where = !string.IsNullOrWhiteSpace(path) ? $"page {path}" : "signed-in page";
                    return (version, configured ? $"configured probe ({where}{(string.IsNullOrWhiteSpace(selector) ? "" : $", {selector}")})" : $"text on the {where}");
                }
            }
            return (null, configured
                ? $"The configured version probe for {env.DisplayName} did not find a version."
                : $"No version text was found on the signed-in page of {env.DisplayName}; configure Metadata:{PathKey}/{SelectorKey} for this environment.");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or UriFormatException)
        {
            return (null, $"Reading the version failed: {ex.Message.Split('\n')[0]}");
        }
        finally
        {
            if (probePage is not null)
                await probePage.CloseAsync();
        }
    }
}
