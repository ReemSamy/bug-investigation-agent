using System.Data;
using Microsoft.EntityFrameworkCore;

namespace BugInvestigationAgent.Infrastructure.Persistence;

/// <summary>
/// The app uses EnsureCreated (no EF migrations), which never alters existing tables.
/// This adds columns introduced after the first release to existing SQLite databases, idempotently.
/// </summary>
public static class SchemaUpgrader
{
    private static readonly (string Table, string Column, string Type)[] AddedColumns =
    [
        ("CredentialProfiles", "OwnerId", "TEXT NULL"),
        ("CredentialProfiles", "EnvironmentId", "TEXT NULL")
    ];

    public static async Task UpgradeAsync(AgentDbContext db, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsSqlite())
            return;

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(cancellationToken);

        try
        {
            foreach (var group in AddedColumns.GroupBy(c => c.Table))
            {
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA table_info(\"{group.Key}\");";
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        existing.Add(reader.GetString(1));
                }

                foreach (var (table, column, type) in group)
                {
                    if (existing.Contains(column))
                        continue;
                    await using var alter = connection.CreateCommand();
                    alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type};";
                    await alter.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using var index = connection.CreateCommand();
            index.CommandText =
                "CREATE INDEX IF NOT EXISTS \"IX_CredentialProfiles_OwnerId_EnvironmentId\" ON \"CredentialProfiles\" (\"OwnerId\", \"EnvironmentId\");";
            await index.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }
}
