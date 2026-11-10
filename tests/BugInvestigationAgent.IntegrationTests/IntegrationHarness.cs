using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BugInvestigationAgent.TestKit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace BugInvestigationAgent.IntegrationTests;

/// <summary>Real local PostgreSQL. Tests are skipped (not faked) when BIA_IT_POSTGRES is not set.</summary>
public static class Postgres
{
    public static string? AdminConnectionString => Environment.GetEnvironmentVariable("BIA_IT_POSTGRES");

    public static async Task<string> CreateDatabaseAsync()
    {
        if (string.IsNullOrWhiteSpace(AdminConnectionString))
            Assert.Ignore("BIA_IT_POSTGRES is not set — real PostgreSQL integration tests skipped.");
        var name = "bia_it_" + Guid.NewGuid().ToString("N")[..12];
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name};", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name, Pooling = true }.ConnectionString;
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE);", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

