using BugInvestigationAgent.Core.Jobs;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Api.Tests;

public class DatabaseMigratorTests
{
    private string _file = null!;

    [SetUp]
    public void SetUp() => _file = Path.Combine(Path.GetTempPath(), $"bia-mig-{Guid.NewGuid():N}.db");

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch { }
    }

    private AgentDbContext Db() => new(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite($"Data Source={_file};Default Timeout=60").Options);

    [Test]
    public async Task Fresh_database_gets_all_versions_once_and_reruns_are_no_ops()
    {
        await using (var db = Db())
            Assert.That(await DatabaseMigrator.MigrateAsync(db), Is.EqualTo(new[] { 1, 2, 3 }));
        await using (var db = Db())
        {
            Assert.That(await DatabaseMigrator.MigrateAsync(db), Is.Empty);
            Assert.That(await DatabaseMigrator.AppliedVersionsAsync(db), Is.EqualTo(new[] { 1, 2, 3 }));
            db.InvestigationJobs.Add(new InvestigationJob { TenantId = "t", CloudId = "c", IssueId = "1", IssueKey = "A-1", InvokingAccountId = "a", ContextSnapshotJson = "{}" });
            await db.SaveChangesAsync();
        }
    }

    [Test]
    public async Task Concurrent_startup_applies_each_migration_exactly_once()
    {
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await using var db = Db();
            return await DatabaseMigrator.MigrateAsync(db);
        }));
        var results = await Task.WhenAll(tasks);

        Assert.That(results.SelectMany(r => r).OrderBy(v => v), Is.EqualTo(new[] { 1, 2, 3 }), "each version applied by exactly one starter");
        await using var check = Db();
        Assert.That(await DatabaseMigrator.AppliedVersionsAsync(check), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task Pre_migration_database_is_adopted_and_upgraded_without_data_loss()
    {
        await using (var conn = new SqliteConnection($"Data Source={_file}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "CredentialProfiles" ("Id" TEXT NOT NULL PRIMARY KEY, "Label" TEXT NOT NULL, "SystemName" TEXT NULL,
                  "EnvironmentName" TEXT NULL, "Username" TEXT NULL, "Role" TEXT NULL, "AuthType" TEXT NOT NULL,
                  "EncryptedSecret" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
                INSERT INTO "CredentialProfiles" VALUES ('8b2c6c1e-2c3a-4a9b-9f00-000000000001','old',NULL,NULL,'u',NULL,'password','x','2026-01-01','2026-01-01');
                CREATE TABLE "InvestigationJobs" ("Id" TEXT NOT NULL PRIMARY KEY, "TenantId" TEXT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = Db())
            await DatabaseMigrator.MigrateAsync(db);

        await using var check = Db();
        Assert.That(await check.CredentialProfiles.CountAsync(), Is.EqualTo(1), "existing rows preserved");
        await using var conn2 = new SqliteConnection($"Data Source={_file}");
        await conn2.OpenAsync();
        await using var q = conn2.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM pragma_table_info('CredentialProfiles') WHERE name IN ('OwnerId','EnvironmentId');";
        Assert.That(Convert.ToInt32(await q.ExecuteScalarAsync()), Is.EqualTo(2));
        q.CommandText = "SELECT COUNT(*) FROM pragma_table_info('InvestigationJobs') WHERE name IN ('ContextSnapshotJson','AwaitingSinceUnixMs');";
        Assert.That(Convert.ToInt32(await q.ExecuteScalarAsync()), Is.EqualTo(2));
    }
}
