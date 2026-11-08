using System.Collections.Concurrent;
using System.Text.Json;
using BugInvestigationAgent.Core;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Browser;
using BugInvestigationAgent.Core.Configuration;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Core.Environments;
using BugInvestigationAgent.Core.Jira;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Memory;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Reporting;
using BugInvestigationAgent.Core.Repositories;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Skills;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Infrastructure;
using BugInvestigationAgent.Infrastructure.Forge;
using BugInvestigationAgent.Infrastructure.Hosting;
using BugInvestigationAgent.Infrastructure.Jobs;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace BugInvestigationAgent.Api.Tests;

/// <summary>
/// Acceptance scenario, simulated end to end on the REAL worker runner + REAL agent orchestrator + REAL job store:
/// two developers invoke the agent concurrently; each picks an environment, supplies credentials through the secure
/// channel, the (simulated) browser signs in, the bug is investigated and a report is published to each issue —
/// with Jira reads made using each developer's own token and no cross-user leakage.
/// Jira, the browser and the LLM are simulated; no real environments or credentials are used.
/// </summary>
[NonParallelizable]
public class WorkerEndToEndTests
{
    private sealed class FakeSkill : ISkill
    {
        private readonly Func<SkillExecutionRequest, Task> _run;
        public FakeSkill(string id, Func<SkillExecutionRequest, Task>? run = null) { SkillId = id; _run = run ?? (_ => Task.CompletedTask); }
        public string SkillId { get; }
        public string DisplayName => SkillId;
        public AgentPermission RequiredPermissions => AgentPermission.None;
        public bool CanExecute(RuntimeContext context, IPermissionGate permissions) => true;
        public async Task<SkillResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
        {
            await _run(request);
            return SkillResult.Ok();
        }
    }

    private sealed record JiraRead(Guid JobId, string? IssueKey, bool Delegated, string Token);
    private sealed record LoginAttempt(Guid JobId, string? Environment, string Username, string Password);

    private TestDb _db = null!;
    private ServiceProvider _sp = null!;
    private FakeJira _jira = null!;
    private string _temp = null!;
    private readonly ConcurrentBag<JiraRead> _reads = new();
    private readonly ConcurrentBag<LoginAttempt> _logins = new();

    [SetUp]
    public void SetUp()
    {
        _db = new TestDb();
        _jira = new FakeJira();
        _temp = Path.Combine(Path.GetTempPath(), "bia-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AgentOptions>(o =>
        {
            o.EvidenceDirectory = Path.Combine(_temp, "evidence");
            o.Permissions = new AgentPermissionOptions { ModifyCode = false, RunValidation = false }; // read-only rollout
        });
        services.Configure<EnvironmentRegistryOptions>(o =>
        {
            o.Environments["KMSRC"] = new EnvironmentDefinitionOptions { ApplicationId = "KMS", BaseUrl = "https://kmsrc.example.test", Type = EnvironmentType.ReleaseCandidate };
            o.Environments["KMSQA"] = new EnvironmentDefinitionOptions { ApplicationId = "KMS", BaseUrl = "https://kmsqa.example.test", Type = EnvironmentType.QA };
        });
        services.Configure<WorkerOptions>(o => { o.PollIntervalMs = 40; o.LeaseSeconds = 30; });
        services.Configure<ForgeOptions>(o => { o.AppId = FitFactory.AppId; o.EnvironmentId = "env-1"; });
        services.Configure<ReportPublishingOptions>(_ => { });
        services.Configure<RepositoryMappingOptions>(o => o.CheckoutRoot = Path.Combine(_temp, "checkouts"));
        services.AddBugInvestigationCore();

        services.AddSingleton<IDbContextFactory<AgentDbContext>>(_db);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IInvestigationJobStore, EfInvestigationJobStore>();
        services.AddSingleton<IJobSecretStore, DataProtectionJobSecretStore>();
        services.AddSingleton<IAuditLog, EfAuditLog>();
        var evidence = new Mock<IEvidenceStore>();
        evidence.Setup(e => e.PublishAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("blob://evidence/");
        evidence.Setup(e => e.OpenReadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream([1]));
        services.AddSingleton(evidence.Object);
        var http = new Mock<IHttpClientFactory>();
        http.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_jira, disposeHandler: false));
        services.AddSingleton(http.Object);
        services.AddSingleton<JiraCloudClient>();
        services.AddSingleton<JiraReportPublisher>();
        services.AddSingleton<IJiraConnectionSettings, HostedJiraConnectionSettings>();
        services.AddSingleton<ConfiguredJiraAccessProvider>();
        services.Replace(ServiceDescriptor.Singleton<IJiraAccessProvider, HostedJiraAccessProvider>());
        services.AddSingleton<IWorkspaceDefaults>(new Workspace(_temp));
        services.AddSingleton(Mock.Of<IBrowserSessionCloser>());
        services.AddScoped(_ => Mock.Of<IEngineeringMemoryStore>(m =>
            m.QueryRelevantAsync(It.IsAny<string?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<EngineeringLesson>>(Array.Empty<EngineeringLesson>())));
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.FindForEnvironmentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((CredentialProfile?)null);
        services.AddScoped(_ => vault.Object);

        services.AddScoped<ISkill>(sp => new FakeSkill(SkillIds.Jira, async r =>
        {
            // Reads go through the hosted access provider: must be the invoking user's own token.
            var access = await sp.GetRequiredService<IJiraAccessProvider>().GetReadAccessAsync(r.Context);
            _reads.Add(new JiraRead(r.Context.InvestigationId, r.Context.JiraTicketKey, access.Access!.IsUserDelegated, access.Access.Authorization.Parameter!));
            r.Context.JiraDetailsLoaded = true;
            r.Context.Summary = "Save button throws an error";
            r.Context.ReproductionSteps = new List<string> { "Open module 'Incident'", "Click Save" };
        }));
        foreach (var id in new[] { SkillIds.TicketUnderstanding, SkillIds.HistoricalJiraSearch, SkillIds.HistoricalReproductionReuse, SkillIds.KmsDocumentation, SkillIds.EvidenceReporting })
            services.AddScoped<ISkill>(_ => new FakeSkill(id));
        services.AddScoped<ISkill>(sp => new FakeSkill(SkillIds.EnvironmentDiscovery, r =>
        {
            sp.GetRequiredService<IEnvironmentSelectionService>().ApplySuggestion(r.Context);
            return Task.CompletedTask;
        }));
        services.AddScoped<ISkill>(sp => new FakeSkill(SkillIds.BrowserAutomation, r =>
        {
            var ctx = r.Context;
            var secret = sp.GetRequiredService<ISessionCredentialStore>().Take(ctx.InvestigationId, ctx.SelectedEnvironmentId!);
            if (secret is null)
            {
                ctx.AuthenticationStatus = AuthenticationStatus.CredentialsRequired;
                return Task.CompletedTask;
            }
            _logins.Add(new LoginAttempt(ctx.InvestigationId, ctx.SelectedEnvironmentId, secret.Username, secret.Password!));
            ctx.AuthenticationStatus = secret.Password == "right-password" ? AuthenticationStatus.Succeeded : AuthenticationStatus.InvalidCredentials;
            ctx.AuthenticationMessage = ctx.AuthenticationStatus == AuthenticationStatus.Succeeded ? "ok" : "The environment rejected the username or password.";
            return Task.CompletedTask;
        }));
        services.AddScoped<ISkill>(_ => new FakeSkill(SkillIds.Reproduction, r =>
        {
            Assert.That(r.Context.AuthenticationStatus, Is.EqualTo(AuthenticationStatus.Succeeded), "never reproduce before sign-in");
            r.Context.ReproductionOutcome = ReproductionOutcome.Reproduced;
            r.Context.ReproductionNotes = "Error toast after Save.";
            r.Context.Evidence.Add(EvidenceItem.For(r.Context, "screenshot", $"{r.Context.JiraTicketKey}_{r.Context.SelectedEnvironmentId}_repro-final-state.png",
                "Observed failure state", EvidenceCategories.FailureState));
            return Task.CompletedTask;
        }));
        services.AddScoped<ISkill>(_ => new FakeSkill(SkillIds.CodeInvestigation, r =>
        {
            r.Context.RootCause = "SaveHandler dereferences a null assignee.";
            r.Context.ProposedFixSummary = "Null-check the assignee.";
            return Task.CompletedTask;
        }));

        services.AddSingleton<InvestigationJobRunner>();
        _sp = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _sp.Dispose();
        _db.Dispose();
        _jira.Dispose();
        try { Directory.Delete(_temp, true); } catch { }
    }

    private sealed class Workspace : IWorkspaceDefaults
    {
        public Workspace(string path) => DefaultWorkspacePath = path;
        public string? DefaultWorkspacePath { get; }
    }

    private T S<T>() where T : notnull => _sp.GetRequiredService<T>();

    private async Task<InvestigationJob> CreateJob(string account, string issueKey, string userToken)
    {
        var job = await S<IInvestigationJobStore>().CreateAsync(new InvestigationJob
        {
            TenantId = FitFactory.Installation, CloudId = FitFactory.CloudId, SiteUrl = "https://example-site.atlassian.net",
            IssueId = "100" + issueKey.Split('-')[1], IssueKey = issueKey, InvokingAccountId = account, ContextId = "ctx-" + account,
            ForgeEnvironmentId = "env-1"
        });
        await S<IJobSecretStore>().SetAsync(job.Id, JobSecretKinds.JiraUserToken, userToken, DateTimeOffset.UtcNow.AddHours(1));
        await S<IJobSecretStore>().SetAsync(job.Id, JobSecretKinds.JiraSystemToken, FakeJira.SystemToken, DateTimeOffset.UtcNow.AddHours(1));
        return job;
    }

    private async Task<InvestigationJob> WaitFor(Guid id, Func<InvestigationJob, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var job = await S<IInvestigationJobStore>().GetAsync(id);
            if (job is not null && condition(job))
                return job;
            await Task.Delay(30);
        }
        var last = await S<IInvestigationJobStore>().GetAsync(id);
        Assert.Fail($"Timed out waiting for {what}. Status={last?.Status} Kind={last?.AwaitingInputKind} Msg={last?.StatusMessage}");
        return null!;
    }

    private Task Reply(Guid id, string text) => S<IInvestigationJobStore>().UpdateAsync(id, j => j.PendingUserReply = text);

    private Task SubmitCredentials(Guid id, string env, string user, string password) =>
        S<IJobSecretStore>().SetAsync(id, JobSecretKinds.EnvironmentCredentials(env),
            JsonSerializer.Serialize(new SubmittedCredentials { Username = user, Password = password }), DateTimeOffset.UtcNow.AddMinutes(5));

    [Test]
    public async Task Two_developers_investigate_concurrently_with_isolated_environments_credentials_and_reports()
    {
        var runner = S<InvestigationJobRunner>();
        var alice = await CreateJob("acc-alice", "ABC-1", "tok-alice");
        var bob = await CreateJob("acc-bob", "ABC-2", "tok-bob");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runs = new[] { runner.RunAsync(alice.Id, "worker-1", stop.Token), runner.RunAsync(bob.Id, "worker-2", stop.Token) };

        // 1) Both ask which environment to use (environment cannot be inferred from these tickets).
        var a = await WaitFor(alice.Id, j => j.AwaitingInputKind == AwaitingInputKinds.Environment, "Alice env prompt");
        await WaitFor(bob.Id, j => j.AwaitingInputKind == AwaitingInputKinds.Environment, "Bob env prompt");
        Assert.That(a.Status, Is.EqualTo(InvestigationJobStatus.AwaitingUserInput));
        Assert.That(a.AwaitingInputPrompt, Does.Contain("KMSRC").And.Contain("KMSQA"));

        // 2) Different environments.
        await Reply(alice.Id, "KMSQA");
        await Reply(bob.Id, "use KMSRC please");
        a = await WaitFor(alice.Id, j => j.AwaitingInputKind == AwaitingInputKinds.Credentials, "Alice credential prompt");
        var b = await WaitFor(bob.Id, j => j.AwaitingInputKind == AwaitingInputKinds.Credentials, "Bob credential prompt");
        Assert.That(a.EnvironmentId, Is.EqualTo("KMSQA"));
        Assert.That(b.EnvironmentId, Is.EqualTo("KMSRC"));
        Assert.That(a.AwaitingInputPrompt, Does.Contain("/jira/settings/personal/apps/" + FitFactory.AppId).And.Contain("do NOT type passwords"));

        // 3) Credentials arrive through the secure channel; Bob first mistypes his password.
        await SubmitCredentials(alice.Id, "KMSQA", "alice_qa", "right-password");
        await SubmitCredentials(bob.Id, "KMSRC", "bob_rc", "wrong-password");

        b = await WaitFor(bob.Id, j => j.AwaitingInputKind == AwaitingInputKinds.Credentials && (j.AwaitingInputPrompt ?? "").Contains("rejected"), "Bob re-prompt after rejection");
        Assert.That(b.Status, Is.EqualTo(InvestigationJobStatus.AwaitingUserInput));

        a = await WaitFor(alice.Id, j => j.IsTerminal, "Alice completion");
        Assert.That(a.Status, Is.EqualTo(InvestigationJobStatus.Completed), a.ErrorDetails);

        await SubmitCredentials(bob.Id, "KMSRC", "bob_rc", "right-password");
        b = await WaitFor(bob.Id, j => j.IsTerminal, "Bob completion");
        Assert.That(b.Status, Is.EqualTo(InvestigationJobStatus.Completed), b.ErrorDetails);
        await Task.WhenAll(runs);
        // Completed is persisted before publication; read the final state once the runners have returned.
        a = (await S<IInvestigationJobStore>().GetAsync(alice.Id))!;
        b = (await S<IInvestigationJobStore>().GetAsync(bob.Id))!;

        // 4) Jira reads used each developer's own token (permissions respected) — never the other's.
        Assert.That(_reads.Where(r => r.JobId == alice.Id).All(r => r.Delegated && r.Token == "tok-alice"), Is.True);
        Assert.That(_reads.Where(r => r.JobId == bob.Id).All(r => r.Delegated && r.Token == "tok-bob"), Is.True);

        // 5) Credentials never crossed users or environments.
        Assert.That(_logins.Where(l => l.JobId == alice.Id).All(l => l.Environment == "KMSQA" && l.Username == "alice_qa"), Is.True);
        Assert.That(_logins.Where(l => l.JobId == bob.Id).All(l => l.Environment == "KMSRC" && l.Username == "bob_rc"), Is.True);
        Assert.That(_logins.Count(l => l.JobId == bob.Id), Is.EqualTo(2), "rejected attempt + retry");

        // 6) Structured, honest reports were published once to each original issue.
        var reportA = InvestigationReport.FromJson(a.ReportJson)!;
        Assert.That(reportA.IssueKey, Is.EqualTo("ABC-1"));
        Assert.That(reportA.EnvironmentId, Is.EqualTo("KMSQA"));
        Assert.That(reportA.ConfirmedFindings.Any(f => f.StartsWith("Bug reproduced")), Is.True);
        Assert.That(reportA.Hypotheses.Any(h => h.Contains("Probable root cause")), Is.True, "root cause is not claimed as confirmed");
        Assert.That(a.ReportPublishedAt, Is.Not.Null);
        Assert.That(b.ReportPublishedAt, Is.Not.Null);
        Assert.That(_jira.PostedComments, Has.Count.EqualTo(2));
        Assert.That(_jira.Requests, Does.Contain("POST /ex/jira/cloud-test-1/rest/api/3/issue/1001/comment as system"));
        Assert.That(_jira.Requests, Does.Contain("POST /ex/jira/cloud-test-1/rest/api/3/issue/1002/comment as system"));
        Assert.That(a.ResultSummary, Does.Contain("Confirmed findings"));

        // 7) No secrets left behind; passwords never in events, audit or reports.
        foreach (var id in new[] { alice.Id, bob.Id })
        {
            Assert.That(await S<IJobSecretStore>().GetAsync(id, JobSecretKinds.JiraUserToken), Is.Null);
            var events = string.Join("\n", (await S<IInvestigationJobStore>().ListEventsAsync(id, 500)).Select(e => e.Message));
            var audit = string.Join("\n", (await S<IAuditLog>().ListForJobAsync(id)).Select(x => x.Detail));
            var job = await S<IInvestigationJobStore>().GetAsync(id);
            Assert.That(events + audit + job!.ReportJson + job.ResultSummary, Does.Not.Contain("right-password").And.Not.Contain("wrong-password"));
        }
        var aliceAudit = (await S<IAuditLog>().ListForJobAsync(alice.Id)).Select(x => x.Action).ToList();
        Assert.That(aliceAudit, Does.Contain(AuditActions.EnvironmentSelected).And.Contain(AuditActions.AuthenticationAttempt)
            .And.Contain(AuditActions.InvestigationCompleted).And.Contain(AuditActions.ReportPublished));
    }

    [Test]
    public async Task Cancellation_stops_one_job_without_affecting_another()
    {
        var runner = S<InvestigationJobRunner>();
        var alice = await CreateJob("acc-alice", "ABC-1", "tok-alice");
        var bob = await CreateJob("acc-bob", "ABC-2", "tok-bob");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runs = new[] { runner.RunAsync(alice.Id, "w1", stop.Token), runner.RunAsync(bob.Id, "w2", stop.Token) };

        await WaitFor(alice.Id, j => j.AwaitingInputKind == AwaitingInputKinds.Environment, "Alice env prompt");
        await WaitFor(bob.Id, j => j.AwaitingInputKind == AwaitingInputKinds.Environment, "Bob env prompt");
        await S<IInvestigationJobStore>().UpdateAsync(alice.Id, j => j.CancelRequested = true);

        var a = await WaitFor(alice.Id, j => j.IsTerminal, "Alice cancelled");
        Assert.That(a.Status, Is.EqualTo(InvestigationJobStatus.Cancelled));
        var b = await S<IInvestigationJobStore>().GetAsync(bob.Id);
        Assert.That(b!.Status, Is.EqualTo(InvestigationJobStatus.AwaitingUserInput), "Bob's investigation is unaffected");

        await S<IInvestigationJobStore>().UpdateAsync(bob.Id, j => j.CancelRequested = true);
        await Task.WhenAll(runs);
        Assert.That(_jira.PostedComments, Is.Empty, "cancelled jobs do not post reports");
    }
}
