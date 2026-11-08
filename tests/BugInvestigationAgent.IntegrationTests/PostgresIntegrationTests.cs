using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Core.Security;
using BugInvestigationAgent.Infrastructure.Jobs;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BugInvestigationAgent.IntegrationTests;

/// <summary>REAL PostgreSQL 16 (local server): migrations, advisory-lock concurrency, leases and secret storage.</summary>
[NonParallelizable]
public class PostgresIntegrationTests
{
    private string _cs = null!;

    private sealed class Factory : IDbContextFactory<AgentDbContext>
    {
        private readonly DbContextOptions<AgentDbContext> _options;
        public Factory(string cs) => _options = new DbContextOptionsBuilder<AgentDbContext>().UseNpgsql(cs).Options;
        public AgentDbContext CreateDbContext() => new(_options);
    }

    [SetUp]
    public async Task SetUp() => _cs = await Postgres.CreateDatabaseAsync();

    [TearDown]
    public async Task TearDown() => await Postgres.DropDatabaseAsync(_cs);

    [Test]
    public async Task Concurrent_replica_startup_applies_each_migration_exactly_once_under_the_advisory_lock()
    {
        var factory = new Factory(_cs);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = factory.CreateDbContext();
            return await DatabaseMigrator.MigrateAsync(db);
        })));

        Assert.That(results.SelectMany(r => r).OrderBy(v => v), Is.EqualTo(new[] { 1, 2, 3 }));
        await using var check = factory.CreateDbContext();
        Assert.That(await DatabaseMigrator.AppliedVersionsAsync(check), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(await DatabaseMigrator.MigrateAsync(check), Is.Empty, "re-running is a no-op");

        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'InvestigationJobs' AND column_name IN ('ContextSnapshotJson','AwaitingSinceUnixMs','LeaseOwner','Version')", conn);
        Assert.That(Convert.ToInt32(await cmd.ExecuteScalarAsync()), Is.EqualTo(4));
    }

    [Test]
    public async Task Job_leases_queries_and_encrypted_secrets_work_on_postgres()
    {
        var factory = new Factory(_cs);
        await using (var db = factory.CreateDbContext())
            await DatabaseMigrator.MigrateAsync(db);
        var jobs = new EfInvestigationJobStore(factory, new SecretRedactor());
        var secrets = new DataProtectionJobSecretStore(factory, new EphemeralDataProtectionProvider());

        var job = await jobs.CreateAsync(new InvestigationJob { TenantId = "t", CloudId = "c", IssueId = "1", IssueKey = "ABC-1", InvokingAccountId = "acc-a", ContextId = "ctx" });
        Assert.That(await jobs.ListRunnableAsync(10), Does.Contain(job.Id));

        // 16 concurrent workers race for the job: exactly one wins (atomic conditional UPDATE).
        var wins = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => jobs.TryAcquireLeaseAsync(job.Id, $"w{i}/{Guid.NewGuid():N}", TimeSpan.FromMinutes(1))));
        Assert.That(wins.Count(w => w), Is.EqualTo(1));

        // Optimistic concurrency: parallel updates all land (retry on version conflict).
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => jobs.UpdateAsync(job.Id, j => j.RetryCount++)));
        Assert.That((await jobs.GetAsync(job.Id))!.RetryCount, Is.EqualTo(10));

        await jobs.UpdateAsync(job.Id, j =>
        {
            j.Status = InvestigationJobStatus.AwaitingUserInput;
            j.LeaseOwner = null;
            j.AwaitingSinceUnixMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        });
        Assert.That(await jobs.ListExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds(), 0, 10), Does.Contain(job.Id));
        Assert.That((await jobs.FindByContextAsync("t", "ctx", "acc-b")), Is.Null, "user isolation in the query");

        await secrets.SetAsync(job.Id, JobSecretKinds.JiraUserToken, "user-token-xyz", DateTimeOffset.UtcNow.AddMinutes(5));
        await using (var db = factory.CreateDbContext())
            Assert.That(await db.JobSecrets.AnyAsync(s => s.ProtectedValue.Contains("user-token-xyz")), Is.False, "encrypted at rest");
        Assert.That(await secrets.TakeAsync(job.Id, JobSecretKinds.JiraUserToken), Is.EqualTo("user-token-xyz"));
        Assert.That(await secrets.TakeAsync(job.Id, JobSecretKinds.JiraUserToken), Is.Null);
    }
}
