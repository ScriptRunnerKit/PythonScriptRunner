using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using PythonScriptRunner.EntityFrameworkCore.Runs;

namespace PythonScriptRunner.EntityFrameworkCore.Maintenance;

/// <summary>
/// Database health: reachable, schema migrated (never migrates by itself) and at least two periods of partitions ahead
/// (2 weeks of history, 2 days of file contents, 2 months of audit).
/// </summary>
internal sealed class DatabaseHealthCheck(IDbContextFactory<PsrDbContext> factory, PsrDataSource dataSource, TimeProvider time) : IHealthCheck
{
    private const string LatestPartitions = """
        SELECT parent.relname::text,
               max(substring(pg_get_expr(child.relpartbound, child.oid) FROM 'TO \(''([^'']+)''\)')::timestamptz)
        FROM pg_inherits inh
        JOIN pg_class child ON child.oid = inh.inhrelid
        JOIN pg_class parent ON parent.oid = inh.inhparent
        JOIN pg_namespace ns ON ns.oid = parent.relnamespace
        WHERE ns.nspname = 'psr' AND parent.relkind = 'p' AND child.relkind IN ('r', 'p')
        GROUP BY parent.relname
        """;

    private static readonly string[] Weekly = ["runs", "step_runs", "step_attempts", "attempt_logs", "artifacts"];

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                if (pending.Count > 0)
                {
                    return new HealthCheckResult(context.Registration.FailureStatus,
                        $"psr schema pending: {pending.Count} migration(s) not applied ({string.Join(", ", pending)}).");
                }
            }

            var latest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var connection = await dataSource.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var command = new NpgsqlCommand(LatestPartitions, connection);
                await using (command.ConfigureAwait(false))
                {
                    var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            latest[reader.GetString(0)] = reader.GetFieldValue<DateTimeOffset>(1);
                        }
                    }
                }
            }

            var now = time.GetUtcNow();
            var missing = Weekly.Where(t => !Covers(latest, t, now.AddDays(14)))
                .Concat(Covers(latest, "artifact_contents", now.AddDays(2)) ? [] : ["artifact_contents"])
                .Concat(Covers(latest, "audit_events", now.AddMonths(2)) ? [] : ["audit_events"])
                .ToList();
            return missing.Count > 0
                ? new HealthCheckResult(context.Registration.FailureStatus,
                    $"Fewer than two periods of partitions ahead for: {string.Join(", ", missing)} (is the maintenance job running?).")
                : HealthCheckResult.Healthy("psr database ready.");
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "psr database unreachable.", ex);
        }
    }

    private static bool Covers(Dictionary<string, DateTimeOffset> latest, string table, DateTimeOffset until) =>
        latest.TryGetValue(table, out var upper) && upper >= until;
}
