using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Services.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AI.BugInvestigationTool.Pages;

public sealed class SettingsModel(InvestigationRunner runner, IOptions<ToolOptions> options) : PageModel
{
    public sealed class SettingsInput
    {
        public string? JiraBaseUrl { get; set; }
        public string? JiraEmail { get; set; }
        public string? JiraApiToken { get; set; }
        public string? AdoOrganization { get; set; }
        public string? AdoProject { get; set; }
        public string? AdoPat { get; set; }
        public string? ClaudeApiKey { get; set; }
        public string? EnvironmentUrl { get; set; }
        public string? EnvironmentUsername { get; set; }
        public string? EnvironmentPassword { get; set; }
        public string? AllowedHosts { get; set; }
    }

    [BindProperty] public SettingsInput Input { get; set; } = new();
    public Credentials Current { get; private set; } = new();
    public string Model => options.Value.ClaudeModel;
    public List<string> ConfiguredHosts => options.Value.AllowedEnvironmentHosts;

    public void OnGet()
    {
        Current = runner.GetCredentials();
        // Only non-secret values are echoed back to the form.
        Input = new SettingsInput
        {
            JiraBaseUrl = Current.JiraBaseUrl, JiraEmail = Current.JiraEmail,
            AdoOrganization = Current.AdoOrganization, AdoProject = Current.AdoProject,
            EnvironmentUrl = Current.EnvironmentUrl, EnvironmentUsername = Current.EnvironmentUsername,
            AllowedHosts = string.Join(", ", Current.AllowedEnvironmentHosts),
        };
    }

    public IActionResult OnPost()
    {
        var c = runner.GetCredentials();
        c.JiraBaseUrl = Trim(Input.JiraBaseUrl)?.TrimEnd('/');
        c.JiraEmail = Trim(Input.JiraEmail);
        c.AdoOrganization = Trim(Input.AdoOrganization);
        c.AdoProject = Trim(Input.AdoProject);
        c.EnvironmentUrl = Trim(Input.EnvironmentUrl);
        c.EnvironmentUsername = Trim(Input.EnvironmentUsername);
        c.AllowedEnvironmentHosts = (Input.AllowedHosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        // Blank secret = keep the existing value.
        if (!string.IsNullOrEmpty(Input.JiraApiToken)) c.JiraApiToken = Input.JiraApiToken.Trim();
        if (!string.IsNullOrEmpty(Input.AdoPat)) c.AdoPat = Input.AdoPat.Trim();
        if (!string.IsNullOrEmpty(Input.ClaudeApiKey)) c.ClaudeApiKey = Input.ClaudeApiKey.Trim();
        if (!string.IsNullOrEmpty(Input.EnvironmentPassword)) c.EnvironmentPassword = Input.EnvironmentPassword;
        runner.SetCredentials(c);
        TempData["Message"] = "Saved in memory.";
        return RedirectToPage();
    }

    public IActionResult OnPostClear()
    {
        runner.SetCredentials(new Credentials());
        TempData["Message"] = "All credentials cleared from memory.";
        return RedirectToPage();
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
