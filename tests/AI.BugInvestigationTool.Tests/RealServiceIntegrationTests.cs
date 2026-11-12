using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services;
using AI.BugInvestigationTool.Services.AzureDevOps;
using AI.BugInvestigationTool.Services.Claude;
using AI.BugInvestigationTool.Services.Jira;

namespace AI.BugInvestigationTool.Tests;

/// <summary>
/// Opt-in tests against real services. Skipped unless BIT_INTEGRATION=1 and the matching credentials are set
/// (same environment variables as the CLI). They only READ from Jira and Azure DevOps.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(params string[] requiredVariables)
    {
        if (Environment.GetEnvironmentVariable("BIT_INTEGRATION") != "1")
            Skip = "Integration mode is off (set BIT_INTEGRATION=1).";
        else if (requiredVariables.FirstOrDefault(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))) is { } missing)
            Skip = $"{missing} is not set.";
    }
}

public class RealServiceIntegrationTests
{
    private static string Env(string name) => Environment.GetEnvironmentVariable(name)!;

    [IntegrationFact("ANTHROPIC_API_KEY")]
    public async Task Claude_returns_a_validated_structured_result()
    {
        var submit = new ClaudeTool { Name = "submit_answer", Description = "Submit", Schema = ClaudeTool.Obj(("answer", ClaudeTool.Str("answer", 100), true)) };
        var options = new ToolOptions { ClaudeModel = Environment.GetEnvironmentVariable("BIT_CLAUDE_MODEL") ?? "claude-opus-5-5", ClaudeEffort = "low" };
        var client = new ClaudeClient(new HttpClient(), Env("ANTHROPIC_API_KEY"), options);
        var result = await client.RunAsync("Answer briefly.", [ClaudeClient.Text("What is 2+2? Call submit_answer.")], [], submit, null, CancellationToken.None);
        Assert.Contains("4", result.GetProperty("answer").GetString());
    }

    [IntegrationFact("BIT_JIRA_EMAIL", "BIT_JIRA_API_TOKEN", "BIT_TEST_JIRA_URL")]
    public async Task Jira_ticket_extraction_works_against_the_real_site()
    {
        var target = UrlParser.Parse(Env("BIT_TEST_JIRA_URL"));
        var client = new JiraClient(new HttpClient(), target.JiraBaseUrl!, Env("BIT_JIRA_EMAIL"), Env("BIT_JIRA_API_TOKEN"));
        var issue = await client.GetIssueAsync(target.JiraKey!, CancellationToken.None);
        Assert.Equal(target.JiraKey, issue.Key);
        Assert.False(string.IsNullOrEmpty(issue.Summary));
        Assert.NotEmpty(await client.GetTransitionsAsync(target.JiraKey!, CancellationToken.None));
    }

    [IntegrationFact("BIT_ADO_PAT", "BIT_TEST_PR_URL")]
    public async Task Azure_devops_repository_access_and_pr_loading_work()
    {
        var target = UrlParser.Parse(Env("BIT_TEST_PR_URL"));
        Assert.Equal(TargetKind.PullRequest, target.Kind);
        var ado = new AzureDevOpsClient(new HttpClient(), target.AdoOrganization!, target.AdoProject!, Env("BIT_ADO_PAT"));
        Assert.Contains(await ado.ListRepositoriesAsync(CancellationToken.None), r => r.Name.Equals(target.AdoRepository, StringComparison.OrdinalIgnoreCase));
        var pr = await ado.GetPullRequestAsync(target.AdoRepository!, target.PullRequestId, 20, 4000, CancellationToken.None);
        Assert.NotEmpty(pr.Files);
        Assert.False(string.IsNullOrEmpty(pr.SourceCommit));
    }
}
