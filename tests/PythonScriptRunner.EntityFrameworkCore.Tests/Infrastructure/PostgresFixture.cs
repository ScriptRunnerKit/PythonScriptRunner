using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure.PostgresFixture))]

namespace PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>One PostgreSQL 16 container (the minimum supported version) for the whole test run; each test gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"psr_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = true }.ConnectionString;
    }
}
