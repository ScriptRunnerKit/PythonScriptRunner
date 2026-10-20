namespace PythonScriptRunner.Worker;

/// <summary>Settings of the worker (spec 006).</summary>
public sealed class WorkerOptions
{
    /// <summary>Identifier recorded on the attempts this worker runs. Default: the RabbitMQ instance id (machine name + process id).</summary>
    public string? WorkerId { get; set; }

    /// <summary>Steps executed at the same time by this worker (the queue prefetch; D-39). Default: number of CPUs.</summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>Lease of a running attempt; another worker recovers it when not renewed (QA-006-2). Default: 60 seconds.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the lease of a running attempt is renewed. Default: 15 seconds.</summary>
    public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often expired leases are looked for. Default: 15 seconds.</summary>
    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Recoveries of one step after lost workers before it fails with <c>WorkerLost</c> (QA-006-2). Default: 3.</summary>
    public int MaxLeaseRecoveries { get; set; } = 3;

    /// <summary>
    /// How long steps in progress may finish when the host stops (QA-006-3). Default: 5 minutes (the default timeout). The
    /// platform must grant at least this time before killing the process.
    /// </summary>
    public TimeSpan ShutdownGracePeriod { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often leftover work folders (older than <c>OrphanRetention</c>) are removed. Default: 1 hour.</summary>
    public TimeSpan WorkFolderCleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Cron scheduler.</summary>
    public WorkerSchedulerOptions Scheduler { get; } = new();

    internal void Validate()
    {
        if (MaxConcurrency is < 1 or > ushort.MaxValue)
        {
            throw new PsrConfigurationException("Worker MaxConcurrency must be between 1 and 65535.");
        }

        if (LeaseDuration <= TimeSpan.Zero || LeaseRenewInterval <= TimeSpan.Zero || LeaseRenewInterval >= LeaseDuration)
        {
            throw new PsrConfigurationException("Worker LeaseRenewInterval must be positive and shorter than LeaseDuration.");
        }

        if (ReaperInterval <= TimeSpan.Zero || ShutdownGracePeriod < TimeSpan.Zero || WorkFolderCleanupInterval <= TimeSpan.Zero || MaxLeaseRecoveries < 0)
        {
            throw new PsrConfigurationException("Worker intervals must be positive and MaxLeaseRecoveries cannot be negative.");
        }

        if (Scheduler.PollInterval <= TimeSpan.Zero || Scheduler.BatchSize < 1)
        {
            throw new PsrConfigurationException("Scheduler PollInterval and BatchSize must be positive.");
        }
    }
}

/// <summary>Settings of the cron scheduler.</summary>
public sealed class WorkerSchedulerOptions
{
    /// <summary>Fires schedules from this worker (several workers never fire the same occurrence twice). Default: <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often due schedules are looked for (the firing precision). Default: 5 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Schedules fired per transaction. Default: 50.</summary>
    public int BatchSize { get; set; } = 50;
}
