using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PythonScriptRunner.EntityFrameworkCore;
using PythonScriptRunner.RabbitMQ;

namespace PythonScriptRunner.Worker;

/// <summary>
/// Executes queued steps, one <c>ExecuteStep</c> message per step (D-34; spec 006): claims the step with a lease, runs the
/// Python process while renewing the lease, records the outcome and the next message in one transaction, then acknowledges.
/// Duplicate or stale messages are acknowledged without effect. On shutdown it stops receiving and lets steps in progress
/// finish within <see cref="WorkerOptions.ShutdownGracePeriod"/>; past it, processes are stopped and their attempts are left
/// to the reaper.
/// </summary>
internal sealed class StepWorker(
    PsrBroker broker,
    AsyncRunLifecycle lifecycle,
    IRunEngine engine,
    WorkerOptions options,
    WorkerMetrics metrics,
    TimeProvider time,
    ILogger<StepWorker> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private QueueConsumer? _consumer;
    private Task? _running;

    /// <summary>Identifier recorded on the attempts.</summary>
    public string WorkerId { get; } = options.WorkerId ?? broker.InstanceId;

    /// <summary>The consumer (for tests and health).</summary>
    public QueueConsumer? Consumer => _consumer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _consumer = new QueueConsumer(broker, new QueueConsumerSettings
        {
            Queue = PsrTopology.ExecuteStepQueue,
            Prefetch = (ushort)options.MaxConcurrency,
        }, HandleAsync, logger);
        _running = Task.Run(() => _consumer.RunAsync(_stopping.Token), CancellationToken.None);
        Log.Started(logger, WorkerId, PsrTopology.ExecuteStepQueue, options.MaxConcurrency);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A host can be stopped more than once (e.g. stop, then dispose).
        if (Interlocked.Exchange(ref _consumer, null) is not { } consumer || _running is not { } running)
        {
            return;
        }

        await consumer.StopAsync(options.ShutdownGracePeriod, cancellationToken).ConfigureAwait(false);
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown timeout.
        }

        await consumer.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose() => _stopping.Dispose();

    /// <param name="delivery">The message.</param>
    /// <param name="abandon">Cancelled when the shutdown grace period is over.</param>
    internal async Task<DeliveryOutcome> HandleAsync(PsrDelivery delivery, CancellationToken abandon)
    {
        ExecuteStepCommand command;
        try
        {
            if (delivery.Type is { Length: > 0 } type && type != PsrMessageTypes.ExecuteStep)
            {
                throw new JsonException($"unknown message type '{type}'");
            }

            command = JsonSerializer.Deserialize<ExecuteStepCommand>(delivery.Body.Span, PsrMessageJson.Options) ?? throw new JsonException("The body is null.");
            if (command.RunId.Value == Guid.Empty || command.StepRunId.Value == Guid.Empty || command.Attempt < 1)
            {
                throw new JsonException("runId, stepRunId and attempt (>= 1) are required.");
            }
        }
        catch (JsonException ex)
        {
            Log.InvalidCommand(logger, delivery.MessageId, ex.Message);
            return DeliveryOutcome.DeadLetter;
        }

        using var activity = StartActivity(delivery, command);
        var claimed = await RetryAsync("claim step", () => lifecycle.ClaimStepAsync(command, WorkerId, options.LeaseDuration, abandon), abandon)
            .ConfigureAwait(false);
        if (claimed is null)
        {
            // Duplicate, stale (already executed or recovered elsewhere) or cancelled: nothing to do.
            return DeliveryOutcome.Ack;
        }

        RecordQueueWait(delivery);
        activity?.SetTag("psr.step.key", claimed.StepKey);
        activity?.SetTag("psr.stage.key", claimed.Stage.Key);
        metrics.StepStarted();
        try
        {
            return await ExecuteAsync(claimed, abandon).ConfigureAwait(false);
        }
        finally
        {
            metrics.StepEnded();
        }
    }

    private async Task<DeliveryOutcome> ExecuteAsync(ClaimedStep claimed, CancellationToken abandon)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(abandon);
        using var renewing = new CancellationTokenSource();
        var leaseLost = false;
        var renewal = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(options.LeaseRenewInterval, time);
            try
            {
                while (await timer.WaitForNextTickAsync(renewing.Token).ConfigureAwait(false))
                {
                    try
                    {
                        if (!await lifecycle.RenewLeaseAsync(claimed, options.LeaseDuration, renewing.Token).ConfigureAwait(false))
                        {
                            // The reaper took the attempt: stop the process and record nothing.
                            leaseLost = true;
                            await execution.CancelAsync().ConfigureAwait(false);
                            return;
                        }
                    }
                    catch (Exception ex) when (IsTransient(ex))
                    {
                        Log.DatabaseRetry(logger, "renew lease", options.LeaseRenewInterval, ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Step finished.
            }
        }, CancellationToken.None);

        StepExecution result;
        try
        {
            result = await engine.ExecuteStepAsync(claimed.ToExecutionRequest(), execution.Token).ConfigureAwait(false);
        }
        finally
        {
            await renewing.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
        }

        await using (result.ConfigureAwait(false))
        {
            if (leaseLost)
            {
                Log.LeaseLost(logger, claimed.StepKey, claimed.RunId);
                return DeliveryOutcome.Ack;
            }

            if (abandon.IsCancellationRequested)
            {
                Log.Abandoned(logger, claimed.StepKey, claimed.RunId);
                return DeliveryOutcome.Abandon;
            }

            // Retried while the lease lasts (database blips); afterwards the reaper decides (D-85).
            var completion = await RetryAsync("complete step", () => lifecycle.CompleteStepAsync(claimed, result, CancellationToken.None),
                CancellationToken.None, options.LeaseDuration).ConfigureAwait(false);
            if (completion == StepCompletion.LeaseLost)
            {
                Log.LeaseLost(logger, claimed.StepKey, claimed.RunId);
            }
            else
            {
                Log.StepCompleted(logger, claimed.StepKey, claimed.Attempt, claimed.RunId, completion.ToString());
            }

            return DeliveryOutcome.Ack;
        }
    }

    /// <summary>Retries transient database failures with backoff (up to <paramref name="limit"/>; default 30 seconds).</summary>
    private async Task<T> RetryAsync<T>(string operation, Func<Task<T>> action, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        var started = Stopwatch.GetTimestamp();
        var delay = TimeSpan.FromMilliseconds(200);
        while (true)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) && Stopwatch.GetElapsedTime(started) + delay < (limit ?? TimeSpan.FromSeconds(30)))
            {
                Log.DatabaseRetry(logger, operation, delay, ex);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(5000, delay.TotalMilliseconds * 2));
            }
        }
    }

    private static bool IsTransient(Exception exception) =>
        exception is DbException or TimeoutException || exception.InnerException is DbException or TimeoutException;

    private static Activity? StartActivity(PsrDelivery delivery, ExecuteStepCommand command)
    {
        var activity = ActivityContext.TryParse(delivery.TraceParent, null, out var parent)
            ? WorkerDiagnostics.Source.StartActivity("psr.message.consume", ActivityKind.Consumer, parent)
            : WorkerDiagnostics.Source.StartActivity("psr.message.consume", ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", delivery.Queue);
        activity?.SetTag("psr.run.id", command.RunId.ToString());
        activity?.SetTag("psr.attempt", command.Attempt);
        return activity;
    }

    /// <summary><c>psr.queue.wait</c>: from the moment the message became due (header set by the relay) to the claim.</summary>
    private void RecordQueueWait(PsrDelivery delivery)
    {
        if (delivery.Headers.TryGetValue("psr-available-at", out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            metrics.QueueWait(time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
        }
    }
}
