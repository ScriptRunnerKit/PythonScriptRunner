using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace PythonScriptRunner.Worker;

/// <summary>Tracing of the worker (source <c>PythonScriptRunner</c>).</summary>
internal static class WorkerDiagnostics
{
    public static readonly ActivitySource Source = new("PythonScriptRunner");
}

/// <summary>Metrics of the worker (meter <c>PythonScriptRunner</c>; spec 006).</summary>
internal sealed class WorkerMetrics
{
    private readonly UpDownCounter<int> _activeSteps;
    private readonly Histogram<double> _queueWait;
    private readonly Counter<long> _leaseRecovered;
    private readonly Counter<long> _scheduleFired;
    private readonly Counter<long> _scheduleRejected;
    private int _pendingRuns;

    public WorkerMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("PythonScriptRunner");
        _activeSteps = meter.CreateUpDownCounter<int>("psr.worker.active_steps", description: "Steps executing in this worker.");
        _queueWait = meter.CreateHistogram<double>("psr.queue.wait", "ms", "Time from queuing a step to its start in a worker.");
        _leaseRecovered = meter.CreateCounter<long>("psr.lease.recovered", description: "Attempts restarted after their worker was lost.");
        _scheduleFired = meter.CreateCounter<long>("psr.schedule.fired", description: "Runs created by schedules.");
        _scheduleRejected = meter.CreateCounter<long>("psr.schedule.rejected", description: "Schedule firings rejected.");
        meter.CreateObservableGauge("psr.runs.pending", () => Volatile.Read(ref _pendingRuns), description: "Runs waiting for a free slot of their pipeline.");
    }

    public void StepStarted() => _activeSteps.Add(1);

    public void StepEnded() => _activeSteps.Add(-1);

    public void QueueWait(TimeSpan wait) => _queueWait.Record(Math.Max(0, wait.TotalMilliseconds));

    public void LeasesRecovered(int count) => _leaseRecovered.Add(count);

    public void Schedules(int fired, int rejected)
    {
        _scheduleFired.Add(fired);
        _scheduleRejected.Add(rejected);
    }

    public void PendingRuns(int count) => Volatile.Write(ref _pendingRuns, count);
}

/// <summary>Source-generated log messages.</summary>
internal static partial class Log
{
    [LoggerMessage(200, LogLevel.Information, "Worker {WorkerId} consuming {Queue} with {MaxConcurrency} concurrent steps")]
    public static partial void Started(ILogger logger, string workerId, string queue, int maxConcurrency);

    [LoggerMessage(201, LogLevel.Information, "Step {StepKey} (attempt {Attempt}) of run {RunId}: {Completion}")]
    public static partial void StepCompleted(ILogger logger, string stepKey, int attempt, RunId runId, string completion);

    [LoggerMessage(202, LogLevel.Warning, "The lease of step {StepKey} of run {RunId} was taken by another worker; the process was stopped")]
    public static partial void LeaseLost(ILogger logger, string stepKey, RunId runId);

    [LoggerMessage(203, LogLevel.Warning, "Shutdown grace period over: step {StepKey} of run {RunId} was stopped and will be recovered by another worker")]
    public static partial void Abandoned(ILogger logger, string stepKey, RunId runId);

    [LoggerMessage(204, LogLevel.Warning, "Database operation failed ({Operation}); retrying in {Delay}")]
    public static partial void DatabaseRetry(ILogger logger, string operation, TimeSpan delay, Exception exception);

    [LoggerMessage(205, LogLevel.Information, "Lease reaper: {Recovered} recovered, {Failed} failed (worker lost), {Cancelled} cancelled")]
    public static partial void Reaped(ILogger logger, int recovered, int failed, int cancelled);

    [LoggerMessage(206, LogLevel.Information, "Scheduler: {Fired} runs created, {Rejected} firings rejected")]
    public static partial void SchedulesFired(ILogger logger, int fired, int rejected);

    [LoggerMessage(207, LogLevel.Error, "{Loop} failed")]
    public static partial void LoopFailed(ILogger logger, string loop, Exception exception);

    [LoggerMessage(208, LogLevel.Error, "ExecuteStep message {MessageId} is invalid and goes to the dead-letter queue: {Reason}")]
    public static partial void InvalidCommand(ILogger logger, string? messageId, string reason);
}
