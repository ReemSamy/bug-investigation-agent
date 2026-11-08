using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Persistence;

/// <summary>
/// The app uses EnsureCreated (no EF migrations), which never alters an existing database.
/// This idempotently adds tables and columns introduced after the first release, on SQLite and PostgreSQL.
/// </summary>
public static class SchemaUpgrader
{
    private static readonly (string Table, string Column, string SqliteType, string PostgresType)[] AddedColumns =
    [
        ("CredentialProfiles", "OwnerId", "TEXT NULL", "text NULL"),
        ("CredentialProfiles", "EnvironmentId", "TEXT NULL", "text NULL")
    ];

    public static async Task UpgradeAsync(AgentDbContext db, CancellationToken cancellationToken = default)
    {
        var isSqlite = db.Database.IsSqlite();
        var isPostgres = db.Database.IsNpgsql();
        if (!isSqlite && !isPostgres)
            return;

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);

        try
        {
            // 1) Columns added to existing tables (before replaying indexes that reference them).
            foreach (var group in AddedColumns.GroupBy(c => c.Table))
            {
                var existing = await ListColumnsAsync(connection, group.Key, isSqlite, cancellationToken);
                if (existing.Count == 0)
                    continue; // table does not exist yet — created below with all columns
                foreach (var (table, column, sqliteType, postgresType) in group)
                {
                    if (existing.Contains(column))
                        continue;
                    await ExecuteAsync(connection,
                        $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {(isSqlite ? sqliteType : postgresType)};",
                        cancellationToken);
                }
            }

            // 2) Tables/indexes missing from older databases (CREATE ... IF NOT EXISTS from the current model).
            foreach (var statement in SplitStatements(db.Database.GenerateCreateScript()))
            {
                var sql = MakeIdempotent(statement);
                if (sql is null)
                    continue;
                await ExecuteAsync(connection, sql, cancellationToken);
            }

            await ExecuteAsync(connection,
                "CREATE INDEX IF NOT EXISTS \"IX_CredentialProfiles_OwnerId_EnvironmentId\" ON \"CredentialProfiles\" (\"OwnerId\", \"EnvironmentId\");",
                cancellationToken);
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    internal static IEnumerable<string> SplitStatements(string script) =>
        script.Split(";", StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.StartsWith("--", StringComparison.Ordinal));

    internal static string? MakeIdempotent(string statement)
    {
        var s = statement.TrimStart();
        if (s.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase) && !s.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase))
            return "CREATE TABLE IF NOT EXISTS " + s["CREATE TABLE ".Length..] + ";";
        if (s.StartsWith("CREATE UNIQUE INDEX ", StringComparison.OrdinalIgnoreCase) && !s.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase))
            return "CREATE UNIQUE INDEX IF NOT EXISTS " + s["CREATE UNIQUE INDEX ".Length..] + ";";
        if (s.StartsWith("CREATE INDEX ", StringComparison.OrdinalIgnoreCase) && !s.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase))
            return "CREATE INDEX IF NOT EXISTS " + s["CREATE INDEX ".Length..] + ";";
        return null; // only CREATE statements are replayed
    }

    private static async Task<HashSet<string>> ListColumnsAsync(DbConnection connection, string table, bool isSqlite, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = isSqlite
            ? $"PRAGMA table_info(\"{table}\");"
            : $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}';";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(isSqlite ? 1 : 0));
        return columns;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
