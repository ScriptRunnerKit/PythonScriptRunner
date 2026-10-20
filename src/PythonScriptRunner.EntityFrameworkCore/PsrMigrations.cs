using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PythonScriptRunner.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Runs;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Applies or scripts the migrations of the <c>psr</c> schema. Migrations never run implicitly.</summary>
public static class PsrMigrations
{
    /// <summary>Advisory lock key that serializes migrations of the <c>psr</c> schema ("psrmigr" in ASCII).</summary>
    private const long MigrationLockKey = 0x707372_6D69_6772;

    /// <summary>
    /// Creates or updates the <c>psr</c> schema (tables, partitions and functions). Safe to run again, and from several
    /// instances at once: a PostgreSQL advisory lock lets one migrate while the others wait (spec 009, AC-8).
    /// </summary>
    /// <param name="services">Application services.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task MigratePythonScriptRunnerAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var connection = await services.GetRequiredService<PsrDataSource>().DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await LockAsync(connection, "SELECT pg_advisory_lock($1)", cancellationToken).ConfigureAwait(false);
            try
            {
                var factory = services.GetRequiredService<IDbContextFactory<PsrDbContext>>();
                var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using (db.ConfigureAwait(false))
                {
                    await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await LockAsync(connection, "SELECT pg_advisory_unlock($1)", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task LockAsync(Npgsql.NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = new Npgsql.NpgsqlCommand(sql, connection);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = MigrationLockKey });
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Generates the SQL of every migration, for DBAs or deployment pipelines.</summary>
    /// <param name="services">Application services.</param>
    /// <param name="idempotent">Whether the script checks the history table and can run on any version of the schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<string> GeneratePythonScriptRunnerMigrationScriptAsync(
        this IServiceProvider services, bool idempotent = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var factory = services.GetRequiredService<IDbContextFactory<PsrDbContext>>();
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var migrator = db.GetService<IMigrator>();
            return migrator.GenerateScript(options: idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default);
        }
    }
}
