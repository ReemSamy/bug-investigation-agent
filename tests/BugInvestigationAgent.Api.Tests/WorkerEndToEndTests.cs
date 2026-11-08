using System.Collections.Concurrent;
using BugInvestigationAgent.Api.Credentials;
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
using BugInvestigationAgent.Core.Orchestration;
using BugInvestigationAgent.Core.Permissions;
using BugInvestigationAgent.Core.Reporting;
using BugInvestigationAgent.Core.Repositories;
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
using Microsoft.Extensions.Hosting;
using Moq;

namespace BugInvestigationAgent.Api.Tests;

/// <summary>
/// SIMULATED externals (Jira HTTP, browser sign-in, reproduction, code analysis are fakes); REAL worker service,
/// queue, runner, orchestrator, job store, secret store, credential channel and suspend/resume. Proves the worker
/// lifecycle: a job waiting for input releases its worker slot, resumes from its checkpoint on new input, MFA is held
/// only briefly, crashes are recovered, and two developers never affect each other.
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

    private sealed record JiraRead(Guid JobId, bool Delegated, string Token);
    private sealed record LoginAttempt(Guid JobId, string? Environment, string Username, string Secret, string Mode);

    private TestDb _db = null!;
    private ServiceProvider _sp = null!;
    private FakeJira _jira = null!;
    private string _temp = null!;
    private CancellationTokenSource _stop = null!;
    private InvestigationWorkerService _worker = null!;
    private readonly ConcurrentBag<JiraRead> _reads = new();
    private readonly ConcurrentBag<LoginAttempt> _logins = new();
    private readonly ConcurrentDictionary<Guid, int> _analysisRuns = new();

    private void Build(int slots = 1, int mfaHoldMinutes = 5, bool startWorker = true)
    {
        _db = new TestDb();
        _jira = new FakeJira();
        _temp = Path.Combine(Path.GetTempPath(), "bia-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AgentOptions>(o => o.EvidenceDirectory = Path.Combine(_temp, "evidence"));
        services.Configure<EnvironmentRegistryOptions>(o =>
        {
            o.Environments["KMSRC"] = new EnvironmentDefinitionOptions { ApplicationId = "KMS", BaseUrl = "https://kmsrc.example.test", Type = EnvironmentType.ReleaseCandidate };
            o.Environments["KMSQA"] = new EnvironmentDefinitionOptions { ApplicationId = "KMS", BaseUrl = "https://kmsqa.example.test", Type = EnvironmentType.QA };
        });
        services.Configure<WorkerOptions>(o => { o.PollIntervalMs = 30; o.LeaseSeconds = 30; o.MaxConcurrentJobs = slots; o.MfaHoldMinutes = mfaHoldMinutes; });
        services.Configure<ForgeOptions>(o => { o.AppId = FitFactory.AppId; o.EnvironmentId = "env-1"; });
        services.Configure<ReportPublishingOptions>(_ => { });
        services.Configure<RepositoryMappingOptions>(o => o.CheckoutRoot = Path.Combine(_temp, "checkouts"));
        services.AddBugInvestigationCore();

        services.AddSingleton<IDbContextFactory<AgentDbContext>>(_db);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IInvestigationJobStore, EfInvestigationJobStore>();
        services.AddSingleton<IJobSecretStore, DataProtectionJobSecretStore>();
        services.AddSingleton<IAuditLog, EfAuditLog>();
        services.AddSingleton<IInvestigationQueue, DatabaseInvestigationQueue>();
        services.AddSingleton<JobInputService>();
        services.AddSingleton<CredentialChannelService>();
        var evidence = new Mock<IEvidenceStore>();
        evidence.Setup(e => e.PublishAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("blob://evidence/");
        evidence.Setup(e => e.OpenReadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream([1]));
        services.AddSingleton(evidence.Object);
        var http = new Mock<IHttpClientFactory>();
        http.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_jira, disposeHandler: false));
        services.AddSingleton(http.Object);
        services.AddSingleton<JiraCloudClient>();
        services.AddSingleton<JiraReportPublisher>();
        services.AddSingleton<ConfiguredJiraAccessProvider>();
        services.AddSingleton<IJiraConnectionSettings, HostedJiraConnectionSettings>();
        services.AddSingleton<IWorkspaceDefaults>(new Workspace(_temp));
        services.AddSingleton(Mock.Of<IBrowserSessionCloser>());
        services.AddScoped(_ => Mock.Of<IEngineeringMemoryStore>(m =>
            m.QueryRelevantAsync(It.IsAny<string?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<EngineeringLesson>>(Array.Empty<EngineeringLesson>())));
        var vault = new Mock<ICredentialVault>();
        vault.Setup(v => v.FindForEnvironmentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((CredentialProfile?)null);
        services.AddScoped(_ => vault.Object);
        services.Replace(ServiceDescriptor.Singleton<IJiraAccessProvider, HostedJiraAccessProvider>());
        services.AddBugInvestigationHostedMode();

        services.AddScoped<ISkill>(sp => new FakeSkill(SkillIds.Jira, async r =>
        {
            _analysisRuns.AddOrUpdate(r.Context.InvestigationId, 1, (_, n) => n + 1);
            var access = await sp.GetRequiredService<IJiraAccessProvider>().GetReadAccessAsync(r.Context);
            _reads.Add(new JiraRead(r.Context.InvestigationId, access.Access!.IsUserDelegated, access.Access.Authorization.Parameter!));
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
            var store = sp.GetRequiredService<ISessionCredentialStore>();
            var mode = r.Inputs?["mode"] ?? "login";
            if (mode == "recheck")
            {
                var code = store.Take(ctx.InvestigationId, AgentOrchestrator.MfaCredentialKey(ctx.SelectedEnvironmentId!));
                _logins.Add(new LoginAttempt(ctx.InvestigationId, ctx.SelectedEnvironmentId, "", code?.Password ?? "", mode));
                ctx.AuthenticationStatus = code?.Password == "424242" ? AuthenticationStatus.Succeeded : AuthenticationStatus.MfaRequired;
                ctx.AuthenticationMessage = "Enter the verification code.";
                return Task.CompletedTask;
            }

            var secret = store.Take(ctx.InvestigationId, ctx.SelectedEnvironmentId!);
            if (secret is null)
            {
                ctx.AuthenticationStatus = AuthenticationStatus.CredentialsRequired;
                return Task.CompletedTask;
            }
            _logins.Add(new LoginAttempt(ctx.InvestigationId, ctx.SelectedEnvironmentId, secret.Username, secret.Password!, mode));
            ctx.AuthenticationStatus = secret.Password switch
            {
                "right-password" => AuthenticationStatus.Succeeded,
                "mfa-password" => AuthenticationStatus.MfaRequired,
                _ => AuthenticationStatus.InvalidCredentials
            };
            ctx.AuthenticationMessage = ctx.AuthenticationStatus switch
            {
                AuthenticationStatus.MfaRequired => "Enter the verification code.",
                AuthenticationStatus.InvalidCredentials => "The environment rejected the username or password.",
                _ => "ok"
            };
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
        services.AddSingleton<InvestigationWorkerService>();
        services.AddSingleton<JobRecoveryService>();
        _sp = services.BuildServiceProvider();

        _stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _worker = _sp.GetRequiredService<InvestigationWorkerService>();
        if (startWorker)
            _worker.StartAsync(_stop.Token).GetAwaiter().GetResult();
    }

    [TearDown]
    public void TearDown()
    {
        _stop?.Cancel();
        try { _worker?.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(10)); } catch { }
        _worker?.Dispose();
        _sp?.Dispose();
        _db?.Dispose();
        _jira?.Dispose();
        _stop?.Dispose();
        try { Directory.Delete(_temp, true); } catch { }
    }

    private sealed class Workspace : IWorkspaceDefaults
    {
        public Workspace(string path) => DefaultWorkspacePath = path;
        public string? DefaultWorkspacePath { get; }
    }

    private T S<T>() where T : notnull => _sp.GetRequiredService<T>();

    private async Task<InvestigationJob> CreateJob(string account, string issueKey, string userToken, bool enqueue = true)
    {
        var job = await S<IInvestigationJobStore>().CreateAsync(new InvestigationJob
        {
            TenantId = FitFactory.Installation, CloudId = FitFactory.CloudId, SiteUrl = "https://example-site.atlassian.net",
            IssueId = "100" + issueKey.Split('-')[1], IssueKey = issueKey, InvokingAccountId = account, ContextId = "ctx-" + account,
            ForgeEnvironmentId = "env-1"
        });
        await S<IJobSecretStore>().SetAsync(job.Id, JobSecretKinds.JiraUserToken, userToken, DateTimeOffset.UtcNow.AddHours(1));
        await S<IJobSecretStore>().SetAsync(job.Id, JobSecretKinds.JiraSystemToken, FakeJira.SystemToken, DateTimeOffset.UtcNow.AddHours(1));
        if (enqueue)
            await S<IInvestigationQueue>().EnqueueAsync(job.Id);
        return job;
    }

    private async Task<InvestigationJob> WaitFor(Guid id, Func<InvestigationJob, bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var job = await S<IInvestigationJobStore>().GetAsync(id);
            if (job is not null && condition(job))
                return job;
            await Task.Delay(25);
        }
        var last = await S<IInvestigationJobStore>().GetAsync(id);
        Assert.Fail($"Timed out waiting for {what}. Status={last?.Status} Kind={last?.AwaitingInputKind} Lease={last?.LeaseOwner} Msg={last?.StatusMessage}");
        return null!;
    }

    private static bool Suspended(InvestigationJob j, string kind) =>
        j.Status == InvestigationJobStatus.AwaitingUserInput && j.AwaitingInputKind == kind && j.LeaseOwner is null && j.ContextSnapshotJson is not null;

    /// <summary>Same path as the Forge personal settings page: FIT-verified pending list → one-time ticket → direct POST.</summary>
    private async Task SubmitCredentials(Guid jobId, string account, string env, string user, string password)
    {
        var channel = S<CredentialChannelService>();
        var pending = await channel.ListPendingAsync(new ForgeInvocation { InstallationId = FitFactory.Installation, PrincipalAccountId = account }, CancellationToken.None);
        var item = pending.Single(p => p.JobId == jobId);
        var result = await channel.SubmitCredentialsAsync(jobId, item.Ticket,
            new CredentialSubmission { EnvironmentId = env, Username = user, Password = password }, CancellationToken.None);
        Assert.That(result, Is.EqualTo(SubmissionResult.Accepted));
    }

    private async Task SubmitMfa(Guid jobId, string account, string code)
    {
        var channel = S<CredentialChannelService>();
        var pending = await channel.ListPendingAsync(new ForgeInvocation { InstallationId = FitFactory.Installation, PrincipalAccountId = account }, CancellationToken.None);
        var item = pending.Single(p => p.JobId == jobId);
        Assert.That(await channel.SubmitMfaCodeAsync(jobId, item.Ticket, new MfaSubmission { Code = code }, CancellationToken.None), Is.EqualTo(SubmissionResult.Accepted));
    }

    [Test]
    public async Task Two_developers_one_worker_slot_waiting_releases_the_slot_and_resumes_from_checkpoint()
    {
        Build(slots: 1);
        var alice = await CreateJob("acc-alice", "ABC-1", "tok-alice");
        var bob = await CreateJob("acc-bob", "ABC-2", "tok-bob");

        // With ONE worker slot, Bob can only reach his prompt if Alice's waiting job released the slot.
        await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Environment), "Alice suspended for environment");
        var b = await WaitFor(bob.Id, j => Suspended(j, AwaitingInputKinds.Environment), "Bob suspended for environment");
        Assert.That(b.AwaitingInputPrompt, Does.Contain("KMSRC").And.Contain("KMSQA"));
        Assert.That(b.AwaitingSinceUnixMs, Is.GreaterThan(0));

        await S<JobInputService>().RecordReplyAsync(alice.Id, "KMSQA");
        await S<JobInputService>().RecordReplyAsync(bob.Id, "use KMSRC please");
        var a = await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Credentials), "Alice suspended for credentials");
        b = await WaitFor(bob.Id, j => Suspended(j, AwaitingInputKinds.Credentials), "Bob suspended for credentials");
        Assert.That(a.EnvironmentId, Is.EqualTo("KMSQA"));
        Assert.That(b.EnvironmentId, Is.EqualTo("KMSRC"));

        // Alice: MFA. The worker HOLDS the job (lease kept, bounded) because the sign-in page can't be checkpointed.
        await SubmitCredentials(alice.Id, "acc-alice", "KMSQA", "alice_qa", "mfa-password");
        a = await WaitFor(alice.Id, j => j.AwaitingInputKind == AwaitingInputKinds.MfaCode, "Alice MFA prompt");
        Assert.That(a.LeaseOwner, Is.Not.Null, "MFA keeps the browser page on the worker");
        // Bob mistypes his password — and his job still runs while Alice's MFA is held? No: one slot is busy with
        // Alice's MFA hold, so Bob's credentials wait in the queue until Alice finishes. Proves bounded hold, then:
        await SubmitCredentials(bob.Id, "acc-bob", "KMSRC", "bob_rc", "wrong-password");
        await SubmitMfa(alice.Id, "acc-alice", "424242");

        a = await WaitFor(alice.Id, j => j.IsTerminal, "Alice completion");
        Assert.That(a.Status, Is.EqualTo(InvestigationJobStatus.Completed), a.ErrorDetails);

        b = await WaitFor(bob.Id, j => Suspended(j, AwaitingInputKinds.Credentials) && (j.AwaitingInputPrompt ?? "").Contains("rejected"), "Bob re-prompt after rejection");
        await SubmitCredentials(bob.Id, "acc-bob", "KMSRC", "bob_rc", "right-password");
        b = await WaitFor(bob.Id, j => j.IsTerminal && j.ReportPublishedAt is not null, "Bob completion");
        Assert.That(b.Status, Is.EqualTo(InvestigationJobStatus.Completed), b.ErrorDetails);
        a = (await S<IInvestigationJobStore>().GetAsync(alice.Id))!;

        // Resumes continued from the checkpoint: the ticket was analysed exactly once per job.
        Assert.That(_analysisRuns[alice.Id], Is.EqualTo(1));
        Assert.That(_analysisRuns[bob.Id], Is.EqualTo(1));

        // Identity / credential isolation.
        Assert.That(_reads.Where(r => r.JobId == alice.Id).All(r => r.Delegated && r.Token == "tok-alice"), Is.True);
        Assert.That(_reads.Where(r => r.JobId == bob.Id).All(r => r.Delegated && r.Token == "tok-bob"), Is.True);
        Assert.That(_logins.Where(l => l.JobId == alice.Id && l.Mode == "login").All(l => l.Environment == "KMSQA" && l.Username == "alice_qa"), Is.True);
        Assert.That(_logins.Where(l => l.JobId == bob.Id).All(l => l.Environment == "KMSRC" && l.Username == "bob_rc"), Is.True);
        Assert.That(_logins.Count(l => l.JobId == alice.Id && l.Mode == "recheck" && l.Secret == "424242"), Is.EqualTo(1));
        Assert.That(_logins.Count(l => l.JobId == bob.Id), Is.EqualTo(2), "rejected attempt + retry");

        // Reports: honest, separate, one per issue, published as the app.
        var reportA = InvestigationReport.FromJson(a.ReportJson)!;
        Assert.That(reportA.EnvironmentId, Is.EqualTo("KMSQA"));
        Assert.That(reportA.Hypotheses.Any(h => h.Contains("Probable root cause")), Is.True);
        Assert.That(InvestigationReport.FromJson(b.ReportJson)!.EnvironmentId, Is.EqualTo("KMSRC"));
        Assert.That(_jira.PostedComments, Has.Count.EqualTo(2));

        // Nothing secret persisted; checkpoints removed once finished.
        foreach (var id in new[] { alice.Id, bob.Id })
        {
            var job = (await S<IInvestigationJobStore>().GetAsync(id))!;
            Assert.That(job.ContextSnapshotJson, Is.Null);
            Assert.That(await S<IJobSecretStore>().GetAsync(id, JobSecretKinds.JiraUserToken), Is.Null);
            var events = string.Join("\n", (await S<IInvestigationJobStore>().ListEventsAsync(id, 500)).Select(e => e.Message));
            var audit = string.Join("\n", (await S<IAuditLog>().ListForJobAsync(id)).Select(x => x.Detail));
            var persisted = events + audit + job.ReportJson + job.ResultSummary;
            foreach (var secret in new[] { "right-password", "wrong-password", "mfa-password", "424242", "tok-alice", "tok-bob", FakeJira.SystemToken })
                Assert.That(persisted, Does.Not.Contain(secret), $"secret '{secret}' leaked into persisted job data");
        }
    }

    [Test]
    public async Task Checkpoints_never_contain_credentials_or_tokens()
    {
        Build(slots: 2);
        var alice = await CreateJob("acc-alice", "ABC-1", "tok-alice");
        await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Environment), "suspended");
        await S<JobInputService>().RecordReplyAsync(alice.Id, "KMSQA");
        await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Credentials), "suspended for credentials");
        await SubmitCredentials(alice.Id, "acc-alice", "KMSQA", "alice_qa", "wrong-password");
        var job = await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Credentials) && (j.AwaitingInputPrompt ?? "").Contains("rejected"), "re-prompt");

        Assert.That(job.ContextSnapshotJson, Does.Not.Contain("wrong-password").And.Not.Contain("tok-alice").And.Not.Contain(FakeJira.SystemToken));
        var restored = RuntimeContextSnapshot.Restore(job.ContextSnapshotJson!, job.Id);
        Assert.That(restored.SelectedEnvironmentId, Is.EqualTo("KMSQA"));
        Assert.That(restored.Summary, Is.EqualTo("Save button throws an error"));
    }

    [Test]
    public async Task Unanswered_mfa_is_held_only_briefly_then_suspended_and_requires_a_fresh_sign_in()
    {
        Build(slots: 1, mfaHoldMinutes: 0);
        var alice = await CreateJob("acc-alice", "ABC-1", "tok-alice");
        await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Environment), "suspended");
        await S<JobInputService>().RecordReplyAsync(alice.Id, "KMSQA");
        await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Credentials), "suspended for credentials");
        await SubmitCredentials(alice.Id, "acc-alice", "KMSQA", "alice_qa", "mfa-password");

        var job = await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Credentials) && (j.AwaitingInputPrompt ?? "").Contains("verification step was not completed"), "MFA hold expired");
        Assert.That(job.LeaseOwner, Is.Null, "slot released after the bounded MFA hold");
    }

    [Test]
    public async Task Cancellation_of_a_suspended_job_is_immediate_and_does_not_affect_another_user()
    {
        Build(slots: 1);
        var alice = await CreateJob("acc-alice", "ABC-1", "tok-alice");
        var bob = await CreateJob("acc-bob", "ABC-2", "tok-bob");
        await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Environment), "Alice suspended");
        await WaitFor(bob.Id, j => Suspended(j, AwaitingInputKinds.Environment), "Bob suspended");

        var cancelled = await S<JobInputService>().CancelAsync(alice.Id);
        Assert.That(cancelled!.Status, Is.EqualTo(InvestigationJobStatus.Cancelled));
        Assert.That(cancelled.ContextSnapshotJson, Is.Null);
        Assert.That(await S<JobInputService>().WakeAsync(alice.Id), Is.False, "a cancelled job never resumes");

        await S<JobInputService>().RecordReplyAsync(bob.Id, "KMSRC");
        await WaitFor(bob.Id, j => Suspended(j, AwaitingInputKinds.Credentials), "Bob continues independently");
        Assert.That(_jira.PostedComments, Is.Empty);
    }

    [Test]
    public async Task Crashed_worker_is_recovered_resumed_from_checkpoint_and_reports_once()
    {
        Build(slots: 1, startWorker: false);
        var alice = await CreateJob("acc-alice", "ABC-1", "tok-alice", enqueue: false);

        // Phase 1: run until the job suspends (checkpoint written), then simulate a crash in the next run.
        var runner = S<InvestigationJobRunner>();
        Assert.That(await runner.RunAsync(alice.Id, "worker-A", _stop.Token), Is.EqualTo(JobRunOutcome.Suspended));
        await S<IInvestigationJobStore>().UpdateAsync(alice.Id, j =>
        {
            j.EnvironmentId = "KMSQA";
            j.Status = InvestigationJobStatus.Running;          // worker-B picked it up ...
            j.LeaseOwner = "worker-B-crashed";                  // ... and died without releasing its lease
            j.LeaseExpiresUnixMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
        });
        await S<IJobSecretStore>().SetAsync(alice.Id, JobSecretKinds.EnvironmentCredentials("KMSQA"),
            System.Text.Json.JsonSerializer.Serialize(new SubmittedCredentials { Username = "alice_qa", Password = "right-password" }),
            DateTimeOffset.UtcNow.AddMinutes(5));

        // Phase 2: the sweeper detects the orphan and re-queues it; a healthy worker resumes from the checkpoint.
        await S<JobRecoveryService>().SweepOnceAsync(CancellationToken.None);
        var requeued = (await S<IInvestigationJobStore>().GetAsync(alice.Id))!;
        Assert.That(requeued.Status, Is.EqualTo(InvestigationJobStatus.Queued));
        Assert.That(requeued.RetryCount, Is.EqualTo(1));
        await _worker.StartAsync(_stop.Token);

        var done = await WaitFor(alice.Id, j => j.IsTerminal && j.ReportPublishedAt is not null, "recovered completion");
        Assert.That(done.Status, Is.EqualTo(InvestigationJobStatus.Completed), done.ErrorDetails);
        Assert.That(_analysisRuns[alice.Id], Is.EqualTo(1), "recovery resumed from the checkpoint, no re-analysis");

        // A late duplicate queue message and another sweep must not produce a second report.
        await S<IInvestigationQueue>().EnqueueAsync(alice.Id);
        await S<JobRecoveryService>().SweepOnceAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.That(_jira.PostedComments, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Duplicate_delivery_to_the_same_worker_never_runs_a_job_twice_concurrently()
    {
        Build(slots: 2, startWorker: false);
        var alice = await CreateJob("acc-alice", "ABC-1", "tok-alice", enqueue: false);
        var runner = S<InvestigationJobRunner>();

        var outcomes = await Task.WhenAll(
            runner.RunAsync(alice.Id, "same-worker", _stop.Token),
            runner.RunAsync(alice.Id, "same-worker", _stop.Token));

        Assert.That(outcomes, Is.EquivalentTo(new[] { JobRunOutcome.NotLeased, JobRunOutcome.Suspended }));
        Assert.That(_analysisRuns[alice.Id], Is.EqualTo(1));
    }

    [Test]
    public async Task Suspended_job_without_answer_times_out_via_the_sweeper()
    {
        Build(slots: 1);
        var alice = await CreateJob("acc-alice", "ABC-1", "tok-alice");
        await WaitFor(alice.Id, j => Suspended(j, AwaitingInputKinds.Environment), "suspended");
        await S<IInvestigationJobStore>().UpdateAsync(alice.Id, j => j.AwaitingSinceUnixMs = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds());

        await S<JobRecoveryService>().SweepOnceAsync(CancellationToken.None);

        var job = (await S<IInvestigationJobStore>().GetAsync(alice.Id))!;
        Assert.That(job.Status, Is.EqualTo(InvestigationJobStatus.TimedOut));
        Assert.That(job.StatusMessage, Does.Contain("No response"));
        Assert.That(await S<IJobSecretStore>().GetAsync(alice.Id, JobSecretKinds.JiraUserToken), Is.Null);
    }
}
