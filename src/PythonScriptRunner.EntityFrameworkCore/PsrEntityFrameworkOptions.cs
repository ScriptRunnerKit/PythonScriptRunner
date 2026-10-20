using Npgsql;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>Settings of the PostgreSQL persistence.</summary>
public sealed class PsrEntityFrameworkOptions
{
    internal string? ConnectionString { get; private set; }

    internal Action<NpgsqlDataSourceBuilder>? ConfigureDataSource { get; private set; }

    /// <summary>Global retention (pipelines can only shorten it).</summary>
    public PsrRetentionOptions Retention { get; } = new();

    /// <summary>Maintenance job: partitions, retention and cleanups.</summary>
    public PsrMaintenanceOptions Maintenance { get; } = new();

    /// <summary>How long an idempotency key returns the same run. Default: 24 hours.</summary>
    public TimeSpan IdempotencyWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long "latest published version" lookups are cached in this process. Default: 30 seconds.</summary>
    public TimeSpan LatestVersionCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Aligns the time used by queued runs (leases, retry delays, schedules, outbox) to the PostgreSQL clock, so hosts with
    /// skewed clocks agree. Default: <c>true</c>. Tests that simulate time with a <see cref="TimeProvider"/> turn it off.
    /// </summary>
    public bool UseDatabaseClock { get; set; } = true;

    /// <summary>Connects to PostgreSQL (16 or newer).</summary>
    /// <param name="connectionString">Npgsql connection string.</param>
    /// <param name="configure">Extra settings of the data source (e.g. logging, password provider).</param>
    public PsrEntityFrameworkOptions UseNpgsql(string connectionString, Action<NpgsqlDataSourceBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString;
        ConfigureDataSource = configure;
        return this;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new PsrConfigurationException("AddEntityFrameworkCore requires a connection string: call o.UseNpgsql(...).");
        }

        if (Retention.Artifacts < TimeSpan.FromDays(1) || Retention.Runs < Retention.Artifacts || Retention.Audit < TimeSpan.FromDays(1))
        {
            throw new PsrConfigurationException("Retention must keep files at least 1 day, runs at least as long as files, and audit at least 1 day.");
        }

        if (Maintenance.Interval <= TimeSpan.Zero || Maintenance.BatchSize < 1)
        {
            throw new PsrConfigurationException("Maintenance interval and batch size must be positive.");
        }
    }
}

/// <summary>Global retention.</summary>
public sealed class PsrRetentionOptions
{
    /// <summary>How long file contents are kept. Default: 7 days.</summary>
    public TimeSpan Artifacts { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long runs, steps, attempts, logs and file metadata are kept. Default: 90 days.</summary>
    public TimeSpan Runs { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long audit events are kept. Default: 365 days.</summary>
    public TimeSpan Audit { get; set; } = TimeSpan.FromDays(365);
}

/// <summary>Maintenance job settings.</summary>
public sealed class PsrMaintenanceOptions
{
    /// <summary>Runs the maintenance job in this process (only one instance runs each cycle). Default: <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time between cycles. Default: 1 hour.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Rows deleted per statement when applying shortened retentions. Default: 1000.</summary>
    public int BatchSize { get; set; } = 1000;
}
