using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using PythonScriptRunner;
using PythonScriptRunner.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Infrastructure;
using PythonScriptRunner.EntityFrameworkCore.Maintenance;
using PythonScriptRunner.EntityFrameworkCore.Runs;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the PostgreSQL persistence of PythonScriptRunner.</summary>
public static class PsrEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Stores the catalog and every run in PostgreSQL: replaces the catalog (stages and pipelines then live in the database)
    /// and the run recorder, and adds <see cref="ICatalogAdmin"/>, <see cref="IRunManager"/>, <see cref="IArtifactStore"/>,
    /// <see cref="IAuditWriter"/>, <see cref="IAuditReader"/>, <see cref="IIdempotencyStore"/>, <see cref="IOutboxWriter"/> and
    /// the maintenance job. The schema is never migrated implicitly: call <c>MigratePythonScriptRunnerAsync()</c>.
    /// </summary>
    /// <param name="builder">Builder returned by <c>AddPythonScriptRunner</c>.</param>
    /// <param name="configure">Connection, retention and maintenance settings.</param>
    public static IPythonScriptRunnerBuilder AddEntityFrameworkCore(this IPythonScriptRunnerBuilder builder, Action<PsrEntityFrameworkOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new PsrEntityFrameworkOptions();
        configure(options);
        options.Validate();

        var services = builder.Services;
        services.AddSingleton(options);
        services.TryAddSingleton<PsrDataSource>();
        services.AddPooledDbContextFactory<PsrDbContext>((provider, db) => db
            .UseNpgsql(provider.GetRequiredService<PsrDataSource>().DataSource, PsrDbContextOptions.ConfigureNpgsql)
            .ConfigureWarnings(PsrDbContextOptions.ConfigureWarnings));

        services.TryAddSingleton<EfCatalog>();
        services.Replace(ServiceDescriptor.Singleton<ICatalog>(provider => provider.GetRequiredService<EfCatalog>()));
        services.TryAddSingleton<ICatalogAdmin, EfCatalogAdmin>();
        services.TryAddSingleton<ArtifactStore>();
        services.TryAddSingleton<IArtifactStore>(provider => provider.GetRequiredService<ArtifactStore>());
        services.TryAddSingleton<IIdempotencyStore, IdempotencyStore>();
        services.TryAddSingleton<IOutboxWriter, OutboxWriter>();
        services.TryAddSingleton<RunLoader>();
        services.TryAddSingleton<IRunMessageOutbox, NullRunMessageOutbox>();
        services.TryAddSingleton(provider => new PsrClock(
            provider.GetRequiredService<PsrDataSource>(), options, provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(provider => new PsrOutbox(
            provider.GetRequiredService<IDbContextFactory<PsrDbContext>>(), provider.GetRequiredService<PsrClock>()));
        services.TryAddSingleton(provider => new AsyncRunLifecycle(
            provider.GetRequiredService<IDbContextFactory<PsrDbContext>>(),
            provider.GetRequiredService<ArtifactStore>(),
            provider.GetRequiredService<IIdempotencyStore>(),
            provider.GetRequiredService<RunLoader>(),
            provider.GetRequiredService<IRunMessageOutbox>(),
            provider.GetRequiredService<PsrClock>(),
            provider.GetRequiredService<ICatalog>(),
            provider.GetRequiredService<IRunEngine>(),
            options));
        services.Replace(ServiceDescriptor.Singleton<IRunRecorder, EfRunRecorder>());
        services.TryAddSingleton<IRunManager, EfRunManager>();
        services.TryAddSingleton<IAuditWriter, AuditWriter>();
        services.TryAddSingleton<IAuditReader, AuditReader>();
        services.TryAddSingleton(provider => new PsrMaintenance(
            provider.GetRequiredService<PsrDataSource>(),
            options,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PsrMaintenance>>()));
        if (options.Maintenance.Enabled)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MaintenanceService>());
        }

        return builder;
    }

    /// <summary>
    /// Health of the <c>psr</c> database: reachable, migrated and with partitions ahead. Reports <paramref name="failureStatus"/>
    /// (default <see cref="HealthStatus.Unhealthy"/>) when the schema is pending or fewer than two periods of partitions remain.
    /// </summary>
    /// <param name="builder">Health checks builder.</param>
    /// <param name="name">Check name.</param>
    /// <param name="failureStatus">Status reported on failure.</param>
    /// <param name="tags">Tags (e.g. <c>ready</c>).</param>
    public static IHealthChecksBuilder AddPythonScriptRunnerDatabase(
        this IHealthChecksBuilder builder, string name = "psr-database", HealthStatus? failureStatus = null, IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<DatabaseHealthCheck>();
        return builder.Add(new HealthCheckRegistration(name, provider => provider.GetRequiredService<DatabaseHealthCheck>(), failureStatus, tags));
    }
}
