using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PythonScriptRunner.EntityFrameworkCore.Tests.Infrastructure;

namespace PythonScriptRunner.EntityFrameworkCore.Tests;

public sealed class SchemaTests(PostgresFixture postgres, PythonFixture python)
{
    private const string Signature = """
        SELECT string_agg(item, ',' ORDER BY item) FROM (
            SELECT c.relkind::text || ':' || c.relname AS item
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'psr'
            UNION ALL
            SELECT 'f:' || p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'psr'
        ) objects
        """;

    private static readonly string[] Tables =
    [
        "stages", "stage_versions", "pipelines", "pipeline_versions", "pipeline_steps", "schedules", "runs", "step_runs",
        "step_attempts", "attempt_logs", "artifacts", "artifact_contents", "outbox_messages", "idempotency_keys", "audit_events",
    ];

    [Fact]
    public async Task Spec004_AC01_MigrateCreatesSchemaPartitionsAndIsRepeatable()
    {
        await using var migrated = await PsrTestHost.CreateAsync(postgres, python);
        var tables = await migrated.ColumnAsync<string>(
            "SELECT c.relname::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'psr' AND c.relkind IN ('r', 'p') AND NOT c.relispartition");
        tables.ShouldBeSubsetOf([.. Tables, "__ef_migrations_history"]);
        tables.Count.ShouldBe(Tables.Length + 1);
        (await migrated.ScalarAsync<long>("SELECT count(*) FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhparent WHERE p.relname = 'runs'")).ShouldBe(5);
        (await migrated.ScalarAsync<long>("SELECT count(*) FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhparent WHERE p.relname = 'artifact_contents'")).ShouldBe(8);
        (await migrated.ScalarAsync<long>("SELECT count(*) FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhparent WHERE p.relname = 'audit_events'")).ShouldBe(4);
        var signature = await migrated.ScalarAsync<string>(Signature);

        await migrated.Services.MigratePythonScriptRunnerAsync(TestContext.Current.CancellationToken);
        (await migrated.ScalarAsync<string>(Signature)).ShouldBe(signature);

        var script = await migrated.Services.GeneratePythonScriptRunnerMigrationScriptAsync(idempotent: true, TestContext.Current.CancellationToken);
        await using var scripted = await PsrTestHost.CreateAsync(postgres, python, migrate: false);
        await ExecuteAsync(scripted.ConnectionString, script);
        (await scripted.ScalarAsync<string>(Signature)).ShouldBe(signature);

        await ExecuteAsync(scripted.ConnectionString, script);
        (await scripted.ScalarAsync<string>(Signature)).ShouldBe(signature);
        (await scripted.ScalarAsync<long>("SELECT count(*) FROM psr.__ef_migrations_history")).ShouldBe(1);
    }

    [Fact]
    public async Task Spec004_AC02_WithoutMigrationStartupDoesNotTouchTheDatabaseAndHealthReportsPendingSchema()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python, migrate: false,
            configure: o => o.Maintenance.Enabled = true,
            services: s => s.AddHealthChecks().AddPythonScriptRunnerDatabase());
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        var health = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);
        foreach (var service in hosted)
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        health.Status.ShouldBe(HealthStatus.Unhealthy);
        health.Entries["psr-database"].Description.ShouldNotBeNull().ShouldContain("schema pending");
        (await host.ScalarAsync<long>("SELECT count(*) FROM pg_namespace WHERE nspname = 'psr'")).ShouldBe(0);
    }

    [Fact]
    public async Task HealthIsHealthyAfterMigration()
    {
        await using var host = await PsrTestHost.CreateAsync(postgres, python, services: s => s.AddHealthChecks().AddPythonScriptRunnerDatabase());

        var health = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        health.Status.ShouldBe(HealthStatus.Healthy, health.Entries["psr-database"].Description);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
