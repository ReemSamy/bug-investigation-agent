using BugInvestigationAgent.Core.Credentials;
using BugInvestigationAgent.Infrastructure.Credentials;
using BugInvestigationAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Skills.Tests;

public class CredentialVaultTests
{
    private sealed class SwitchableUser : ICurrentUserAccessor
    {
        public string UserId { get; set; } = "os:corp\\alice|jira:alice@example.test";
    }

    private SqliteConnection _connection = null!;
    private AgentDbContext _db = null!;
    private SwitchableUser _user = null!;
    private DataProtectionCredentialVault _vault = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _user = new SwitchableUser();
        _vault = new DataProtectionCredentialVault(_db, new EphemeralDataProtectionProvider(), _user);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<CredentialProfile> SaveAsync(string env, string user, string password) =>
        _vault.SaveProfileAsync(
            new CredentialProfile { EnvironmentId = env, EnvironmentName = env, Username = user },
            new CredentialSecret { Username = user, Password = password });

    [Test]
    public async Task Secrets_are_encrypted_at_rest()
    {
        var saved = await SaveAsync("KMSQA", "qa_admin", "Pl41nText-Never!");

        var row = await _db.CredentialProfiles.AsNoTracking().SingleAsync(x => x.Id == saved.Id);
        Assert.That(row.EncryptedSecret, Does.Not.Contain("Pl41nText-Never!"));
        Assert.That(row.EncryptedSecret, Does.Not.Contain("qa_admin"));
        Assert.That((await _vault.ResolveSecretAsync(saved.Id))!.Password, Is.EqualTo("Pl41nText-Never!"));
    }

    [Test]
    public async Task Credentials_are_isolated_between_environments()
    {
        await SaveAsync("KMSRC", "rc_admin", "rc-pw");

        Assert.That(await _vault.FindForEnvironmentAsync("KMSQA"), Is.Null);
        Assert.That((await _vault.FindForEnvironmentAsync("kmsrc"))!.Username, Is.EqualTo("rc_admin"));
        Assert.That(await _vault.FindMatchAsync(null, "KMSQA", "rc_admin", null), Is.Null);
    }

    [Test]
    public async Task Credentials_are_isolated_between_users()
    {
        var alices = await SaveAsync("KMSQA", "qa_admin", "alice-pw");

        _user.UserId = "os:corp\\bob|jira:bob@example.test";

        Assert.That(await _vault.ListProfilesAsync(), Is.Empty);
        Assert.That(await _vault.GetProfileAsync(alices.Id), Is.Null);
        Assert.That(await _vault.FindForEnvironmentAsync("KMSQA"), Is.Null);
        Assert.That(await _vault.ResolveSecretAsync(alices.Id), Is.Null);

        // Bob cannot overwrite Alice's profile by reusing its id — a new profile is created for him instead.
        var bobs = await _vault.SaveProfileAsync(
            new CredentialProfile { Id = alices.Id, EnvironmentId = "KMSQA", Username = "bob" },
            new CredentialSecret { Username = "bob", Password = "bob-pw" });
        Assert.That(bobs.Id, Is.Not.EqualTo(alices.Id));

        _user.UserId = "os:corp\\alice|jira:alice@example.test";
        Assert.That((await _vault.ResolveSecretAsync(alices.Id))!.Password, Is.EqualTo("alice-pw"));
    }

    [Test]
    public async Task Ciphertext_copied_to_another_users_row_cannot_be_decrypted()
    {
        var alices = await SaveAsync("KMSQA", "qa_admin", "alice-pw");
        _user.UserId = "bob";
        var bobs = await SaveAsync("KMSQA", "bob", "bob-pw");

        // Simulate tampering: copy Alice's ciphertext into Bob's row.
        var aliceRow = await _db.CredentialProfiles.SingleAsync(x => x.Id == alices.Id);
        var bobRow = await _db.CredentialProfiles.SingleAsync(x => x.Id == bobs.Id);
        bobRow.EncryptedSecret = aliceRow.EncryptedSecret;
        await _db.SaveChangesAsync();

        Assert.That(await _vault.ResolveSecretAsync(bobs.Id), Is.Null);
    }

    [Test]
    public async Task Saving_requires_an_environment()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => _vault.SaveProfileAsync(
            new CredentialProfile { Username = "x" }, new CredentialSecret { Username = "x", Password = "y" }));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Legacy_unscoped_profiles_are_listed_for_cleanup_but_never_resolved()
    {
        _db.CredentialProfiles.Add(new CredentialProfileEntity
        {
            Id = Guid.NewGuid(), Label = "old", Username = "old", EncryptedSecret = "irrelevant",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var listed = (await _vault.ListProfilesAsync()).Single();
        Assert.That(listed.IsLegacyUnscoped, Is.True);
        Assert.That(await _vault.ResolveSecretAsync(listed.Id), Is.Null);
        await _vault.DeleteProfileAsync(listed.Id);
        Assert.That(await _vault.ListProfilesAsync(), Is.Empty);
    }

    [Test]
    public async Task Schema_upgrade_adds_isolation_columns_to_existing_databases()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            // Shape of the table before this change (no OwnerId / EnvironmentId).
            create.CommandText = """
                CREATE TABLE "CredentialProfiles" ("Id" TEXT NOT NULL PRIMARY KEY, "Label" TEXT NOT NULL, "SystemName" TEXT NULL,
                  "EnvironmentName" TEXT NULL, "Username" TEXT NULL, "Role" TEXT NULL, "AuthType" TEXT NOT NULL,
                  "EncryptedSecret" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
                """;
            create.ExecuteNonQuery();
        }

        using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite(conn).Options);
        await SchemaUpgrader.UpgradeAsync(db);
        await SchemaUpgrader.UpgradeAsync(db); // idempotent

        using var info = conn.CreateCommand();
        info.CommandText = "PRAGMA table_info(\"CredentialProfiles\");";
        var columns = new List<string>();
        using (var reader = info.ExecuteReader())
            while (reader.Read())
                columns.Add(reader.GetString(1));

        Assert.That(columns, Does.Contain("OwnerId").And.Contain("EnvironmentId"));
    }
}
