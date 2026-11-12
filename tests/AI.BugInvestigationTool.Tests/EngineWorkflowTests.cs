using System.Net;
using System.Text.Json.Nodes;
using AI.BugInvestigationTool.Configuration;
using AI.BugInvestigationTool.Models;
using AI.BugInvestigationTool.Services.Browser;
using AI.BugInvestigationTool.Services.Workflow;

namespace AI.BugInvestigationTool.Tests;

public sealed class FakeBrowserSession : IBrowserSession
{
    private readonly List<EvidenceItem> _shots = [];
    public string EvidenceDir { get; set; } = Path.GetTempPath();
    public bool LoginResult { get; set; } = true;
    public List<string> Actions { get; } = [];
    public Action<string>? OnAction { get; set; }
    public IReadOnlyList<string> ConsoleErrors { get; } = ["TypeError: cannot read properties of null (reading 'id')"];
    public IReadOnlyList<string> NetworkFailures { get; } = ["POST https://qa.example.com/api/orders → HTTP 500"];
    public IReadOnlyList<EvidenceItem> Screenshots => _shots;
    public string CurrentUrl => "https://qa.example.com/app";

    private Task<string> Do(string a) { Actions.Add(a); OnAction?.Invoke(a); return Task.FromResult("OK: " + a); }
    public Task<string> NavigateAsync(string url, CancellationToken ct) => Do("navigate " + url);
    public Task<bool> LoginAsync(string u, string p, string? us, string? ps, string? ss, CancellationToken ct) { Actions.Add("login " + u); return Task.FromResult(LoginResult); }
    public Task<string> ClickAsync(string target, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Do("click " + target); }
    public Task<string> FillAsync(string target, string value, CancellationToken ct) => Do($"fill {target}");
    public Task<string> SelectAsync(string target, string value, CancellationToken ct) => Do($"select {target}");
    public Task<string> PressAsync(string key, CancellationToken ct) => Do("press " + key);
    public Task<string> ReadPageAsync(CancellationToken ct) => Task.FromResult("URL: https://qa.example.com/app\nbutton selector='#save' label='Save'");

    public async Task<EvidenceItem> ScreenshotAsync(string label, CancellationToken ct)
    {
        var dir = Path.Combine(EvidenceDir, "screenshots");
        Directory.CreateDirectory(dir);
        var file = $"{_shots.Count + 1:00}-shot.png";
        await File.WriteAllBytesAsync(Path.Combine(dir, file), [0x89, 0x50, 0x4E, 0x47], ct);
        var item = new EvidenceItem { Kind = "screenshot", Label = label, File = "screenshots/" + file };
        _shots.Add(item);
        return item;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeLauncher(FakeBrowserSession? session = null, Exception? failure = null) : IBrowserLauncher
{
    public int Launches { get; private set; }
    public BrowserLaunchOptions? LastOptions { get; private set; }

    public Task<IBrowserSession> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct)
    {
        Launches++;
        LastOptions = options;
        if (failure is not null) throw failure;
        session!.EvidenceDir = options.EvidenceDirectory;
        return Task.FromResult<IBrowserSession>(session);
    }
}

public class EngineWorkflowTests
{
    internal static FakeHttp JiraFake(FakeHttp? http = null)
    {
        http ??= new FakeHttp();
        return http
            .OnJson(HttpMethod.Get, "/rest/api/3/issue/SHOP-42?", TestEngine.Fixture("jira-issue.json"))
            .OnJson(HttpMethod.Get, "/rest/api/3/issue/SHOP-42/comment", """{"startAt":0,"total":0,"comments":[]}""")
            .OnJson(HttpMethod.Get, "/rest/api/3/issue/SHOP-42/changelog", """{"startAt":0,"isLast":true,"values":[]}""")
            .On(r => r.RequestUri!.AbsolutePath.Contains("/attachment/content/900"),
                (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("NullReferenceException at OrderService.Save line 5") });
    }

    internal static object Understanding(string envUrl = "") => new
    {
        summary = "Saving an order fails with a server error.",
        expected_behavior = "The order is saved and a confirmation is shown.",
        actual_behavior = "Error 'Could not save order'.",
        reproduction_steps = new[] { "Open the Orders page", "Click Save" },
        environment_url = envUrl,
        requires_login = true,
        code_search_terms = new[] { "SaveOrder", "OrderService" },
        missing_information = Array.Empty<object>(),
    };

    internal static object Repro(string reproduced = "yes") => new
    {
        reproduced,
        expected_behavior_observed = false,
        actual_behavior = "Clicking Save shows 'Could not save order'; POST /api/orders returned 500.",
        steps_executed = new[] { "Opened Orders page", "Clicked Save → error banner" },
    };

    internal static object Report(string outcome = "Unresolved") => new
    {
        outcome,
        summary = "The defect still occurs on QA.",
        expected_behavior = "Order saved",
        actual_behavior = "Server error",
        reproduction_result = "Reproduced on first attempt",
        root_cause = "OrderService.Save dereferences a null Customer.",
        root_cause_status = "confirmed",
        code_references = new[] { new { repository = "shop-api", path = "/src/Orders/OrderService.cs", start_line = 5, end_line = 5, note = "o.Customer.Id" } },
        recommendations = new[] { "Use o.Customer?.Id and validate before saving (OrderService.cs L5)" },
        confidence = 0.85,
        limitations = Array.Empty<string>(),
        proposed_jira_action = "Reopen",
    };

    [Fact]
    public async Task Waits_for_missing_information_then_resumes_through_every_step()
    {
        using var dir = new TempDir();
        var http = JiraFake()
            .OnJson(HttpMethod.Post, "almsearch.dev.azure.com", """{"results":[{"fileName":"OrderService.cs","path":"/src/Orders/OrderService.cs","repository":{"name":"shop-api"}}]}""")
            .OnJson(HttpMethod.Get, "/items", """{"content":"class OrderService\n{\n  void Save(Order o)\n  {\n    db.Save(o.Customer.Id);\n  }\n}"}""");
        var claude = new ClaudeScript()
            .ThenToolUse("submit_ticket_understanding", Understanding())
            // resume: browser loop
            .ThenToolUse("browser_click", new { target = "#save" })
            .ThenToolUse("browser_screenshot", new { label = "error banner" })
            .ThenToolUse("submit_reproduction", Repro())
            // code loop
            .ThenToolUse("search_code", new { query = "OrderService" })
            .ThenToolUse("get_file", new { repository = "shop-api", path = "/src/Orders/OrderService.cs" })
            .ThenToolUse("submit_code_findings", new { summary = "Save uses o.Customer.Id", suspected_root_cause = "null Customer", references = new[] { new { repository = "shop-api", path = "/src/Orders/OrderService.cs", start_line = 5, note = "deref" } } })
            // final analysis
            .ThenToolUse("submit_investigation_report", Report());
        claude.Register(http);
        var browser = new FakeBrowserSession();
        var launcher = new FakeLauncher(browser);
        var engine = TestEngine.Create(http, dir.Path, launcher);

        // 1st run: no environment URL and no environment login → must stop before the browser.
        var creds = TestEngine.Credentials(envUrl: null);
        creds.EnvironmentUsername = null;
        creds.EnvironmentPassword = null;
        var inv = await engine.InvestigateAsync("https://acme.atlassian.net/browse/SHOP-42", creds, null, CancellationToken.None);

        Assert.Equal(InvestigationStep.WaitingForInput, inv.Step);
        Assert.Contains(inv.MissingInfo, m => m.Key == "environmentUrl" && m.Required);
        Assert.Contains(inv.MissingInfo, m => m.Key == "environmentLogin" && m.Required);
        Assert.Equal(0, launcher.Launches);
        Assert.Single(inv.Evidence, e => e.Kind == "attachment"); // error.log downloaded, evil.txt (foreign host) not
        Assert.Contains("NullReferenceException", inv.Issue!.Attachments[0].Text);

        // 2nd run: the user supplies the URL and credentials.
        var full = TestEngine.Credentials(envUrl: null);
        inv = await engine.RunAsync(inv.Id, full, new Dictionary<string, string> { ["environmentUrl"] = "https://qa.example.com/app" }, CancellationToken.None);

        Assert.Equal(InvestigationStep.Completed, inv.Step);
        Assert.Null(inv.Error);
        Assert.Equal(1, launcher.Launches);
        Assert.Equal(["127.0.0.1", "qa.example.com"], launcher.LastOptions!.AllowedHosts);
        Assert.Equal(["login tester", "click #save"], browser.Actions);
        Assert.Equal("yes", inv.Browser!.Reproduced);
        Assert.True(inv.Browser.LoginSucceeded);
        Assert.Equal(InvestigationOutcome.Unresolved, inv.Report!.Outcome);
        Assert.Equal("Reopen", inv.Report.ProposedJiraAction);
        Assert.DoesNotContain("unverified", inv.Report.CodeReferences[0].Note); // file was opened via get_file
        Assert.True(inv.Evidence.Count(e => e.Kind == "screenshot") >= 3);
        Assert.NotNull(inv.JiraCommentDraft);

        var order = inv.Progress.Select(p => p.Step).Distinct().ToList();
        Assert.Equal([InvestigationStep.JiraDataCollection, InvestigationStep.ContextValidation, InvestigationStep.WaitingForInput, InvestigationStep.BrowserInvestigation,
            InvestigationStep.CodeInvestigation, InvestigationStep.ClaudeAnalysis, InvestigationStep.ReportGeneration, InvestigationStep.Completed], order);

        // Understanding was reused on resume (1 + 3 + 3 + 1 Claude calls).
        Assert.Equal(8, claude.Requests.Count);
        // Nothing was written to Jira without approval.
        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Post && c.Url.Host == "acme.atlassian.net");
        // No secret reached disk or Claude.
        var onDisk = string.Join("\n", Directory.GetFiles(engine.Store.FolderOf(inv.Id)).Where(f => f.EndsWith(".json") || f.EndsWith(".md")).Select(File.ReadAllText));
        var relaxed = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var toClaude = string.Join("\n", claude.Requests.Select(r => r.ToJsonString(relaxed)));
        foreach (var secret in full.SecretValues())
        {
            Assert.DoesNotContain(secret, onDisk);
            Assert.DoesNotContain(secret, toClaude);
        }
        // External content reached Claude wrapped as untrusted data.
        Assert.StartsWith("<untrusted_data source=\"jira-ticket\">", claude.Requests[0]["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Browser_failure_forces_blocked_even_if_claude_claims_otherwise()
    {
        using var dir = new TempDir();
        var http = JiraFake();
        new ClaudeScript()
            .ThenToolUse("submit_ticket_understanding", Understanding("https://qa.example.com/app"))
            .ThenToolUse("submit_investigation_report", Report("Resolved"))
            .Register(http);
        var engine = TestEngine.Create(http, dir.Path, new FakeLauncher(failure: new InvalidOperationException("Chromium could not be launched")));
        var creds = TestEngine.Credentials();
        creds.AdoPat = null; // code investigation skipped

        var inv = await engine.InvestigateAsync("https://acme.atlassian.net/browse/SHOP-42", creds, null, CancellationToken.None);

        Assert.Equal(InvestigationStep.Completed, inv.Step);
        Assert.Equal("Chromium could not be launched", inv.Browser!.Error);
        Assert.Equal(InvestigationOutcome.Blocked, inv.Report!.Outcome);
        Assert.Equal(InvestigationOutcome.Resolved, inv.Report.ProposedOutcome);
        Assert.Null(inv.Report.ProposedJiraAction);
        Assert.Contains(inv.Report.Limitations, l => l.Contains("Azure DevOps was not configured"));
        Assert.Equal("hypothesis", inv.Report.RootCauseStatus); // unverified code reference cannot be "confirmed"... reference kept but flagged
        Assert.Contains("[unverified", inv.Report.CodeReferences[0].Note);
    }

    [Fact]
    public async Task Failed_login_skips_reproduction_and_blocks()
    {
        using var dir = new TempDir();
        var http = JiraFake();
        var claude = new ClaudeScript()
            .ThenToolUse("submit_ticket_understanding", Understanding("https://qa.example.com/app"))
            .ThenToolUse("submit_investigation_report", Report("NotReproducible"));
        claude.Register(http);
        var browser = new FakeBrowserSession { LoginResult = false };
        var creds = TestEngine.Credentials();
        creds.AdoPat = null;

        var inv = await TestEngine.Create(http, dir.Path, new FakeLauncher(browser)).InvestigateAsync("https://acme.atlassian.net/browse/SHOP-42", creds, null, CancellationToken.None);

        Assert.False(inv.Browser!.LoginSucceeded);
        Assert.Equal(["login tester"], browser.Actions);
        Assert.Equal(InvestigationOutcome.Blocked, inv.Report!.Outcome);
        Assert.Equal(2, claude.Requests.Count); // no browser conversation
    }

    [Fact]
    public async Task Jira_errors_fail_the_investigation_without_leaking_secrets()
    {
        using var dir = new TempDir();
        var http = new FakeHttp().OnJson(HttpMethod.Get, "/rest/api/3/issue/", """{"errorMessages":["Unauthorized"]}""", HttpStatusCode.Unauthorized);
        var engine = TestEngine.Create(http, dir.Path, new FakeLauncher(new FakeBrowserSession()));
        var creds = TestEngine.Credentials();

        var inv = await engine.InvestigateAsync("https://acme.atlassian.net/browse/SHOP-42", creds, null, CancellationToken.None);

        Assert.Equal(InvestigationStep.Failed, inv.Step);
        Assert.Contains("HTTP 401", inv.Error);
        Assert.Contains("API token", inv.Error);
        var md = File.ReadAllText(Path.Combine(engine.Store.FolderOf(inv.Id), "report.md"));
        Assert.Contains("HTTP 401", md);
        Assert.DoesNotContain(creds.JiraApiToken!, md);
    }

    [Fact]
    public async Task Credentials_are_not_sent_to_unexpected_hosts()
    {
        using var dir = new TempDir();
        var http = new FakeHttp();
        var engine = TestEngine.Create(http, dir.Path, new FakeLauncher(new FakeBrowserSession()));

        var other = await engine.InvestigateAsync("https://evil.atlassian.net/browse/SHOP-42", TestEngine.Credentials(), null, CancellationToken.None);
        Assert.Equal(InvestigationStep.Failed, other.Step);
        Assert.Contains("credentials are for 'acme.atlassian.net'", other.Error);

        var creds = TestEngine.Credentials();
        creds.JiraBaseUrl = null;
        var notAllowed = await engine.InvestigateAsync("https://jira.attacker.net/browse/SHOP-42", creds, null, CancellationToken.None);
        Assert.Contains("not in Tool:AllowedJiraHosts", notAllowed.Error);

        var pr = await engine.InvestigateAsync("https://dev.azure.com/OtherOrg/Shop/_git/shop-api/pullrequest/77", TestEngine.Credentials(), null, CancellationToken.None);
        Assert.Contains("PAT is configured for 'Acme'", pr.Error);

        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_is_recorded()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var http = JiraFake();
        new ClaudeScript()
            .ThenToolUse("submit_ticket_understanding", Understanding("https://qa.example.com/app"))
            .ThenToolUse("browser_click", new { target = "#save" })
            .ThenToolUse("submit_reproduction", Repro())
            .Register(http);
        var browser = new FakeBrowserSession { OnAction = _ => cts.Cancel() };
        var engine = TestEngine.Create(http, dir.Path, new FakeLauncher(browser));

        var inv = await engine.InvestigateAsync("https://acme.atlassian.net/browse/SHOP-42", TestEngine.Credentials(), null, cts.Token);

        Assert.Equal(InvestigationStep.Cancelled, inv.Step);
        Assert.Null(inv.Report);
        var saved = await engine.Store.LoadAsync(inv.Id);
        Assert.Equal(InvestigationStep.Cancelled, saved!.Step);
    }

    [Fact]
    public async Task Claude_api_failure_fails_cleanly()
    {
        using var dir = new TempDir();
        var http = JiraFake().On(r => r.RequestUri!.Host == "api.anthropic.com",
            (_, _) => FakeHttp.Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized));
        var inv = await TestEngine.Create(http, dir.Path, new FakeLauncher(new FakeBrowserSession()))
            .InvestigateAsync("https://acme.atlassian.net/browse/SHOP-42", TestEngine.Credentials(), null, CancellationToken.None);
        Assert.Equal(InvestigationStep.Failed, inv.Step);
        Assert.Contains("Claude API key was rejected", inv.Error);
    }
}

public sealed class ApprovalTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private async Task<(Services.Workflow.InvestigationEngine Engine, Investigation Inv, FakeHttp Http)> CompletedAsync(string outcome, string reproduced)
    {
        var dir = _dir.Path;
        var http = EngineWorkflowTests.JiraFake();
        new ClaudeScript()
            .ThenToolUse("submit_ticket_understanding", EngineWorkflowTests.Understanding("https://qa.example.com/app"))
            .ThenToolUse("submit_reproduction", EngineWorkflowTests.Repro(reproduced))
            .ThenToolUse("submit_investigation_report", EngineWorkflowTests.Report(outcome))
            .Register(http);
        http.OnJson(HttpMethod.Get, "/transitions", """{"transitions":[{"id":"11","name":"Reopen","to":{"name":"Reopened"}},{"id":"31","name":"Close Issue","to":{"name":"Closed"}}]}""")
            .OnJson(HttpMethod.Post, "/transitions", "")
            .OnJson(HttpMethod.Post, "/comment", """{"id":"5555"}""")
            .OnJson(HttpMethod.Post, "/attachments", """[{"filename":"01-shot.png"}]""");
        var creds = TestEngine.Credentials();
        creds.AdoPat = null;
        var engine = TestEngine.Create(http, dir, new FakeLauncher(new FakeBrowserSession()));
        var inv = await engine.InvestigateAsync("https://acme.atlassian.net/browse/SHOP-42", creds, null, CancellationToken.None);
        return (engine, inv, http);
    }

    [Fact]
    public async Task Publishes_comment_attachments_and_transition_only_when_called()
    {
        var (engine, inv, http) = await CompletedAsync("Unresolved", "yes");
        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Post && c.Url.Host == "acme.atlassian.net");

        var edited = inv.JiraCommentDraft + "\n- Reviewed by QA";
        inv = await engine.PublishJiraAsync(inv.Id, TestEngine.Credentials(), new JiraPublishRequest(true, edited, true, "reopen"), CancellationToken.None);

        Assert.All(inv.Publishing, p => Assert.True(p.Success, p.Detail));
        var comment = http.Calls.Single(c => c.Method == HttpMethod.Post && c.Url.AbsolutePath.EndsWith("/comment"));
        var body = JsonNode.Parse(comment.Body)!["body"]!;
        Assert.Equal("doc", body["type"]!.GetValue<string>());
        Assert.Contains("Reviewed by QA", comment.Body);
        var attach = http.Calls.Single(c => c.Url.AbsolutePath.EndsWith("/attachments"));
        Assert.Equal("no-check", attach.Headers["X-Atlassian-Token"]);
        var transition = http.Calls.Single(c => c.Method == HttpMethod.Post && c.Url.AbsolutePath.EndsWith("/transitions"));
        Assert.Contains("\"id\":\"11\"", transition.Body);
    }

    [Fact]
    public async Task Refuses_to_close_inconclusive_issues_and_unknown_transitions()
    {
        var (engine, inv, http) = await CompletedAsync("Inconclusive", "unclear");
        Assert.Equal(InvestigationOutcome.Inconclusive, inv.Report!.Outcome);

        inv = await engine.PublishJiraAsync(inv.Id, TestEngine.Credentials(), new JiraPublishRequest(false, null, false, "Close Issue"), CancellationToken.None);
        var close = inv.Publishing.Single();
        Assert.False(close.Success);
        Assert.Contains("Refusing to close", close.Detail);

        inv = await engine.PublishJiraAsync(inv.Id, TestEngine.Credentials(), new JiraPublishRequest(false, null, false, "Teleport"), CancellationToken.None);
        Assert.Contains("not available", inv.Publishing[^1].Detail);
        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Post && c.Url.AbsolutePath.EndsWith("/transitions"));
    }
}

public class PullRequestWorkflowTests
{
    [Fact]
    public async Task Reviews_pr_validates_references_and_posts_only_on_approval()
    {
        using var dir = new TempDir();
        var http = AzureDevOpsClientTests.PullRequestFake();
        http.OnJson(HttpMethod.Post, "/pullrequests/77/threads", """{"id":901}""");
        var claude = new ClaudeScript().ThenToolUse("submit_pr_review", new
        {
            summary = "Guards against a null customer when saving orders.",
            findings = new object[]
            {
                new { title = "Nullable Value without check", severity = "Critical", category = "null-safety", file = "src/Orders/OrderService.cs", start_line = 6, end_line = 6,
                      detail = "id.Value throws when Customer is null — the original bug moves one line down.", recommendation = "Return a validation error when Customer is null." },
                new { title = "Unrelated", severity = "Low", category = "maintainability", file = "/src/Other.cs", start_line = 200, end_line = 210, detail = "d", recommendation = "r" },
            },
            missing_tests = new[] { "OrderService.Save with null Customer returns a validation error" },
            recommendations = Array.Empty<string>(),
            overall_assessment = "Not ready: the null case still throws.",
            confidence = 0.8,
        });
        claude.Register(http);
        var engine = TestEngine.Create(http, dir.Path, new FakeLauncher(new FakeBrowserSession()));
        var creds = TestEngine.Credentials();

        var inv = await engine.InvestigateAsync("https://dev.azure.com/Acme/Shop/_git/shop-api/pullrequest/77", creds, null, CancellationToken.None);

        Assert.Equal(InvestigationStep.Completed, inv.Step);
        Assert.Equal("Waiting for author", inv.Report!.PrVerdict);
        Assert.Equal("Critical", inv.Report.Findings[0].Severity);
        Assert.False(inv.Report.Findings[0].ReferenceUnverified);   // line 6 is in the diff
        Assert.True(inv.Report.Findings[1].ReferenceUnverified);    // file not in the PR, never opened
        Assert.False(inv.Tests!.Executed);
        Assert.Contains("--- a/src/Orders/OrderService.cs", claude.Requests[0].ToJsonString());
        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Post && c.Url.Host == "dev.azure.com");

        inv = await engine.PublishPullRequestCommentAsync(inv.Id, creds, null, CancellationToken.None);
        Assert.True(inv.Publishing[^1].Success);
        var post = http.Calls.Single(c => c.Method == HttpMethod.Post && c.Url.AbsolutePath.EndsWith("/threads"));
        Assert.Contains("Waiting for author", post.Body);
        Assert.DoesNotContain(http.Calls, c => c.Url.AbsolutePath.Contains("/reviewers") || c.Method == HttpMethod.Patch); // never votes or completes
    }
}
