using System.Diagnostics;
using Npgsql;
using PythonScriptRunner.EntityFrameworkCore.Runs;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>
/// Current time of queued runs: the host clock corrected by its offset to the PostgreSQL clock (measured every minute), so
/// lease expiry, retry delays and cron decisions agree across hosts (spec 006). With
/// <see cref="PsrEntityFrameworkOptions.UseDatabaseClock"/> off it is the registered <see cref="TimeProvider"/>.
/// </summary>
public sealed class PsrClock
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(1);

    private readonly PsrDataSource _dataSource;
    private readonly PsrEntityFrameworkOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private TimeSpan _offset;
    private long _syncedAt;

    internal PsrClock(PsrDataSource dataSource, PsrEntityFrameworkOptions options, TimeProvider time)
    {
        _dataSource = dataSource;
        _options = options;
        _time = time;
    }

    /// <summary>Current UTC time aligned to the database.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.UseDatabaseClock)
        {
            return _time.GetUtcNow();
        }

        var syncedAt = Volatile.Read(ref _syncedAt);
        if (syncedAt == 0 || Stopwatch.GetElapsedTime(syncedAt) > SyncInterval)
        {
            await SyncAsync(cancellationToken).ConfigureAwait(false);
        }

        return _time.GetUtcNow() + _offset;
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_syncedAt != 0 && Stopwatch.GetElapsedTime(_syncedAt) <= SyncInterval)
            {
                return;
            }

            var connection = await _dataSource.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
                await using (command.ConfigureAwait(false))
                {
                    var before = _time.GetUtcNow();
                    var started = Stopwatch.GetTimestamp();
                    var database = (DateTime)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                    var local = before + (Stopwatch.GetElapsedTime(started) / 2);
                    _offset = new DateTimeOffset(DateTime.SpecifyKind(database, DateTimeKind.Utc)) - local;
                }
            }

            Volatile.Write(ref _syncedAt, Stopwatch.GetTimestamp());
        }
        finally
        {
            _sync.Release();
        }
    }
}
