using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PythonScriptRunner.EntityFrameworkCore.Maintenance;

/// <summary>Runs <see cref="PsrMaintenance"/> at start and then every <see cref="PsrMaintenanceOptions.Interval"/>.</summary>
internal sealed class MaintenanceService(
    PsrMaintenance maintenance, PsrEntityFrameworkOptions options, TimeProvider time, ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Maintenance.Interval, time);
        do
        {
            try
            {
                await maintenance.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedFunction
                or PostgresErrorCodes.InvalidSchemaName)
            {
                Log.SchemaMissing(logger);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                Log.Failed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
