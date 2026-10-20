using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PythonScriptRunner.EntityFrameworkCore;

namespace PythonScriptRunner.Worker;

/// <summary>A loop that runs a cycle every interval (on the host's <see cref="TimeProvider"/>) and survives failures.</summary>
internal abstract class PeriodicLoop(TimeProvider time, ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    protected abstract string Name { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.LoopFailed(logger, Name, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    protected abstract Task RunCycleAsync(CancellationToken cancellationToken);

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Recovers or fails attempts whose worker stopped renewing the lease (D-85), and refreshes <c>psr.runs.pending</c>.</summary>
internal sealed class LeaseReaper(AsyncRunLifecycle lifecycle, WorkerOptions options, WorkerMetrics metrics, TimeProvider time, ILogger<LeaseReaper> logger)
    : PeriodicLoop(time, logger)
{
    private const int BatchSize = 50;

    protected override TimeSpan Interval => options.ReaperInterval;

    protected override string Name => "Lease reaper";

    protected override async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        LeaseReaping reaping;
        do
        {
            reaping = await lifecycle.ReapExpiredLeasesAsync(BatchSize, options.MaxLeaseRecoveries, cancellationToken).ConfigureAwait(false);
            if (reaping.Total > 0)
            {
                metrics.LeasesRecovered(reaping.Recovered);
                Log.Reaped(logger, reaping.Recovered, reaping.Failed, reaping.Cancelled);
            }
        }
        while (reaping.Total == BatchSize);

        metrics.PendingRuns(await lifecycle.CountPendingRunsAsync(cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>Fires due cron schedules (D-41, D-44); with several workers each occurrence fires once.</summary>
internal sealed class ScheduleFirer(AsyncRunLifecycle lifecycle, WorkerOptions options, WorkerMetrics metrics, TimeProvider time, ILogger<ScheduleFirer> logger)
    : PeriodicLoop(time, logger)
{
    protected override TimeSpan Interval => options.Scheduler.PollInterval;

    protected override string Name => "Scheduler";

    protected override async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        ScheduleFiring firing;
        do
        {
            firing = await lifecycle.FireDueSchedulesAsync(options.Scheduler.BatchSize, cancellationToken).ConfigureAwait(false);
            if (firing.Total > 0)
            {
                metrics.Schedules(firing.Fired, firing.Rejected);
                Log.SchedulesFired(logger, firing.Fired, firing.Rejected);
            }
        }
        while (firing.Total == options.Scheduler.BatchSize);
    }
}

/// <summary>Removes work folders left by interrupted runs, at startup and then every <see cref="WorkerOptions.WorkFolderCleanupInterval"/>.</summary>
internal sealed class WorkFolderJanitor(IRunEngine engine, WorkerOptions options, TimeProvider time, ILogger<WorkFolderJanitor> logger)
    : PeriodicLoop(time, logger)
{
    protected override TimeSpan Interval => options.WorkFolderCleanupInterval;

    protected override string Name => "Work folder cleanup";

    protected override Task RunCycleAsync(CancellationToken cancellationToken)
    {
        engine.RemoveOrphanWorkFolders();
        return Task.CompletedTask;
    }
}
