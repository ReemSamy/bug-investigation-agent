using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Environments;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Evidence;

/// <summary>
/// Single place where investigation screenshots are taken: always masks password/secret fields (plus any
/// environment-configured sensitive selectors), files the image under the investigation folder, and stamps the
/// evidence item with ticket, investigation and environment.
/// </summary>
public static class EvidenceScreenshots
{
    private static readonly string[] AlwaysMasked =
    [
        "input[type=password]",
        "input[autocomplete*=password]",
        "input[name*=password i]",
        "input[name*=token i]",
        "input[name*=secret i]",
        "[data-sensitive]",
        "[data-mask]"
    ];

    public static string EvidenceDirectory(AgentOptions options, RuntimeContext ctx)
    {
        var root = Path.IsPathRooted(options.EvidenceDirectory)
            ? options.EvidenceDirectory
            : Path.Combine(Directory.GetCurrentDirectory(), options.EvidenceDirectory);
        var dir = Path.Combine(root, ctx.InvestigationId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>File name prefixed with ticket and environment so evidence is identifiable outside the app too.</summary>
    public static string FileName(RuntimeContext ctx, string name)
    {
        var parts = new[] { ctx.JiraTicketKey, ctx.SelectedEnvironmentId, name }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Regex.Replace(p!, @"[^A-Za-z0-9_\-]+", "-").Trim('-'));
        return string.Join("_", parts) + ".png";
    }

    public static IReadOnlyList<string> MaskSelectors(LoginFormOptions? login) =>
        AlwaysMasked.Concat(login?.MaskSelectors ?? new List<string>()).Distinct().ToList();

    public static async Task<EvidenceItem> CaptureAsync(
        IPage page,
        AgentOptions options,
        RuntimeContext ctx,
        string name,
        string caption,
        string category,
        LoginFormOptions? login = null,
        bool fullPage = true)
    {
        var path = Path.Combine(EvidenceDirectory(options, ctx), FileName(ctx, name));
        var masks = MaskSelectors(login).Select(s => page.Locator(s)).ToArray();
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            FullPage = fullPage,
            Mask = masks,
            MaskColor = "#1f1f1f"
        });

        var item = EvidenceItem.For(ctx, "screenshot", path, caption, category);
        ctx.Evidence.Add(item);
        return item;
    }
}
