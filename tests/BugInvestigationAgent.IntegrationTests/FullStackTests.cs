using BugInvestigationAgent.TestKit;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BugInvestigationAgent.IntegrationTests;

/// <summary>
/// Full stack, locally:
///   REAL  — API host (HTTP pipeline, FIT validation, A2A, credential channel), worker host (queue, runner,
///           orchestrator, real skills incl. Jira skill + Playwright sign-in + reproduction + evidence + report),
///           PostgreSQL 16, Chromium, the login portal over HTTP.
///   SIMULATED — Jira Cloud REST API (HTTP handler; real site not authorized), LLM (none configured), Azure DevOps
///           (no repository mapping → code investigation reported as unavailable).
/// Two developers, two issues, two environments, different credentials, MFA for one, a rejected password + retry
/// for the other, ONE worker slot (proves waiting releases the slot), concurrent execution.
/// </summary>
[NonParallelizable]
public class FullStackTests
{
    private const string AliceToken = "tok-alice-it", BobToken = "tok-bob-it";
    private const string AlicePassword = "qa-secret-A1!", BobPassword = "rc-secret-B2!", BobWrong = "rc-WRONG-pass";

    private string _cs = null!;
    private string _evidence = null!;
    private TestPortal _portal = null!;
    private SimulatedJira _jira = null!;
    private SharedSetup _setup = null!;
    private ApiHost _api = null!;
    private WorkerHost _worker = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _cs = await Postgres.CreateDatabaseAsync();
        _evidence = Path.Combine(Path.GetTempPath(), "bia-it-" + Guid.NewGuid().ToString("N"));
        _portal = await TestPortal.StartAsync();
        _jira = new SimulatedJira();
        _jira.Grant(AliceToken, "acc-alice", "ABC-1");
        _jira.Grant(BobToken, "acc-bob", "ABC-2");
        _setup = new SharedSetup { ConnectionString = _cs, EvidenceRoot = _evidence, Portal = _portal, Fit = new Fit(), Jira = _jira };
        _api = new ApiHost(_setup);
        _worker = new WorkerHost(_setup);
        _client = _api.CreateClient();
        _ = _worker.Services; // starts the worker host (queue consumer, recovery sweeper, migrator)
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.Dispose();
        await _worker.DisposeAsync();
        await _api.DisposeAsync();
        await _portal.DisposeAsync();
        _jira.Dispose();
        await Postgres.DropDatabaseAsync(_cs);
        try { Directory.Delete(_evidence, true); } catch { }
    }

    private async Task<JsonObject> Rpc(string method, object @params, string principal, string userToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/a2a/json-rpc") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString(), method, @params }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _setup.Fit.Mint(principal));
        req.Headers.Add("x-forge-oauth-user", userToken);
        req.Headers.Add("x-forge-oauth-system", SimulatedJira.SystemToken);
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
    }

    private static object Invoke(string issueKey, string account) => new
    {
        message = new
        {
            role = "ROLE_USER", messageId = Guid.NewGuid().ToString(),
            parts = new object[]
            {
                new { text = "Investigate this bug" },
                new { data = new { userAccountId = account, invocationType = "ISSUE_ASSIGNMENT", issue = new { id = "100" + issueKey.Split('-')[1], fields = new { key = issueKey } } } }
            }
        }
    };

    private static object Reply(string contextId, string text) => new
    {
        message = new { role = "ROLE_USER", messageId = Guid.NewGuid().ToString(), contextId, parts = new object[] { new { text } } }
    };

    private static string State(JsonObject r) => r["result"]!["task"]!["status"]!["state"]!.GetValue<string>();
    private static string Text(JsonObject r) => r["result"]!["task"]!["status"]!["message"]!["parts"]![0]!["text"]!.GetValue<string>();

    private async Task<JsonObject> PollUntil(string taskId, string principal, string token, Func<JsonObject, bool> condition, string what, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonObject last = new();
        while (DateTime.UtcNow < deadline)
        {
            last = await Rpc("GetTask", new { id = taskId }, principal, token);
            if (last["result"] is not null && condition(last))
                return last;
            await Task.Delay(250);
        }
        Assert.Fail($"Timed out waiting for {what}. Last: {last.ToJsonString()}");
        return last;
    }

    private async Task<(Guid JobId, string Ticket, string Kind)> Pending(string principal)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/forge/credentials/pending") { Content = JsonContent.Create(new { }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _setup.Fit.Mint(principal));
        var body = JsonNode.Parse(await (await _client.SendAsync(req)).Content.ReadAsStringAsync())!;
        var item = body["items"]!.AsArray().Single()!;
        return (Guid.Parse(item["jobId"]!.GetValue<string>()), item["ticket"]!.GetValue<string>(), item["kind"]!.GetValue<string>());
    }

    private async Task<HttpStatusCode> PostSecret(Guid jobId, string ticket, string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/{path}") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Submission-Ticket", ticket);
        return (await _client.SendAsync(req)).StatusCode;
    }

    [Test]
    public async Task Two_developers_full_jira_workflow_with_real_api_worker_postgres_and_browser()
    {
        // 0) Forge "installed" event registers the site.
        using (var install = new HttpRequestMessage(HttpMethod.Post, "/atlassian/installed") { Content = JsonContent.Create(new { context = "ari:cloud:jira::site/" + Fit.CloudId }) })
        {
            install.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _setup.Fit.Mint(null));
            Assert.That((await _client.SendAsync(install)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        // 1) Both developers invoke the agent from their own issue.
        var a = await Rpc("SendMessage", Invoke("ABC-1", "acc-alice"), "acc-alice", AliceToken);
        var b = await Rpc("SendMessage", Invoke("ABC-2", "acc-bob"), "acc-bob", BobToken);
        var aTask = a["result"]!["task"]!["id"]!.GetValue<string>();
        var bTask = b["result"]!["task"]!["id"]!.GetValue<string>();
        var aCtx = a["result"]!["task"]!["contextId"]!.GetValue<string>();
        var bCtx = b["result"]!["task"]!["contextId"]!.GetValue<string>();

        // Bob cannot see Alice's task (verified identity, not task id).
        var cross = await Rpc("GetTask", new { id = aTask }, "acc-bob", BobToken);
        Assert.That(cross["error"]!["code"]!.GetValue<int>(), Is.EqualTo(-32001));

        // 2) With ONE worker slot, both reach the environment question only if waiting releases the slot.
        await PollUntil(aTask, "acc-alice", AliceToken, r => State(r) == "TASK_STATE_INPUT_REQUIRED" && Text(r).Contains("KMSQA"), "Alice env prompt");
        await PollUntil(bTask, "acc-bob", BobToken, r => State(r) == "TASK_STATE_INPUT_REQUIRED" && Text(r).Contains("KMSRC"), "Bob env prompt");

        // 3) Different environments.
        await Rpc("SendMessage", Reply(aCtx, "KMSQA"), "acc-alice", AliceToken);
        await Rpc("SendMessage", Reply(bCtx, "KMSRC"), "acc-bob", BobToken);
        await PollUntil(aTask, "acc-alice", AliceToken, r => State(r) == "TASK_STATE_INPUT_REQUIRED" && Text(r).Contains("personal settings"), "Alice credential prompt");
        await PollUntil(bTask, "acc-bob", BobToken, r => State(r) == "TASK_STATE_INPUT_REQUIRED" && Text(r).Contains("personal settings"), "Bob credential prompt");

        // 4) Credentials via the secure channel (FIT-verified pending list → one-time ticket → direct POST).
        var (aJob, aTicket, aKind) = await Pending("acc-alice");
        Assert.That(aKind, Is.EqualTo(AwaitingInputKinds.Credentials));
        Assert.That(await PostSecret(aJob, aTicket, "credentials", new { environmentId = "KMSQA", username = "alice_qa", password = AlicePassword }), Is.EqualTo(HttpStatusCode.OK));
        var (bJob, bTicket, _) = await Pending("acc-bob");
        Assert.That(await PostSecret(bJob, bTicket, "credentials", new { environmentId = "KMSRC", username = "bob_rc", password = BobWrong }), Is.EqualTo(HttpStatusCode.OK));

        // 5) Alice: real MFA page → code via the secure channel → the worker types it into the real browser.
        await PollUntil(aTask, "acc-alice", AliceToken, r => State(r) == "TASK_STATE_INPUT_REQUIRED" && Text(r).Contains("verification"), "Alice MFA prompt");
        var (aJob2, aTicket2, aKind2) = await Pending("acc-alice");
        Assert.That(aKind2, Is.EqualTo(AwaitingInputKinds.MfaCode));
        Assert.That(await PostSecret(aJob2, aTicket2, "mfa-code", new { code = TestPortal.MfaCode }), Is.EqualTo(HttpStatusCode.OK));

        // 6) Bob: real login rejected → asked again → correct password.
        await PollUntil(bTask, "acc-bob", BobToken, r => State(r) == "TASK_STATE_INPUT_REQUIRED" && Text(r).Contains("rejected"), "Bob rejected prompt", 150);
        var (bJob2, bTicket2, _) = await Pending("acc-bob");
        Assert.That(await PostSecret(bJob2, bTicket2, "credentials", new { environmentId = "KMSRC", username = "bob_rc", password = BobPassword }), Is.EqualTo(HttpStatusCode.OK));
        await Rpc("SendMessage", Reply(bCtx, "continue"), "acc-bob", BobToken);

        // 7) Both complete; reports published to their own issues.
        var aDone = await PollUntil(aTask, "acc-alice", AliceToken, r => State(r) is "TASK_STATE_COMPLETED" or "TASK_STATE_FAILED", "Alice done", 180);
        var bDone = await PollUntil(bTask, "acc-bob", BobToken, r => State(r) is "TASK_STATE_COMPLETED" or "TASK_STATE_FAILED", "Bob done", 180);
        Assert.That(State(aDone), Is.EqualTo("TASK_STATE_COMPLETED"), Text(aDone));
        Assert.That(State(bDone), Is.EqualTo("TASK_STATE_COMPLETED"), Text(bDone));
        Assert.That(Text(aDone), Does.Contain("KMSQA").And.Contain("Confirmed findings"));
        Assert.That(Text(bDone), Does.Contain("KMSRC"));

        var jobs = _api.Services.GetRequiredService<IInvestigationJobStore>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && ((await jobs.GetAsync(aJob))!.ReportPublishedAt is null || (await jobs.GetAsync(bJob))!.ReportPublishedAt is null))
            await Task.Delay(250);

        TestContext.Out.WriteLine("ALICE TASK RESULT: " + Text(aDone));
        TestContext.Out.WriteLine("BOB TASK RESULT (first 600 chars): " + Text(bDone)[..Math.Min(600, Text(bDone).Length)]);
        TestContext.Out.WriteLine("PORTAL HITS: " + string.Join(" | ", _portal.Hits.Select(h => $"{h.Method} {h.Path} [{h.User ?? "-"}]")));

        // ── Server-side isolation evidence (the portal's own request log) ──
        var hits = _portal.Hits.ToList();
        Assert.That(hits.Where(h => h.User == "alice_qa").All(h => h.Env == "qa"), Is.True);
        Assert.That(hits.Where(h => h.User == "bob_rc").All(h => h.Env == "rc"), Is.True);
        var aliceSessions = hits.Where(h => h.User == "alice_qa").Select(h => h.Session).Distinct().ToList();
        var bobSessions = hits.Where(h => h.User == "bob_rc").Select(h => h.Session).Distinct().ToList();
        Assert.That(aliceSessions, Is.Not.Empty);
        Assert.That(bobSessions, Is.Not.Empty);
        Assert.That(aliceSessions.Intersect(bobSessions), Is.Empty, "no shared browser session/cookies");
        Assert.That(hits.Any(h => h is { Env: "qa", Method: "POST", Path: "/qa/mfa" }), Is.True, "MFA code was submitted in the real browser");

        // ── Evidence per investigation, named by ticket + environment ──
        var aShots = Directory.GetFiles(Path.Combine(_evidence, aJob.ToString("N")), "*.png").Select(Path.GetFileName).ToList();
        var bShots = Directory.GetFiles(Path.Combine(_evidence, bJob.ToString("N")), "*.png").Select(Path.GetFileName).ToList();
        Assert.That(aShots, Has.Some.StartsWith("ABC-1_KMSQA_02-after-login"));
        Assert.That(bShots, Has.Some.StartsWith("ABC-2_KMSRC_02-after-login"));
        Assert.That(aShots.All(f => f!.StartsWith("ABC-1_KMSQA")), Is.True);
        Assert.That(bShots.All(f => f!.StartsWith("ABC-2_KMSRC")), Is.True);

        // ── Jira: reads as each user; one report per issue, posted as the app ──
        var reqs = _jira.Requests.ToList();
        Assert.That(reqs, Has.Some.Contains("GET /ex/jira/it-cloud/rest/api/3/issue/ABC-1 as acc-alice"));
        Assert.That(reqs, Has.Some.Contains("GET /ex/jira/it-cloud/rest/api/3/issue/ABC-2 as acc-bob"));
        Assert.That(reqs.Where(r => r.Contains("/issue/ABC-1") || r.Contains("/issue/1001")).All(r => !r.EndsWith("as acc-bob")), Is.True);
        Assert.That(reqs.Where(r => r.StartsWith("POST") && r.Contains("/comment")).All(r => r.EndsWith("as system")), Is.True);
        var comments = _jira.Comments.ToList();
        Assert.That(comments.Count(c => c.IssueId == "1001"), Is.EqualTo(1));
        Assert.That(comments.Count(c => c.IssueId == "1002"), Is.EqualTo(1));
        Assert.That(comments.Single(c => c.IssueId == "1001").Body.ToJsonString(), Does.Contain("KMSQA").And.Not.Contain("KMSRC"));
        Assert.That(_jira.Attachments.Where(x => x.IssueId == "1001").All(x => x.FileName.Contains("ABC-1_KMSQA")), Is.True);

        // ── No secret anywhere in the database (every column of every table) ──
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        var dump = new System.Text.StringBuilder();
        foreach (var table in new[] { "InvestigationJobs", "InvestigationJobEvents", "AuditEntries", "JobSecrets", "JiraInstallations", "CredentialProfiles" })
        {
            await using var cmd = new NpgsqlCommand($"SELECT row_to_json(t)::text FROM \"{table}\" t", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                dump.AppendLine(reader.GetString(0));
        }
        var all = dump.ToString();
        foreach (var secret in new[] { AlicePassword, BobPassword, BobWrong, TestPortal.MfaCode, AliceToken, BobToken, SimulatedJira.SystemToken })
            Assert.That(all, Does.Not.Contain(secret), $"secret '{secret}' found in the database");
        Assert.That(all, Does.Contain(AuditActions.EnvironmentSelected).And.Contain(AuditActions.CredentialsSubmitted)
            .And.Contain(AuditActions.AuthenticationAttempt).And.Contain(AuditActions.ReportPublished));

        // ── Worker readiness reports its real dependencies ──
        var ready = await _worker.CreateClient().GetAsync("/readyz");
        var readyBody = await ready.Content.ReadAsStringAsync();
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK), readyBody);
        Assert.That(readyBody, Does.Contain("\"database\"").And.Contain("\"browser\"").And.Contain("\"key-ring\""));
    }
}
