using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Persistence;

/// <summary>
/// Versioned, repeatable schema migrations for SQLite (local) and PostgreSQL (hosted).
/// Applied versions are recorded in <c>__SchemaVersion</c>; each migration runs in its own transaction and is
/// skipped if already applied. Concurrent starts (several API/worker replicas) are serialized with a PostgreSQL
/// advisory lock, or an IMMEDIATE transaction on SQLite, so a migration is applied exactly once.
/// Rule for new migrations: append a new version — never edit an applied one.
/// </summary>
public static class DatabaseMigrator
{
    private const long AdvisoryLockKey = 0x42_49_41_4D_49_47; // "BIAMIG"

    public sealed record Migration(int Version, string Name, Func<MigrationContext, CancellationToken, Task> Apply);

    public sealed class MigrationContext
    {
        public required AgentDbContext Db { get; init; }
        public required DbConnection Connection { get; init; }
        public required DbTransaction Transaction { get; init; }
        public required bool IsSqlite { get; init; }

        public async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var cmd = Connection.CreateCommand();
            cmd.Transaction = Transaction;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<bool> TableExistsAsync(string table, CancellationToken ct)
        {
            await using var cmd = Connection.CreateCommand();
            cmd.Transaction = Transaction;
            cmd.CommandText = IsSqlite
                ? $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"
                : $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{table}';";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
        }

        public async Task AddColumnIfMissingAsync(string table, string column, string sqliteType, string postgresType, CancellationToken ct)
        {
            if (!await TableExistsAsync(table, ct))
                return;
            await using (var cmd = Connection.CreateCommand())
            {
                cmd.Transaction = Transaction;
                cmd.CommandText = IsSqlite
                    ? $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';"
                    : $"SELECT COUNT(*) FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}';";
                if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0)
                    return;
            }
            await ExecuteAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {(IsSqlite ? sqliteType : postgresType)};", ct);
        }
    }

    public static IReadOnlyList<Migration> Migrations { get; } =
    [
        // 1: every table/index of the model, created only if missing (also adopts pre-migration databases).
        new(1, "baseline", async (m, ct) =>
        {
            foreach (var statement in SchemaUpgrader.SplitStatements(m.Db.Database.GenerateCreateScript()))
            {
                var sql = SchemaUpgrader.MakeIdempotent(statement);
                if (sql is null)
                    continue;
                // Indexes on columns added by later migrations are created there.
                if (sql.Contains("CREATE INDEX IF NOT EXISTS \"IX_CredentialProfiles_OwnerId_EnvironmentId\"", StringComparison.Ordinal))
                    continue;
                await m.ExecuteAsync(sql, ct);
            }
        }),
        // 2: per-user / per-environment credential isolation.
        new(2, "credential-owner-environment", async (m, ct) =>
        {
            await m.AddColumnIfMissingAsync("CredentialProfiles", "OwnerId", "TEXT NULL", "text NULL", ct);
            await m.AddColumnIfMissingAsync("CredentialProfiles", "EnvironmentId", "TEXT NULL", "text NULL", ct);
            await m.ExecuteAsync("CREATE INDEX IF NOT EXISTS \"IX_CredentialProfiles_OwnerId_EnvironmentId\" ON \"CredentialProfiles\" (\"OwnerId\", \"EnvironmentId\");", ct);
        }),
        // 3: suspend/resume of jobs awaiting user input (worker slot released).
        new(3, "job-suspend-resume", async (m, ct) =>
        {
            await m.AddColumnIfMissingAsync("InvestigationJobs", "ContextSnapshotJson", "TEXT NULL", "text NULL", ct);
            await m.AddColumnIfMissingAsync("InvestigationJobs", "AwaitingSinceUnixMs", "INTEGER NOT NULL DEFAULT 0", "bigint NOT NULL DEFAULT 0", ct);
        }),
        // 4: durable investigation records (report, Jira comment draft, review + publication audit) and legacy batches.
        new(4, "investigation-records-and-batches", async (m, ct) =>
        {
            string[] tables = ["InvestigationRecords", "LegacyBatches", "LegacyBatchItems"];
            foreach (var statement in SchemaUpgrader.SplitStatements(m.Db.Database.GenerateCreateScript()))
            {
                var sql = SchemaUpgrader.MakeIdempotent(statement);
                if (sql is not null && tables.Any(t => sql.Contains($"\"{t}\"", StringComparison.Ordinal)))
                    await m.ExecuteAsync(sql, ct);
            }
        }),
        // 5: version-aware investigation (deployed KMS version, source branch, created local branch, recommended fix).
        new(5, "investigation-version-and-branch", async (m, ct) =>
        {
            foreach (var column in new[] { "KmsVersion", "SourceBranch", "CreatedBranch", "BranchNote", "ProposedFix", "AffectedFilesCsv" })
                await m.AddColumnIfMissingAsync("InvestigationRecords", column, "TEXT NULL", "text NULL", ct);
        }),
        // 6: Bug/Story, the four Bug verdicts, evaluation (duration, evidence, developer feedback), reopen audit, fix task.
        new(6, "verdict-evaluation-feedback", async (m, ct) =>
        {
            await m.AddColumnIfMissingAsync("InvestigationRecords", "IssueType", "TEXT NOT NULL DEFAULT 'Bug'", "varchar(32) NOT NULL DEFAULT 'Bug'", ct);
            await m.AddColumnIfMissingAsync("InvestigationRecords", "Verdict", "INTEGER NOT NULL DEFAULT 0", "integer NOT NULL DEFAULT 0", ct);
            await m.AddColumnIfMissingAsync("InvestigationRecords", "EvidenceSufficiency", "INTEGER NOT NULL DEFAULT 0", "integer NOT NULL DEFAULT 0", ct);
            await m.AddColumnIfMissingAsync("InvestigationRecords", "DurationSeconds", "REAL NULL", "double precision NULL", ct);
            foreach (var column in new[] { "Feedback", "FeedbackNote", "FeedbackBy", "FeedbackAt", "ReopenResult", "ReopenedBy", "ReopenedAt", "FixTaskPath" })
                await m.AddColumnIfMissingAsync("InvestigationRecords", column, "TEXT NULL", "text NULL", ct);
        })
    ];

    public static async Task<IReadOnlyList<int>> MigrateAsync(AgentDbContext db, CancellationToken cancellationToken = default)
    {
        var isSqlite = db.Database.IsSqlite();
        if (!isSqlite && !db.Database.IsNpgsql())
            throw new NotSupportedException("Only SQLite and PostgreSQL are supported.");

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);

        var applied = new List<int>();
        try
        {
            if (isSqlite)
            {
                await RunAsync(connection, "PRAGMA busy_timeout = 30000;", null, cancellationToken);
                // WAL: readers never block the writer — API and worker can share the local database file.
                if (!connection.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase) &&
                    !connection.ConnectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase))
                    await RunAsync(connection, "PRAGMA journal_mode = WAL;", null, cancellationToken);
            }
            else
                await RunAsync(connection, $"SELECT pg_advisory_lock({AdvisoryLockKey});", null, cancellationToken);

            await RunAsync(connection,
                "CREATE TABLE IF NOT EXISTS \"__SchemaVersion\" (\"Version\" INTEGER NOT NULL PRIMARY KEY, \"Name\" TEXT NOT NULL, \"AppliedAtUtc\" TEXT NOT NULL);",
                null, cancellationToken);

            foreach (var migration in Migrations.OrderBy(m => m.Version))
            {
                // SQLite: BEGIN IMMEDIATE takes the write lock up-front, serializing concurrent migrators.
                await using var tx = isSqlite
                    ? await BeginImmediateAsync(connection, cancellationToken)
                    : await connection.BeginTransactionAsync(cancellationToken);
                if (await IsAppliedAsync(connection, tx, migration.Version, cancellationToken))
                {
                    await tx.CommitAsync(cancellationToken);
                    continue;
                }

                await db.Database.UseTransactionAsync(tx, cancellationToken);
                await migration.Apply(new MigrationContext { Db = db, Connection = connection, Transaction = tx, IsSqlite = isSqlite }, cancellationToken);
                await using (var record = connection.CreateCommand())
                {
                    record.Transaction = tx;
                    record.CommandText = "INSERT INTO \"__SchemaVersion\" (\"Version\", \"Name\", \"AppliedAtUtc\") VALUES (@v, @n, @t);";
                    AddParam(record, "@v", migration.Version);
                    AddParam(record, "@n", migration.Name);
                    AddParam(record, "@t", DateTimeOffset.UtcNow.ToString("O"));
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }
                await tx.CommitAsync(cancellationToken);
                await db.Database.UseTransactionAsync(null, cancellationToken);
                applied.Add(migration.Version);
            }
        }
        finally
        {
            if (!isSqlite)
            {
                try { await RunAsync(connection, $"SELECT pg_advisory_unlock({AdvisoryLockKey});", null, CancellationToken.None); } catch { /* released with the session */ }
            }
            if (openedHere)
                await connection.CloseAsync();
        }

        return applied;
    }

    public static async Task<IReadOnlyList<int>> AppliedVersionsAsync(AgentDbContext db, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT \"Version\" FROM \"__SchemaVersion\" ORDER BY \"Version\";";
            var versions = new List<int>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                versions.Add(Convert.ToInt32(reader.GetValue(0)));
            return versions;
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    private static async Task<DbTransaction> BeginImmediateAsync(DbConnection connection, CancellationToken ct)
    {
        // Microsoft.Data.Sqlite: deferred=false => BEGIN IMMEDIATE.
        if (connection is Microsoft.Data.Sqlite.SqliteConnection sqlite)
            return sqlite.BeginTransaction(deferred: false);
        return await connection.BeginTransactionAsync(ct);
    }

    private static async Task<bool> IsAppliedAsync(DbConnection connection, DbTransaction tx, int version, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM \"__SchemaVersion\" WHERE \"Version\" = @v;";
        AddParam(cmd, "@v", version);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task RunAsync(DbConnection connection, string sql, DbTransaction? tx, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
