using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Infrastructure.Forge;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

namespace BugInvestigationAgent.Api.Tests;

public class ForgeInvocationTokenValidatorTests
{
    private static ForgeInvocationTokenValidator Validator(FitFactory fit, params string[] allowedClouds) =>
        new(new FixedJwks(fit.Key),
            Options.Create(new ForgeOptions { AppId = FitFactory.AppId, AllowedCloudIds = allowedClouds.ToList() }),
            NullLogger<ForgeInvocationTokenValidator>.Instance);

    [Test]
    public async Task Valid_token_yields_validated_installation_site_and_user()
    {
        var fit = new FitFactory();
        var result = await Validator(fit).ValidateAsync("Bearer " + fit.Mint("acc-alice"));

        Assert.That(result.IsValid, Is.True, result.Error);
        Assert.That(result.Invocation!.InstallationId, Is.EqualTo(FitFactory.Installation));
        Assert.That(result.Invocation.CloudId, Is.EqualTo(FitFactory.CloudId));
        Assert.That(result.Invocation.PrincipalAccountId, Is.EqualTo("acc-alice"));
        Assert.That(result.Invocation.EnvironmentId, Is.EqualTo("env-1"));
    }

    [Test]
    public async Task Rejects_missing_expired_wrong_audience_wrong_issuer_and_foreign_signature()
    {
        var fit = new FitFactory();
        var v = Validator(fit);
        Assert.That((await v.ValidateAsync(null)).IsValid, Is.False);
        Assert.That((await v.ValidateAsync("Basic abc")).IsValid, Is.False);
        Assert.That((await v.ValidateAsync("Bearer " + fit.Mint("a", lifetime: TimeSpan.FromMinutes(-5)))).IsValid, Is.False, "expired");
        Assert.That((await v.ValidateAsync("Bearer " + fit.Mint("a", audience: "ari:cloud:ecosystem::app/other"))).IsValid, Is.False, "audience");
        Assert.That((await v.ValidateAsync("Bearer " + fit.Mint("a", issuer: "someone-else"))).IsValid, Is.False, "issuer");
        var attacker = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-kid" };
        Assert.That((await v.ValidateAsync("Bearer " + fit.Mint("a", signingKey: attacker))).IsValid, Is.False, "signature");
    }

    [Test]
    public async Task Enforces_cloud_id_allow_list()
    {
        var fit = new FitFactory();
        Assert.That((await Validator(fit, "other-site").ValidateAsync("Bearer " + fit.Mint("a"))).IsValid, Is.False);
        Assert.That((await Validator(fit, FitFactory.CloudId).ValidateAsync("Bearer " + fit.Mint("a"))).IsValid, Is.True);
    }
}

public class ForgeApiTests
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiFactory();
        _client = _factory.CreateClient();
        _factory.Jira.GrantUser("tok-alice", "acc-alice", "ABC-1", "ABC-2");
        _factory.Jira.GrantUser("tok-bob", "acc-bob", "ABC-2");
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<JsonObject> Rpc(string method, object @params, string? principal, string? userToken, string? fit = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/a2a/json-rpc")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = "r1", method, @params })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fit ?? _factory.Fit.Mint(principal));
        if (userToken is not null)
            req.Headers.Add("x-forge-oauth-user", userToken);
        req.Headers.Add("x-forge-oauth-system", FakeJira.SystemToken);
        var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
    }

    private static object Invocation(string issueKey, string userAccountId, string? contextId = null) => new
    {
        message = new
        {
            role = "ROLE_USER",
            messageId = Guid.NewGuid().ToString(),
            contextId,
            parts = new object[]
            {
                new { text = "Investigate" },
                new { data = new { userAccountId, agentAccountId = "UNKNOWN", invocationType = "ISSUE_ASSIGNMENT",
                    issue = new { id = "100" + issueKey.Split('-')[1], fields = new { key = issueKey, summary = "s" } } } }
            }
        }
    };

    private static string State(JsonObject rpc) => rpc["result"]!["task"]!["status"]!["state"]!.GetValue<string>();
    private static string TaskId(JsonObject rpc) => rpc["result"]!["task"]!["id"]!.GetValue<string>();
    private static string Text(JsonObject rpc) => rpc["result"]!["task"]!["status"]!["message"]!["parts"]![0]!["text"]!.GetValue<string>();

    private T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    [Test]
    public async Task Every_business_endpoint_requires_authentication()
    {
        Assert.That((await _client.GetAsync("/healthz")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        foreach (var path in new[] { "/a2a/json-rpc", "/atlassian/installed", "/forge/credentials/pending" })
        {
            var resp = await _client.PostAsync(path, JsonContent.Create(new { }));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), path);
        }
        var direct = await _client.PostAsync($"/api/jobs/{Guid.NewGuid()}/credentials", JsonContent.Create(new { username = "u", password = "p" }));
        Assert.That(direct.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Invocation_creates_durable_job_with_validated_identity_and_encrypted_user_token()
    {
        var rpc = await Rpc("SendMessage", Invocation("ABC-1", "acc-alice"), "acc-alice", "tok-alice");

        Assert.That(State(rpc), Is.EqualTo("TASK_STATE_SUBMITTED"));
        var job = await Service<IInvestigationJobStore>().GetAsync(Guid.Parse(TaskId(rpc)));
        Assert.That(job, Is.Not.Null);
        Assert.That(job!.TenantId, Is.EqualTo(FitFactory.Installation));
        Assert.That(job.CloudId, Is.EqualTo(FitFactory.CloudId));
        Assert.That(job.IssueKey, Is.EqualTo("ABC-1"));
        Assert.That(job.IssueId, Is.EqualTo("1001"));
        Assert.That(job.InvokingAccountId, Is.EqualTo("acc-alice"));
        Assert.That(job.Status, Is.EqualTo(InvestigationJobStatus.Queued));

        Assert.That(await Service<IJobSecretStore>().GetAsync(job.Id, JobSecretKinds.JiraUserToken), Is.EqualTo("tok-alice"));
        await using var db = await Service<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        var raw = await db.JobSecrets.AsNoTracking().Where(s => s.JobId == job.Id).Select(s => s.ProtectedValue).ToListAsync();
        Assert.That(raw.Any(r => r.Contains("tok-alice")), Is.False, "tokens are encrypted at rest");
        Assert.That(_factory.Jira.Requests, Does.Contain("GET /ex/jira/cloud-test-1/rest/api/3/issue/1001 as acc-alice"),
            "the issue is read with the invoking user's own token");

        var audit = await Service<IAuditLog>().ListForJobAsync(job.Id);
        Assert.That(audit.Select(a => a.Action), Does.Contain(AuditActions.InvestigationCreated));
    }

    [Test]
    public async Task User_without_access_to_the_issue_is_rejected_and_no_job_is_created()
    {
        var rpc = await Rpc("SendMessage", Invocation("ABC-1", "acc-bob"), "acc-bob", "tok-bob");

        Assert.That(State(rpc), Is.EqualTo("TASK_STATE_REJECTED"));
        Assert.That(Text(rpc), Does.Contain("don't have access"));
        await using var db = await Service<IDbContextFactory<AgentDbContext>>().CreateDbContextAsync();
        Assert.That(await db.InvestigationJobs.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Spoofed_user_ids_in_the_body_or_token_mismatch_are_rejected()
    {
        // Body claims Alice, but the user token belongs to Bob.
        var spoofBody = await Rpc("SendMessage", Invocation("ABC-2", "acc-alice"), "acc-bob", "tok-bob");
        Assert.That(State(spoofBody), Is.EqualTo("TASK_STATE_REJECTED"));

        // FIT principal says Alice, user token is Bob's.
        var spoofToken = await Rpc("SendMessage", Invocation("ABC-2", "acc-bob"), "acc-alice", "tok-bob");
        Assert.That(State(spoofToken), Is.EqualTo("TASK_STATE_REJECTED"));
    }

    [Test]
    public async Task Tasks_are_isolated_per_user_and_per_installation()
    {
        var alice = await Rpc("SendMessage", Invocation("ABC-2", "acc-alice"), "acc-alice", "tok-alice");
        var bob = await Rpc("SendMessage", Invocation("ABC-2", "acc-bob"), "acc-bob", "tok-bob");
        Assert.That(TaskId(alice), Is.Not.EqualTo(TaskId(bob)), "independent investigations of the same issue");

        var own = await Rpc("GetTask", new { id = TaskId(alice) }, "acc-alice", "tok-alice");
        Assert.That(State(own), Is.EqualTo("TASK_STATE_SUBMITTED"));

        var other = await Rpc("GetTask", new { id = TaskId(alice) }, "acc-bob", "tok-bob");
        Assert.That(other["error"]!["code"]!.GetValue<int>(), Is.EqualTo(-32001), "Bob cannot see Alice's task");

        var foreignSite = await Rpc("GetTask", new { id = TaskId(alice) }, null, null,
            _factory.Fit.Mint("acc-alice", installationId: "ari:cloud:ecosystem::installation/other"));
        Assert.That(foreignSite["error"]!["code"]!.GetValue<int>(), Is.EqualTo(-32001));
    }

    [Test]
    public async Task Cancel_task_cancels_once_and_then_reports_not_cancelable()
    {
        var created = await Rpc("SendMessage", Invocation("ABC-1", "acc-alice"), "acc-alice", "tok-alice");
        var cancel = await Rpc("CancelTask", new { id = TaskId(created) }, "acc-alice", "tok-alice");
        Assert.That(State(cancel), Is.EqualTo("TASK_STATE_CANCELED"));

        var again = await Rpc("CancelTask", new { id = TaskId(created) }, "acc-alice", "tok-alice");
        Assert.That(again["error"]!["code"]!.GetValue<int>(), Is.EqualTo(-32002));
    }

    [Test]
    public async Task Follow_up_reply_is_routed_to_the_users_own_job_and_secrets_are_refused_in_chat()
    {
        var created = await Rpc("SendMessage", Invocation("ABC-1", "acc-alice"), "acc-alice", "tok-alice");
        var contextId = created["result"]!["task"]!["contextId"]!.GetValue<string>();
        var jobs = Service<IInvestigationJobStore>();
        await jobs.UpdateAsync(Guid.Parse(TaskId(created)), j =>
        {
            j.Status = InvestigationJobStatus.AwaitingUserInput;
            j.AwaitingInputKind = AwaitingInputKinds.Environment;
        });

        var reply = new { message = new { role = "ROLE_USER", messageId = "m2", contextId, parts = new object[] { new { text = "KMSQA please" } } } };
        var resp = await Rpc("SendMessage", reply, "acc-alice", "tok-alice");
        Assert.That(TaskId(resp), Is.EqualTo(TaskId(created)));
        Assert.That((await jobs.GetAsync(Guid.Parse(TaskId(created))))!.PendingUserReply, Is.EqualTo("KMSQA please"));

        var secret = new { message = new { role = "ROLE_USER", messageId = "m3", contextId, parts = new object[] { new { text = "password: Hunter2!" } } } };
        var refused = await Rpc("SendMessage", secret, "acc-alice", "tok-alice");
        Assert.That(Text(refused), Does.Contain("don't type passwords"));
        Assert.That((await jobs.GetAsync(Guid.Parse(TaskId(created))))!.PendingUserReply, Does.Not.Contain("Hunter2"));

        // Bob replying in Alice's conversation id does not reach Alice's job.
        var bobReply = new { message = new { role = "ROLE_USER", messageId = "m4", contextId, parts = new object[] { new { text = "KMSRC" } } } };
        await Rpc("SendMessage", bobReply, "acc-bob", "tok-bob");
        Assert.That((await jobs.GetAsync(Guid.Parse(TaskId(created))))!.PendingUserReply, Is.EqualTo("KMSQA please"));
    }

    [Test]
    public async Task Installed_event_registers_the_site()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/atlassian/installed")
        {
            Content = JsonContent.Create(new { context = "ari:cloud:jira::site/" + FitFactory.CloudId, installerAccountId = "acc-admin" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Fit.Mint(null));
        Assert.That((await _client.SendAsync(req)).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var installation = await Service<IInvestigationJobStore>().GetInstallationAsync(FitFactory.Installation);
        Assert.That(installation!.CloudId, Is.EqualTo(FitFactory.CloudId));
    }

    [Test]
    public async Task Credential_channel_is_bound_to_user_job_environment_and_single_use_tickets()
    {
        var created = await Rpc("SendMessage", Invocation("ABC-1", "acc-alice"), "acc-alice", "tok-alice");
        var jobId = Guid.Parse(TaskId(created));
        await Service<IInvestigationJobStore>().UpdateAsync(jobId, j =>
        {
            j.Status = InvestigationJobStatus.AwaitingUserInput;
            j.AwaitingInputKind = AwaitingInputKinds.Credentials;
            j.EnvironmentId = "KMSQA";
        });

        async Task<JsonObject> Pending(string principal)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/forge/credentials/pending") { Content = JsonContent.Create(new { }) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Fit.Mint(principal));
            var resp = await _client.SendAsync(req);
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
        }

        Assert.That((await Pending("acc-bob"))["items"]!.AsArray(), Is.Empty, "Bob never sees Alice's pending request");

        var alice = await Pending("acc-alice");
        var item = alice["items"]!.AsArray().Single()!;
        Assert.That(item["environmentId"]!.GetValue<string>(), Is.EqualTo("KMSQA"));
        var ticket = item["ticket"]!.GetValue<string>();

        async Task<HttpStatusCode> Submit(string? t, string env)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/credentials")
            {
                Content = JsonContent.Create(new { environmentId = env, username = "qa_admin", password = "S3cret!", remember = false })
            };
            if (t is not null) req.Headers.Add("X-Submission-Ticket", t);
            return (await _client.SendAsync(req)).StatusCode;
        }

        Assert.That(await Submit("forged-ticket", "KMSQA"), Is.EqualTo(HttpStatusCode.Unauthorized));
        // The forged attempt consumed the ticket (single use) — get a fresh one.
        ticket = (await Pending("acc-alice"))["items"]![0]!["ticket"]!.GetValue<string>();
        Assert.That(await Submit(ticket, "KMSRC"), Is.EqualTo(HttpStatusCode.BadRequest), "credentials are bound to the awaited environment");
        ticket = (await Pending("acc-alice"))["items"]![0]!["ticket"]!.GetValue<string>();
        Assert.That(await Submit(ticket, "KMSQA"), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await Submit(ticket, "KMSQA"), Is.EqualTo(HttpStatusCode.Unauthorized), "ticket reuse");

        var stored = await Service<IJobSecretStore>().GetAsync(jobId, JobSecretKinds.EnvironmentCredentials("KMSQA"));
        Assert.That(stored, Does.Contain("qa_admin"));
        var audit = await Service<IAuditLog>().ListForJobAsync(jobId);
        Assert.That(audit.Any(a => a.Action == AuditActions.CredentialsSubmitted && !(a.Detail ?? "").Contains("S3cret!")), Is.True);
    }

    [Test]
    public async Task Cors_allows_only_forge_custom_ui_origins_for_direct_submission()
    {
        async Task<string?> Preflight(string origin)
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, $"/api/jobs/{Guid.NewGuid()}/credentials");
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", "POST");
            req.Headers.Add("Access-Control-Request-Headers", "content-type,x-submission-ticket");
            var resp = await _client.SendAsync(req);
            return resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.First() : null;
        }

        Assert.That(await Preflight("https://abc123.cdn.prod.atlassian-dev.net"), Is.EqualTo("https://abc123.cdn.prod.atlassian-dev.net"));
        Assert.That(await Preflight("https://evil.example.com"), Is.Null);
    }
}
