using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PythonScriptRunner.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>
/// Publishes the outbox (spec 005): every <see cref="RabbitMqOptions.RelayInterval"/> it locks the due rows
/// (<c>SKIP LOCKED</c>), publishes them with publisher confirms and marks them published. A message lost between the confirm
/// and the commit is published again; consumers are idempotent.
/// </summary>
internal sealed class OutboxRelay(PsrBroker broker, PsrOutbox outbox, RabbitMqMetrics metrics, ILogger<OutboxRelay> logger) : BackgroundService
{
    private static readonly TimeSpan PendingRefresh = TimeSpan.FromSeconds(5);

    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = broker.Options;
        var lastPendingCount = Stopwatch.GetTimestamp() - (long)(PendingRefresh.TotalSeconds * Stopwatch.Frequency);
        var unreachableLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.RelayInterval;
            try
            {
                if (Stopwatch.GetElapsedTime(lastPendingCount) >= PendingRefresh)
                {
                    metrics.Pending(await outbox.CountPendingAsync(stoppingToken).ConfigureAwait(false));
                    lastPendingCount = Stopwatch.GetTimestamp();
                }

                var count = await outbox.DispatchAsync(options.RelayBatchSize, PublishAsync, stoppingToken).ConfigureAwait(false);
                unreachableLogged = false;
                if (count == options.RelayBatchSize)
                {
                    delay = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is BrokerUnreachableException or OperationInterruptedException or AlreadyClosedException or IOException
                or TimeoutException)
            {
                if (!unreachableLogged)
                {
                    Log.BrokerUnreachable(logger, ex.Message);
                    unreachableLogged = true;
                }

                await ResetChannelAsync().ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(2);
            }
            catch (PsrConfigurationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.RelayFailed(logger, ex);
                delay = TimeSpan.FromSeconds(2);
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        await ResetChannelAsync().ConfigureAwait(false);
    }

    /// <summary>Publishes a batch concurrently on one confirm channel; a missing channel or connection fails the whole batch.</summary>
    private async Task<IReadOnlyList<OutboxDelivery>> PublishAsync(IReadOnlyList<OutboxEnvelope> messages, CancellationToken cancellationToken)
    {
        var channel = await ChannelAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var pending = new List<(OutboxEnvelope Message, Task Publish, Activity? Activity)>(messages.Count);
        foreach (var message in messages)
        {
            message.Headers.TryGetValue("traceparent", out var traceParent);
            var activity = RabbitMqDiagnostics.StartFrom("psr.outbox.publish", ActivityKind.Producer, traceParent);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination.name", message.Exchange);
            activity?.SetTag("messaging.rabbitmq.destination.routing_key", message.RoutingKey);
            activity?.SetTag("messaging.message.id", message.Id.ToString());
            var headers = message.Headers.ToDictionary(h => h.Key, h => (object?)h.Value);
            headers["psr-available-at"] = message.AvailableAt.ToUnixTimeMilliseconds();
            var properties = new BasicProperties
            {
                MessageId = message.Id.ToString(),
                Type = message.MessageType,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Timestamp = new AmqpTimestamp(message.AvailableAt.ToUnixTimeSeconds()),
                Headers = headers,
            };
            pending.Add((message, channel.BasicPublishAsync(message.Exchange, message.RoutingKey, mandatory: false, properties,
                Encoding.UTF8.GetBytes(message.Payload), cancellationToken).AsTask(), activity));
        }

        try
        {
            await Task.WhenAll(pending.Select(p => p.Publish)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PublishException or OperationInterruptedException or AlreadyClosedException or IOException or TimeoutException
            or OperationCanceledException)
        {
            // Inspected per message below.
        }

        var results = new List<OutboxDelivery>(messages.Count);
        Exception? broken = null;
        foreach (var (message, publish, activity) in pending)
        {
            if (publish.IsCompletedSuccessfully)
            {
                results.Add(new OutboxDelivery(message.Id, null));
            }
            else if (publish.Exception?.InnerException is PublishException rejected)
            {
                activity?.SetStatus(ActivityStatusCode.Error, rejected.Message);
                Log.PublishFailed(logger, message.Id, message.MessageType, rejected.Message);
                results.Add(new OutboxDelivery(message.Id, $"The broker did not confirm the message: {rejected.Message}"));
            }
            else
            {
                broken ??= publish.Exception?.InnerException ?? new OperationCanceledException(cancellationToken);
            }

            activity?.Dispose();
        }

        if (broken is not null)
        {
            // The channel or the connection failed: nothing is recorded and the batch is published again later.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(broken);
        }

        metrics.BatchPublished(Stopwatch.GetElapsedTime(started), messages.Count);
        return results;
    }

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await ResetChannelAsync().ConfigureAwait(false);
        var connection = await broker.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken).ConfigureAwait(false);
        return _channel;
    }

    private async Task ResetChannelAsync()
    {
        if (_channel is { } channel)
        {
            _channel = null;
            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationInterruptedException or AlreadyClosedException or IOException or TimeoutException)
            {
                // Already closed.
            }
        }
    }
}
