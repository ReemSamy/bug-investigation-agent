using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using BugInvestigationAgent.Core.Authentication;
using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Reporting;
using BugInvestigationAgent.Core.Repositories;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Core.Workflow;
using BugInvestigationAgent.Infrastructure.Forge;
using BugInvestigationAgent.Infrastructure.Hosting;
using BugInvestigationAgent.Infrastructure.Jobs;
using BugInvestigationAgent.Infrastructure.Persistence;
using BugInvestigationAgent.Infrastructure.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BugInvestigationAgent.Api.Tests;

/// <summary>SQLite file database per test — same code paths as PostgreSQL in production (EF Core).</summary>
public sealed class TestDb : IDbContextFactory<AgentDbContext>, IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly DbContextOptions<AgentDbContext> _options;

    public TestDb()
    {
        var name = "bia-" + Guid.NewGuid().ToString("N");
        var cs = $"Data Source={name};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(cs);
        _keepAlive.Open();
        _options = new DbContextOptionsBuilder<AgentDbContext>().UseSqlite(cs).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public AgentDbContext CreateDbContext() => new(_options);
    public void Dispose() => _keepAlive.Dispose();
}

public class JobStoreAndLeaseTests
{
    private TestDb _db = null!;
    private EfInvestigationJobStore _jobs = null!;
    private DataProtectionJobSecretStore _secrets = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDb();
        _jobs = new EfInvestigationJobStore(_db, new SecretRedactor());
        _secrets = new DataProtectionJobSecretStore(_db, new EphemeralDataProtectionProvider());
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private Task<InvestigationJob> NewJob(string account = "acc-a") => _jobs.CreateAsync(new InvestigationJob
    {
        TenantId = "t1", CloudId = "c1", IssueId = "1", IssueKey = "ABC-1", InvokingAccountId = account, ContextId = "ctx-" + account
    });

    [Test]
    public async Task Only_one_worker_can_hold_a_job_lease()
    {
        var job = await NewJob();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => _jobs.TryAcquireLeaseAsync(job.Id, "w" + i, TimeSpan.FromMinutes(1))));
        Assert.That(results.Count(r => r), Is.EqualTo(1));
    }

    [Test]
    public async Task Expired_lease_is_recoverable_and_reported_as_orphaned()
    {
        var job = await NewJob();
        Assert.That(await _jobs.TryAcquireLeaseAsync(job.Id, "w1", TimeSpan.FromMilliseconds(1)), Is.True);
        await _jobs.UpdateAsync(job.Id, j => j.Status = InvestigationJobStatus.Running);
        await Task.Delay(20);

        Assert.That(await _jobs.ListOrphanedAsync(10), Does.Contain(job.Id));
        Assert.That(await _jobs.TryAcquireLeaseAsync(job.Id, "w2", TimeSpan.FromMinutes(1)), Is.True);
        Assert.That(await _jobs.RenewLeaseAsync(job.Id, "w1", TimeSpan.FromMinutes(1)), Is.False, "the crashed worker lost its lease");
    }

    [Test]
    public async Task Terminal_jobs_cannot_be_leased_or_listed_as_runnable()
    {
        var job = await NewJob();
        await _jobs.UpdateAsync(job.Id, j => j.Status = InvestigationJobStatus.Completed);
        Assert.That(await _jobs.TryAcquireLeaseAsync(job.Id, "w1", TimeSpan.FromMinutes(1)), Is.False);
        Assert.That(await _jobs.ListRunnableAsync(10), Does.Not.Contain(job.Id));
    }

    [Test]
    public async Task Conversation_lookup_is_scoped_to_tenant_and_user()
    {
        var job = await NewJob("acc-a");
        Assert.That((await _jobs.FindByContextAsync("t1", "ctx-acc-a", "acc-a"))!.Id, Is.EqualTo(job.Id));
        Assert.That(await _jobs.FindByContextAsync("t1", "ctx-acc-a", "acc-b"), Is.Null);
        Assert.That(await _jobs.FindByContextAsync("t2", "ctx-acc-a", "acc-a"), Is.Null);
    }

    [Test]
    public async Task Job_secrets_are_encrypted_purpose_bound_single_use_and_expire()
    {
        var a = await NewJob("acc-a");
        var b = await NewJob("acc-b");
        await _secrets.SetAsync(a.Id, "k", "secret-A", DateTimeOffset.UtcNow.AddMinutes(5));

        await using (var db = _db.CreateDbContext())
        {
            var row = await db.JobSecrets.SingleAsync(s => s.JobId == a.Id);
            Assert.That(row.ProtectedValue, Does.Not.Contain("secret-A"));
            // Copy A's ciphertext under B: cannot be decrypted (purpose = job + kind).
            db.JobSecrets.Add(new JobSecretEntity { JobId = b.Id, Kind = "k", ProtectedValue = row.ProtectedValue, ExpiresUnixMs = row.ExpiresUnixMs });
            await db.SaveChangesAsync();
        }
        Assert.That(await _secrets.GetAsync(b.Id, "k"), Is.Null);

        Assert.That(await _secrets.TakeAsync(a.Id, "k"), Is.EqualTo("secret-A"));
        Assert.That(await _secrets.TakeAsync(a.Id, "k"), Is.Null, "single use");

        await _secrets.SetAsync(a.Id, "old", "x", DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.That(await _secrets.GetAsync(a.Id, "old"), Is.Null, "expired values are never returned");
        await _secrets.SetAsync(a.Id, JobSecretKinds.JiraSystemToken, "sys", DateTimeOffset.UtcNow.AddMinutes(5));
        await _secrets.SetAsync(a.Id, JobSecretKinds.JiraUserToken, "usr", DateTimeOffset.UtcNow.AddMinutes(5));
        await _secrets.DeleteAllAsync(a.Id, [JobSecretKinds.JiraSystemToken]);
        Assert.That(await _secrets.GetAsync(a.Id, JobSecretKinds.JiraUserToken), Is.Null);
        Assert.That(await _secrets.GetAsync(a.Id, JobSecretKinds.JiraSystemToken), Is.EqualTo("sys"));
    }

    [Test]
    public async Task Schema_upgrade_creates_new_tables_in_an_existing_database()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE "CredentialProfiles" ("Id" TEXT NOT NULL PRIMARY KEY, "Label" TEXT NOT NULL, "SystemName" TEXT NULL,
                  "EnvironmentName" TEXT NULL, "Username" TEXT NULL, "Role" TEXT NULL, "AuthType" TEXT NOT NULL,
                  "EncryptedSecret" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        await using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite(conn).Options);
        await SchemaUpgrader.UpgradeAsync(db);
        await SchemaUpgrader.UpgradeAsync(db);

        db.InvestigationJobs.Add(new InvestigationJob { TenantId = "t", CloudId = "c", IssueId = "1", IssueKey = "A-1", InvokingAccountId = "a" });
        db.AuditEntries.Add(new AuditEntry { Action = "x" });
        await db.SaveChangesAsync();
        Assert.That(await db.InvestigationJobs.CountAsync(), Is.EqualTo(1));
    }
}

public class InvestigationReportTests
{
    private static RuntimeContext Context(ReproductionOutcome outcome, bool screenshots)
    {
        var ctx = new RuntimeContext
        {
            JiraTicketKey = "ABC-1",
            SelectedEnvironmentId = "KMSQA",
            SelectedEnvironmentDisplayName = "KMSQA",
            AuthenticationStatus = AuthenticationStatus.Succeeded,
            ReproductionOutcome = outcome,
            ReproductionNotes = "Observed the error toast.",
            RootCause = "Null check missing in SaveHandler.",
            ProposedFixSummary = "Guard the null value."
        };
        if (screenshots)
            ctx.Evidence.Add(EvidenceItem.For(ctx, "screenshot", "C:/x/ABC-1_KMSQA_repro-final-state.png", "Observed failure", EvidenceCategories.FailureState));
        return ctx;
    }

    [Test]
    public void Reproduction_is_confirmed_only_with_evidence_and_root_cause_stays_a_hypothesis()
    {
        var withEvidence = InvestigationReportBuilder.Build(Context(ReproductionOutcome.Reproduced, true), new SecretRedactor());
        Assert.That(withEvidence.ConfirmedFindings.Any(f => f.StartsWith("Bug reproduced")), Is.True);
        Assert.That(withEvidence.Hypotheses.Any(h => h.Contains("Probable root cause")), Is.True);
        Assert.That(withEvidence.ConfirmedFindings.Any(f => f.Contains("Root cause")), Is.False);

        var noEvidence = InvestigationReportBuilder.Build(Context(ReproductionOutcome.Reproduced, false), new SecretRedactor());
        Assert.That(noEvidence.ConfirmedFindings.Any(f => f.StartsWith("Bug reproduced")), Is.False);
        Assert.That(noEvidence.Hypotheses.Any(h => h.Contains("unconfirmed")), Is.True);
    }

    [Test]
    public void Report_is_redacted_and_renders_markdown_and_adf()
    {
        var redactor = new SecretRedactor();
        redactor.RegisterSecret("Hunter2!secret");
        var ctx = Context(ReproductionOutcome.NotReproduced, true);
        ctx.ReproductionNotes = "Logged in with Hunter2!secret and saw nothing.";
        var report = InvestigationReportBuilder.Build(ctx, redactor, "org/proj/repo@main");

        var md = InvestigationReportBuilder.ToMarkdown(report);
        var adf = InvestigationReportBuilder.ToAdf(report).ToJsonString();
        Assert.That(md, Does.Contain("Confirmed findings").And.Contain("Hypotheses").And.Contain("<details>"));
        Assert.That(md + adf + report.ToJson(), Does.Not.Contain("Hunter2!secret"));
        Assert.That(adf, Does.Contain("\"type\":\"doc\""));
        Assert.That(InvestigationReport.FromJson(report.ToJson())!.EnvironmentId, Is.EqualTo("KMSQA"));
    }
}

public class ReportPublisherTests
{
    [Test]
    public async Task Publishing_is_idempotent_across_retries_and_never_duplicates_the_comment()
    {
        using var testDb = new TestDb();
        var jobs = new EfInvestigationJobStore(testDb, new SecretRedactor());
        var secrets = new DataProtectionJobSecretStore(testDb, new EphemeralDataProtectionProvider());
        var fake = new FakeJira();
        var http = new Mock<IHttpClientFactory>();
        http.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(fake, disposeHandler: false));
        var client = new JiraCloudClient(http.Object, NullLogger<JiraCloudClient>.Instance);
        var evidence = new Mock<IEvidenceStore>();
        evidence.Setup(e => e.OpenReadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        var publisher = new JiraReportPublisher(jobs, secrets, evidence.Object, new EfAuditLog(testDb, new SecretRedactor()), client,
            Options.Create(new ReportPublishingOptions()), NullLogger<JiraReportPublisher>.Instance);

        var ctx = new RuntimeContext { JiraTicketKey = "ABC-1", ReproductionOutcome = ReproductionOutcome.NotReproduced, SelectedEnvironmentDisplayName = "KMSQA" };
        ctx.Evidence.Add(EvidenceItem.For(ctx, "screenshot", "x/shot.png", "s", EvidenceCategories.Workflow));
        var job = await jobs.CreateAsync(new InvestigationJob
        {
            TenantId = "t", CloudId = "c1", IssueId = "1001", IssueKey = "ABC-1", InvokingAccountId = "a",
            Status = InvestigationJobStatus.Completed,
            ReportJson = InvestigationReportBuilder.Build(ctx, new SecretRedactor()).ToJson()
        });

        Assert.That(await publisher.PublishAsync(job.Id), Is.False, "no app token yet — waits instead of failing");
        await secrets.SetAsync(job.Id, JobSecretKinds.JiraSystemToken, FakeJira.SystemToken, DateTimeOffset.UtcNow.AddMinutes(10));

        Assert.That(await publisher.PublishAsync(job.Id), Is.True);
        Assert.That(fake.PostedComments, Has.Count.EqualTo(1));
        Assert.That(fake.PostedAttachments, Has.Count.EqualTo(1));

        // Crash before the job row was updated: the comment property is found and reused.
        await jobs.UpdateAsync(job.Id, j => { j.ReportPublishedAt = null; j.ReportCommentId = null; });
        Assert.That(await publisher.PublishAsync(job.Id), Is.True);
        Assert.That(await publisher.PublishAsync(job.Id), Is.True);
        Assert.That(fake.PostedComments, Has.Count.EqualTo(1), "no duplicate report comments");
        Assert.That(fake.Requests.Where(r => r.StartsWith("POST")).All(r => r.EndsWith("as system")), Is.True,
            "publication is the only app-authorized (system token) operation");
    }
}

public class AzureDevOpsProviderTests
{
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("ado-token-123", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.That(requestContext.Scopes, Does.Contain(AzureDevOpsRepositoryProvider.AzureDevOpsScope));
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class AdoHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo("ado-token-123"));
            var json = request.RequestUri!.Host.StartsWith("almsearch")
                ? """{"count":1,"results":[{"fileName":"SaveHandler.cs","path":"/src/SaveHandler.cs"}]}"""
                : """{"value":[{"commitId":"abcdef1234","comment":"ABC-1 fix save","author":{"name":"Dev"}},{"commitId":"999","comment":"ABC-10 other"},{"commitId":"888","comment":"unrelated"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CapturingRunner : IProcessRunner
    {
        public IReadOnlyList<string>? Args;
        public IReadOnlyDictionary<string, string>? Env;
        public Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Args = arguments;
            Env = environment;
            return Task.FromResult((0, ""));
        }
    }

    private static readonly RepositorySelection Repo = new()
    {
        Provider = "AzureDevOps", Organization = "contoso", Project = "KMS", Repository = "kms-suite", Branch = "release/14"
    };

    private static (AzureDevOpsRepositoryProvider Provider, AdoHandler Handler, CapturingRunner Runner) Create()
    {
        var handler = new AdoHandler();
        var http = new Mock<IHttpClientFactory>();
        http.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var runner = new CapturingRunner();
        return (new AzureDevOpsRepositoryProvider(new FakeCredential(), http.Object, runner,
            Options.Create(new AzureDevOpsOptions()), NullLogger<AzureDevOpsRepositoryProvider>.Instance), handler, runner);
    }

    [Test]
    public async Task Checkout_is_shallow_read_only_and_keeps_the_token_out_of_arguments()
    {
        var (provider, _, runner) = Create();
        await provider.CheckoutAsync(Repo, Path.Combine(Path.GetTempPath(), "bia-test-" + Guid.NewGuid()));

        Assert.That(runner.Args, Does.Contain("clone").And.Contain("--depth").And.Contain("release/14"));
        Assert.That(string.Join(" ", runner.Args!), Does.Not.Contain("ado-token-123"));
        Assert.That(string.Join(" ", runner.Args!), Does.Contain("https://dev.azure.com/contoso/KMS/_git/kms-suite"));
        Assert.That(runner.Env!["GIT_CONFIG_VALUE_0"], Is.EqualTo("AUTHORIZATION: bearer ado-token-123"));
        Assert.That(runner.Args, Does.Not.Contain("push"));
    }

    [Test]
    public async Task Commits_are_correlated_by_exact_issue_key()
    {
        var (provider, _, _) = Create();
        var commits = await provider.FindCommitsMentioningAsync(Repo, "ABC-1");
        Assert.That(commits.Select(c => c.CommitId), Is.EqualTo(new[] { "abcdef1234" }));
    }

    [Test]
    public async Task Code_search_targets_the_mapped_repository_and_branch()
    {
        var (provider, handler, _) = Create();
        var hits = await provider.SearchCodeAsync(Repo, "SaveHandler");
        Assert.That(hits.Single().Path, Is.EqualTo("/src/SaveHandler.cs"));
        var call = handler.Calls.Single();
        Assert.That(call.Url, Does.StartWith("https://almsearch.dev.azure.com/contoso/KMS/_apis/search/codesearchresults"));
        Assert.That(call.Body, Does.Contain("kms-suite").And.Contain("release/14"));
    }

    [Test]
    public void Repository_mapping_prefers_component_then_project()
    {
        var resolver = new ConfiguredRepositoryResolver(new RepositoryMappingOptions
        {
            Mappings =
            {
                new RepositoryMapping { JiraProject = "*", Organization = "o", Project = "p", Repository = "fallback" },
                new RepositoryMapping { JiraProject = "ABC", Organization = "o", Project = "p", Repository = "abc-main" },
                new RepositoryMapping { JiraProject = "ABC", Component = "Mobile", Organization = "o", Project = "p", Repository = "abc-mobile", Branch = "dev" },
                new RepositoryMapping { JiraProject = "XYZ", Organization = "", Project = "p", Repository = "incomplete" }
            }
        });

        Assert.That(resolver.Resolve("ABC-1", ["Mobile"], null)!.Repository, Is.EqualTo("abc-mobile"));
        Assert.That(resolver.Resolve("ABC-1", ["Web"], null)!.Repository, Is.EqualTo("abc-main"));
        Assert.That(resolver.Resolve("XYZ-9", null, null)!.Repository, Is.EqualTo("fallback"), "incomplete mappings are ignored");
    }
}
