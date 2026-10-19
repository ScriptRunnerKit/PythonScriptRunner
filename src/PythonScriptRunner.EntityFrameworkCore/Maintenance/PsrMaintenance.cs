using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Npgsql;
using PythonScriptRunner.EntityFrameworkCore.Runs;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>Outcome of one maintenance cycle.</summary>
/// <param name="PartitionsCreated">Partitions created ahead.</param>
/// <param name="PartitionsDropped">Partitions dropped beyond the global retention.</param>
/// <param name="RunsExpired">Unfinished runs marked <c>Failed (Expired)</c>.</param>
/// <param name="ContentsDeleted">File contents deleted by a shortened retention.</param>
/// <param name="RunsDeleted">Runs deleted by a shortened retention.</param>
/// <param name="OutboxDeleted">Published outbox messages removed.</param>
/// <param name="IdempotencyKeysDeleted">Expired idempotency keys removed.</param>
public sealed record MaintenanceReport(
    int PartitionsCreated,
    IReadOnlyList<string> PartitionsDropped,
    int RunsExpired,
    int ContentsDeleted,
    int RunsDeleted,
    int OutboxDeleted,
    int IdempotencyKeysDeleted);

/// <summary>
/// Maintenance of the <c>psr</c> schema: creates partitions ahead (4 weeks, 7 days, 3 months), applies shortened retentions in
/// batches, ends unfinished runs that reached the file retention, drops partitions beyond the global retention and cleans the
/// outbox and idempotency keys. Only one instance runs at a time (PostgreSQL advisory lock).
/// </summary>
public sealed class PsrMaintenance
{
    /// <summary>Advisory lock key ("PSRMAINT").</summary>
    internal const long LockKey = 0x5053524D41494E54;

    private const string Terminal = "('Succeeded', 'Failed', 'Cancelled')";

    private readonly PsrDataSource _dataSource;
    private readonly PsrEntityFrameworkOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PsrMaintenance> _logger;
    private readonly Counter<long> _partitionsCreated;
    private readonly Counter<long> _partitionsDropped;
    private readonly Counter<long> _rowsDeleted;

    internal PsrMaintenance(PsrDataSource dataSource, PsrEntityFrameworkOptions options, TimeProvider time, IMeterFactory meters, ILogger<PsrMaintenance> logger)
    {
        _dataSource = dataSource;
        _options = options;
        _time = time;
        _logger = logger;
        var meter = meters.Create("PythonScriptRunner");
        _partitionsCreated = meter.CreateCounter<long>("psr.maintenance.partitions.created", description: "Partitions created ahead.");
        _partitionsDropped = meter.CreateCounter<long>("psr.maintenance.partitions.dropped", description: "Partitions dropped by the retention.");
        _rowsDeleted = meter.CreateCounter<long>("psr.maintenance.rows.deleted", description: "Rows deleted by shortened retentions and cleanups.");
    }

    /// <summary>Runs one cycle now.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, or <c>null</c> when another instance is running the cycle.</returns>
    public async Task<MaintenanceReport?> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (!await ScalarAsync<bool>(connection, "SELECT pg_try_advisory_lock($1)", cancellationToken, LockKey).ConfigureAwait(false))
            {
                Log.Skipped(_logger);
                return null;
            }

            try
            {
                var report = await RunCycleAsync(connection, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                _partitionsCreated.Add(report.PartitionsCreated);
                _partitionsDropped.Add(report.PartitionsDropped.Count);
                _rowsDeleted.Add(report.ContentsDeleted + report.RunsDeleted + report.OutboxDeleted + report.IdempotencyKeysDeleted);
                Log.Completed(_logger, report.PartitionsCreated, report.PartitionsDropped.Count, report.RunsExpired, report.ContentsDeleted, report.RunsDeleted);
                return report;
            }
            finally
            {
                await ScalarAsync<bool>(connection, "SELECT pg_advisory_unlock($1)", CancellationToken.None, LockKey).ConfigureAwait(false);
            }
        }
    }

    private async Task<MaintenanceReport> RunCycleAsync(NpgsqlConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var retention = _options.Retention;
        var batch = _options.Maintenance.BatchSize;
        var created = await ScalarAsync<int>(connection, "SELECT psr.ensure_partitions($1, 4, 7, 3)", cancellationToken, now).ConfigureAwait(false);

        // QA-D6: an unfinished run that reaches the file retention (minus one day) ends Failed (Expired) before its files go.
        var expired = await ScalarAsync<long>(connection,
            $"""
            WITH expired AS (
                UPDATE psr.runs r
                SET status = 'Failed', finished_at = $1,
                    error = jsonb_build_object('kind', 'Expired', 'code', 'RUN_EXPIRED', 'message', 'The run reached the file retention before finishing.', 'step', NULL)
                FROM (
                    SELECT r2.id, r2.created_at
                    FROM psr.runs r2
                    LEFT JOIN psr.pipelines p ON p.id = r2.pipeline_id
                    WHERE r2.status IN ('Pending', 'Queued', 'Running', 'Cancelling')
                      AND r2.created_at <= $1 - (LEAST($2::interval, COALESCE(make_interval(days => p.artifact_retention_days), $2::interval)) - interval '1 day')
                ) x
                WHERE r.id = x.id AND r.created_at = x.created_at
                RETURNING r.id, r.created_at),
            steps AS (
                UPDATE psr.step_runs s
                SET status = CASE WHEN s.status IN ('Pending', 'Queued') THEN 'Skipped' ELSE 'Failed' END, finished_at = $1
                FROM expired e
                WHERE s.run_id = e.id AND s.run_created_at = e.created_at AND s.status IN ('Pending', 'Queued', 'Running'))
            SELECT count(*) FROM expired
            """,
            cancellationToken, now, retention.Artifacts).ConfigureAwait(false);

        // Contents past their expiry (shortened by a pipeline, or in partitions about to be dropped) are deleted; metadata stays.
        var contents = await RepeatAsync(connection,
            """
            WITH expired AS (
                SELECT id, run_created_at, content_created_at FROM psr.artifacts
                WHERE content_deleted_at IS NULL AND (content_expires_at <= $1 OR content_created_at <= $1 - $3::interval)
                LIMIT $2),
            removed AS (
                DELETE FROM psr.artifact_contents c USING expired e WHERE c.artifact_id = e.id AND c.created_at = e.content_created_at)
            UPDATE psr.artifacts a SET content_deleted_at = $1
            FROM expired e WHERE a.id = e.id AND a.run_created_at = e.run_created_at
            """,
            batch, cancellationToken, now, batch, retention.Artifacts).ConfigureAwait(false);

        // Runs of pipelines with a shorter run retention (children go with the foreign keys).
        var runs = await RepeatAsync(connection,
            $"""
            WITH expired AS (
                SELECT id, created_at FROM psr.runs WHERE expires_at <= $1 AND status IN {Terminal} LIMIT $2),
            contents AS (
                DELETE FROM psr.artifact_contents c USING psr.artifacts a, expired e
                WHERE a.run_id = e.id AND a.run_created_at = e.created_at AND c.artifact_id = a.id AND c.created_at = a.content_created_at)
            DELETE FROM psr.runs r USING expired e WHERE r.id = e.id AND r.created_at = e.created_at
            """,
            batch, cancellationToken, now, batch).ConfigureAwait(false);

        var dropped = new List<string>();
        var command = new NpgsqlCommand("SELECT * FROM psr.drop_expired_partitions($1, $2, $3, $4)", connection);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            command.Parameters.Add(new NpgsqlParameter { Value = retention.Runs });
            command.Parameters.Add(new NpgsqlParameter { Value = retention.Artifacts });
            command.Parameters.Add(new NpgsqlParameter { Value = retention.Audit });
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    dropped.Add(reader.GetString(0));
                }
            }
        }

        var outbox = await ExecuteAsync(connection, "DELETE FROM psr.outbox_messages WHERE published_at <= $1", cancellationToken, now - TimeSpan.FromHours(24))
            .ConfigureAwait(false);
        var keys = await ExecuteAsync(connection, "DELETE FROM psr.idempotency_keys WHERE expires_at <= $1", cancellationToken, now).ConfigureAwait(false);
        return new MaintenanceReport(created, dropped, (int)expired, contents, runs, outbox, keys);
    }

    /// <summary>Repeats a batched statement until it affects fewer rows than a batch.</summary>
    private static async Task<int> RepeatAsync(NpgsqlConnection connection, string sql, int batch, CancellationToken cancellationToken, params object[] parameters)
    {
        var total = 0;
        int affected;
        do
        {
            affected = await ExecuteAsync(connection, sql, cancellationToken, parameters).ConfigureAwait(false);
            total += affected;
        }
        while (affected >= batch);
        return total;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        var command = Command(connection, sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        var command = Command(connection, sql, parameters);
        await using (command.ConfigureAwait(false))
        {
            return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return command;
    }
}

/// <summary>Log messages of the persistence package.</summary>
internal static partial class Log
{
    [LoggerMessage(101, LogLevel.Debug, "Maintenance skipped: another instance holds the lock")]
    public static partial void Skipped(ILogger logger);

    [LoggerMessage(102, LogLevel.Information,
        "Maintenance: {Created} partitions created, {Dropped} dropped, {Expired} runs expired, {Contents} file contents and {Runs} runs deleted")]
    public static partial void Completed(ILogger logger, int created, int dropped, int expired, int contents, int runs);

    [LoggerMessage(103, LogLevel.Error, "Maintenance cycle failed")]
    public static partial void Failed(ILogger logger, Exception exception);

    [LoggerMessage(104, LogLevel.Warning, "Maintenance skipped: the psr schema is not migrated (call MigratePythonScriptRunnerAsync)")]
    public static partial void SchemaMissing(ILogger logger);
}
