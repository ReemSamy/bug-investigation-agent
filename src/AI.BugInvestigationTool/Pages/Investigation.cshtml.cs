using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services.Workflow;
using AI.BugInvestigationTool.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AI.BugInvestigationTool.Pages;

public sealed class InvestigationModel(InvestigationEngine engine, InvestigationStore store, InvestigationRunner runner) : PageModel
{
    public Investigation? Inv { get; private set; }
    public bool Running { get; private set; }
    public List<JiraTransition> Transitions { get; private set; } = [];
    public string? TransitionError { get; private set; }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (Inv!.Report is not null && !Running && Inv.Target.Kind == TargetKind.JiraIssue)
        {
            try { Transitions = await engine.GetJiraTransitionsAsync(id, runner.GetCredentials(), HttpContext.RequestAborted); }
            catch (Exception ex) when (ex is not OperationCanceledException) { TransitionError = ex.Message; }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostProvideInfoAsync(string id, Dictionary<string, string> info, string? envUsername, string? envPassword, string? allowHost)
    {
        if (!await LoadAsync(id)) return NotFound();
        var creds = runner.GetCredentials();
        if (!string.IsNullOrWhiteSpace(envUsername)) creds.EnvironmentUsername = envUsername.Trim();
        if (!string.IsNullOrEmpty(envPassword)) creds.EnvironmentPassword = envPassword; // memory only
        if (!string.IsNullOrWhiteSpace(allowHost) && !creds.AllowedEnvironmentHosts.Contains(allowHost.Trim(), StringComparer.OrdinalIgnoreCase))
            creds.AllowedEnvironmentHosts.Add(allowHost.Trim());
        runner.SetCredentials(creds);
        runner.Start(id, info.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value));
        return RedirectToPage(new { id });
    }

    public IActionResult OnPostRerun(string id)
    {
        runner.Start(id, null);
        return RedirectToPage(new { id });
    }

    public IActionResult OnPostCancel(string id)
    {
        runner.Cancel(id);
        TempData["Message"] = "Cancellation requested.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPublishJiraAsync(string id, bool postComment, string? commentText, bool attachScreenshots, string? transition, bool confirm)
    {
        if (!confirm)
        {
            TempData["Message"] = "Nothing was published: tick the approval checkbox to confirm.";
            return RedirectToPage(new { id });
        }
        if (!postComment && string.IsNullOrWhiteSpace(transition))
        {
            TempData["Message"] = "Nothing selected to publish.";
            return RedirectToPage(new { id });
        }
        try
        {
            var inv = await engine.PublishJiraAsync(id, runner.GetCredentials(),
                new JiraPublishRequest(postComment, commentText, postComment && attachScreenshots, string.IsNullOrWhiteSpace(transition) ? null : transition), HttpContext.RequestAborted);
            TempData["Message"] = string.Join(" · ", inv.Publishing.TakeLast(3).Select(p => $"{p.Action}: {(p.Success ? "OK" : "FAILED")} {p.Detail}"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            TempData["Message"] = ex.Message;
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPublishPrAsync(string id, string? commentText, bool confirm)
    {
        if (!confirm)
        {
            TempData["Message"] = "Nothing was posted: tick the approval checkbox to confirm.";
            return RedirectToPage(new { id });
        }
        try
        {
            var inv = await engine.PublishPullRequestCommentAsync(id, runner.GetCredentials(), commentText, HttpContext.RequestAborted);
            var p = inv.Publishing[^1];
            TempData["Message"] = $"{p.Action}: {(p.Success ? "OK" : "FAILED")} {p.Detail}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            TempData["Message"] = ex.Message;
        }
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(string id)
    {
        try { Inv = await store.LoadAsync(id, HttpContext.RequestAborted); }
        catch (ArgumentException) { return false; }
        Running = runner.IsRunning(id);
        return Inv is not null;
    }

    public static string OutcomeColor(InvestigationOutcome o) => o switch
    {
        InvestigationOutcome.Resolved => "success",
        InvestigationOutcome.Unresolved => "danger",
        InvestigationOutcome.NotReproducible => "info",
        InvestigationOutcome.Blocked => "dark",
        _ => "warning",
    };
}
