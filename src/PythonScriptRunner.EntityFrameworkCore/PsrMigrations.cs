using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PythonScriptRunner.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Applies or scripts the migrations of the <c>psr</c> schema. Migrations never run implicitly.</summary>
public static class PsrMigrations
{
    /// <summary>Creates or updates the <c>psr</c> schema (tables, partitions and functions). Safe to run again.</summary>
    /// <param name="services">Application services.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task MigratePythonScriptRunnerAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var factory = services.GetRequiredService<IDbContextFactory<PsrDbContext>>();
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
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
