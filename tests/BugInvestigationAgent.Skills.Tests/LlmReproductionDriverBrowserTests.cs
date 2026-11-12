using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BugInvestigationAgent.Core.Abstractions;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Skills.Reproduction;
using Microsoft.Playwright;

namespace BugInvestigationAgent.Skills.Tests;

/// <summary>
/// Real Chromium + a fake KMS portal that has VKMS-4183's bug (plain Save on a Proposal shows "sent for approval").
/// A scripted stand-in for the LLM decides actions from the page snapshot it receives, exactly as a real model would.
/// </summary>
public class LlmReproductionDriverBrowserTests
{
    private const string Site = "https://kms.example.test";
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string _evidence = "";

    private static string Page(string body) => $"<html><head><title>VisiumKMS</title></head><body><nav><a href='/'>Home</a> <a href='/Auditor'>Auditor</a></nav>{body}</body></html>";

    private static string Route(string path) => path switch
    {
        "/Auditor" => Page("<table><tr><td><a href='/Auditor/Proposal/Index/AUD-21-55'>AUD-21-55</a></td><td>Draft</td></tr></table>"),
        "/Auditor/Proposal/Index/AUD-21-55" => Page("""
            <h2>Proposal AUD-21-55</h2><div>Workflow: Plan (current stage)</div>
            <label>Department <select id='dep'><option>—</option><option>Quality</option></select></label>
            <aside><button id='save' onclick="document.getElementById('msg').style.display='block'">Save</button>
                   <button>Send for Approval</button></aside>
            <div id='msg' role='alert' style='display:none'>Audit has been sent for approval.</div>
            """),
        _ => Page("<h1>Home</h1><p>My Assignments</p>")
    };

    /// <summary>Plays the model: reads the numbered elements in the snapshot and returns one JSON action.</summary>
    private sealed class ScriptedLlm(Func<string, string, string> decide) : IAgentRuntime
    {
        public int Calls;
        public readonly List<string> Prompts = new();
        public string ProviderId => "anthropic";

        public Task<AgentTurnResult> CompleteAsync(AgentTurnRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var prompt = request.Messages.Single().Content;
            Prompts.Add(prompt);
            var json = prompt[(prompt.IndexOf("Current page (JSON):", StringComparison.Ordinal) + 20)..].Trim();
            using var doc = JsonDocument.Parse(json);
            var url = doc.RootElement.GetProperty("url").GetString()!;
            var items = string.Join("\n", doc.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetString()));
            var notices = string.Join(" ", doc.RootElement.GetProperty("notices").EnumerateArray().Select(i => i.GetString()));
            return Task.FromResult(new AgentTurnResult { Content = decide(url, items + "\nNOTICES: " + notices) });
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }

    private static int Idx(string items, string name) =>
        int.Parse(Regex.Match(items, $@"\[(\d+)\][^\n]*""{Regex.Escape(name)}""").Groups[1].Value);

    /// <summary>A competent model following VKMS-4183's steps.</summary>
    private static string FollowTicket(string url, string page)
    {
        if (page.Contains("sent for approval"))
            return """{"action":"finish","result":"reproduced","observation":"After plain Save the message says 'Audit has been sent for approval' while the record is still at Plan."}""";
        return url switch
        {
            "/" => $$"""{"thought":"open Auditor","action":"click","element":{{Idx(page, "Auditor")}}}""",
            "/Auditor" => """{"thought":"open the audit from the ticket","action":"navigate","url":"/Auditor/Proposal/Index/AUD-21-55"}""",
            _ when !Selected => SelectDepartment(page),
            _ => $$"""{"thought":"plain Save (not Send for Approval)","action":"click","element":{{Idx(page, "Save")}}}"""
        };
    }

    private static bool Selected;

    private static string SelectDepartment(string page)
    {
        Selected = true;
        var select = Regex.Match(page, @"\[(\d+)\] select").Groups[1].Value;
        return $$"""{"thought":"make a field edit","action":"select","element":{{select}},"value":"Quality"}""";
    }

    [OneTimeSetUp]
    public async Task Launch()
    {
        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex)
        {
            Assert.Ignore("Playwright Chromium not available: " + ex.Message.Split('\n')[0]);
        }
    }

    [OneTimeTearDown]
    public async Task Close()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        Selected = false;
        _evidence = Path.Combine(Path.GetTempPath(), "bia-llm-repro-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_evidence)) Directory.Delete(_evidence, true);
    }

    private async Task<(IPage Page, IBrowserContext Context)> OpenAsync()
    {
        var context = await _browser!.NewContextAsync();
        await context.RouteAsync(Site + "/**", r => r.FulfillAsync(new RouteFulfillOptions
        { ContentType = "text/html", Body = Route(new Uri(r.Request.Url).AbsolutePath) }));
        var page = await context.NewPageAsync();
        await page.GotoAsync(Site + "/");
        return (page, context);
    }

    private static RuntimeContext Ticket(bool production = false) => new()
    {
        JiraTicketKey = "VKMS-4183",
        Summary = "Auditor Proposal: plain Save shows misleading \"sent for approval\" confirmation message",
        Description = "Steps to reproduce:\n1. Log in as Master Admin\n2. Open an existing Proposal-stage audit (e.g. AUD-21-55) at /Auditor/Proposal/Index/AUD-21-55\n3. Make any field edit\n4. Click Save (NOT Send for Approval)\n5. Observe the success message",
        EnvironmentUrl = Site,
        IsReadOnlyEnvironment = production,
        SelectedEnvironmentType = production ? EnvironmentType.Production : EnvironmentType.ReleaseCandidate
    };

    private static AppEnvironment Env(bool allowTestData) => new()
    {
        Id = "KMS14REL", DisplayName = "kms14-portal", BaseUrl = Site, Type = EnvironmentType.ReleaseCandidate, Enabled = true, Login = new LoginFormOptions(),
        Metadata = allowTestData ? new Dictionary<string, string> { [LlmReproductionDriver.AllowTestDataKey] = "true" } : new Dictionary<string, string>()
    };

    private LlmReproductionDriver Driver(IAgentRuntime llm) =>
        new(llm, new AgentOptions { EvidenceDirectory = _evidence }, new SecretRedactor());

    private static IReadOnlyList<string> Steps(RuntimeContext ctx) => BugInvestigationAgent.Skills.Jira.JiraSkill.ExtractSteps(ctx.Description);

    [Test]
    public async Task Follows_the_ticket_steps_and_reproduces_the_bug_when_saving_test_data_is_allowed()
    {
        var (page, context) = await OpenAsync();
        var llm = new ScriptedLlm(FollowTicket);
        var ctx = Ticket();

        var outcome = await Driver(llm).RunAsync(page, ctx, Env(allowTestData: true), Steps(ctx), null, null, CancellationToken.None);

        Assert.That(outcome.Result, Is.EqualTo(ReproductionOutcome.Reproduced), outcome.Conclusion);
        Assert.That(outcome.Conclusion, Does.Contain("sent for approval"));
        Assert.That(page.Url, Is.EqualTo(Site + "/Auditor/Proposal/Index/AUD-21-55"));
        Assert.That(ctx.Evidence.Count(e => e.Kind == "screenshot"), Is.GreaterThanOrEqualTo(llm.Calls - 1), "every action screenshotted");
        Assert.That(llm.Prompts[0], Does.Contain("Steps to reproduce:").And.Contain("AUD-21-55").And.Not.Contain("password"));
        await context.CloseAsync();
    }

    [Test]
    public async Task Stops_before_save_when_test_data_changes_are_not_allowed()
    {
        var (page, context) = await OpenAsync();
        var ctx = Ticket();

        var outcome = await Driver(new ScriptedLlm(FollowTicket)).RunAsync(page, ctx, Env(allowTestData: false), Steps(ctx), null, null, CancellationToken.None);

        Assert.That(outcome.Result, Is.EqualTo(ReproductionOutcome.Ambiguous));
        Assert.That(outcome.NeedsPerson, Is.True);
        Assert.That(outcome.Conclusion, Does.Contain("changes data"));
        Assert.That(await page.IsVisibleAsync("#msg"), Is.False, "Save was never clicked");
        await context.CloseAsync();
    }

    [Test]
    public async Task Production_never_saves_even_if_the_environment_says_it_may()
    {
        var (page, context) = await OpenAsync();
        var ctx = Ticket(production: true);

        var outcome = await Driver(new ScriptedLlm(FollowTicket)).RunAsync(page, ctx, Env(allowTestData: true), Steps(ctx), null, null, CancellationToken.None);

        Assert.That(outcome.NeedsPerson, Is.True);
        Assert.That(await page.IsVisibleAsync("#msg"), Is.False);
        await context.CloseAsync();
    }

    [Test]
    public async Task Navigation_outside_the_site_is_refused_and_bad_answers_end_cleanly()
    {
        var (page, context) = await OpenAsync();
        var turn = 0;
        var llm = new ScriptedLlm((_, _) => ++turn switch
        {
            1 => """{"action":"navigate","url":"https://evil.example/steal"}""",
            2 => """{"action":"finish","result":"insufficient_info","observation":"The ticket does not say which audit to open."}""",
            _ => "not json"
        });
        var ctx = Ticket();

        var outcome = await Driver(llm).RunAsync(page, ctx, Env(true), Steps(ctx), null, null, CancellationToken.None);

        Assert.That(page.Url, Does.StartWith(Site), "stayed on the environment's site");
        Assert.That(llm.Prompts[1], Does.Contain("REFUSED: navigation outside"));
        Assert.That(outcome.InsufficientInformation, Is.True);
        Assert.That(outcome.Conclusion, Does.StartWith("Not enough information"));

        var (page2, context2) = await OpenAsync();
        var garbage = await Driver(new ScriptedLlm((_, _) => "sorry, I cannot")).RunAsync(page2, Ticket(), Env(true), Steps(ctx), null, null, CancellationToken.None);
        Assert.That(garbage.Result, Is.EqualTo(ReproductionOutcome.Ambiguous));
        Assert.That(garbage.Conclusion, Does.Contain("usable action"));
        await context.CloseAsync();
        await context2.CloseAsync();
    }
}
