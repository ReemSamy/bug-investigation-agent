using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services;
using AI.BugInvestigationTool.Services.Workflow;
using AI.BugInvestigationTool.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AI.BugInvestigationTool.Pages;

public sealed class IndexModel(InvestigationEngine engine, InvestigationStore store, InvestigationRunner runner) : PageModel
{
    [BindProperty] public new string? Url { get; set; }
    public List<Investigation> Recent { get; private set; } = [];
    public bool CredentialsReady { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!UrlParser.TryParse(Url, out _, out var error))
        {
            ModelState.AddModelError(nameof(Url), error);
            await LoadAsync();
            return Page();
        }
        var inv = await engine.CreateAsync(Url!, HttpContext.RequestAborted);
        runner.Start(inv.Id, null);
        return RedirectToPage("/Investigation", new { id = inv.Id });
    }

    private async Task LoadAsync()
    {
        Recent = await store.ListRecentAsync(25, HttpContext.RequestAborted);
        CredentialsReady = runner.GetCredentials().HasClaude;
    }

    public static string StepColor(InvestigationStep s) => s switch
    {
        InvestigationStep.Completed => "success",
        InvestigationStep.Failed => "danger",
        InvestigationStep.WaitingForInput => "warning",
        InvestigationStep.Cancelled => "secondary",
        _ => "primary",
    };
}
