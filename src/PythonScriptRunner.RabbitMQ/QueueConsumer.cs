using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>What to do with a delivery after handling it.</summary>
public enum DeliveryOutcome
{
    /// <summary>Done (<c>basic.ack</c>).</summary>
    Ack,

    /// <summary>Deliver it again later (<c>basic.nack</c> with requeue; quorum queues count it toward the delivery limit).</summary>
    Requeue,

    /// <summary>Send it to <c>psr.dead-letter</c> (<c>basic.nack</c> without requeue).</summary>
    DeadLetter,

    /// <summary>Leave it unacknowledged: the broker delivers it again when the channel closes (shutdown past the grace period).</summary>
    Abandon,
}

/// <summary>A message received from a queue.</summary>
/// <param name="Queue">Queue it came from.</param>
/// <param name="MessageId">AMQP <c>message_id</c>.</param>
/// <param name="Type">AMQP <c>type</c> (versioned message type).</param>
/// <param name="RoutingKey">Routing key it was published with.</param>
/// <param name="Redelivered">Whether the broker delivered it before.</param>
/// <param name="Headers">Headers as text.</param>
/// <param name="Body">Body (JSON).</param>
public sealed record PsrDelivery(
    string Queue, string? MessageId, string? Type, string RoutingKey, bool Redelivered, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body)
{
    /// <summary>W3C trace context of the publisher.</summary>
    public string? TraceParent => Headers.GetValueOrDefault("traceparent");
}

/// <summary>How to consume a queue.</summary>
public sealed record QueueConsumerSettings
{
    /// <summary>Queue name.</summary>
    public required string Queue { get; init; }

    /// <summary>Unacknowledged deliveries at a time; also how many are handled concurrently. Default: 1.</summary>
    public ushort Prefetch { get; init; } = 1;

    /// <summary>Declares the queue and its bindings before consuming (queues owned by this instance or subscription).</summary>
    public Func<IChannel, CancellationToken, Task>? Declare { get; init; }
}

/// <summary>
/// Consumes a queue with manual acknowledgements, up to <see cref="QueueConsumerSettings.Prefetch"/> deliveries in parallel.
/// The channel is recreated when it closes for good; connection losses are recovered by the client. A failing handler makes
/// the message be delivered again; <see cref="StopAsync"/> stops receiving and waits for the deliveries in progress.
/// </summary>
public sealed class QueueConsumer : IAsyncDisposable
{
    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(1);

    private readonly PsrBroker _broker;
    private readonly QueueConsumerSettings _settings;
    private readonly Func<PsrDelivery, CancellationToken, Task<DeliveryOutcome>> _handler;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _handlers = new();
    private IChannel? _channel;
    private string? _consumerTag;
    private int _inFlight;
    private volatile bool _stopping;

    /// <summary>Creates the consumer (nothing is consumed before <see cref="RunAsync"/>).</summary>
    /// <param name="broker">Connection.</param>
    /// <param name="settings">Queue and concurrency.</param>
    /// <param name="handler">Handles one delivery; its token is cancelled only when a shutdown exceeds the grace period.</param>
    /// <param name="logger">Logger.</param>
    public QueueConsumer(PsrBroker broker, QueueConsumerSettings settings, Func<PsrDelivery, CancellationToken, Task<DeliveryOutcome>> handler, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(logger);
        _broker = broker;
        _settings = settings;
        _handler = handler;
        _logger = logger;
    }

    /// <summary>Deliveries being handled now.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Whether the consumer is registered on an open channel.</summary>
    public bool IsConsuming => _channel is { IsOpen: true } && _consumerTag is not null && !_stopping;

    /// <summary>Consumes until <paramref name="stoppingToken"/> is cancelled or <see cref="StopAsync"/> is called.</summary>
    /// <param name="stoppingToken">Stops the loop.</param>
    /// <exception cref="PsrConfigurationException">The topology is invalid.</exception>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested && !_stopping)
        {
            try
            {
                var connection = await _broker.ConnectAsync(stoppingToken).ConfigureAwait(false);
                var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false,
                        consumerDispatchConcurrency: _settings.Prefetch),
                    stoppingToken).ConfigureAwait(false);
                _channel = channel;
                if (_settings.Declare is { } declare)
                {
                    await declare(channel, stoppingToken).ConfigureAwait(false);
                }

                await channel.BasicQosAsync(0, _settings.Prefetch, false, stoppingToken).ConfigureAwait(false);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, args) => OnReceivedAsync(channel, args);
                _consumerTag = await channel.BasicConsumeAsync(_settings.Queue, autoAck: false, consumer, stoppingToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(1);

                // The client recovers channels after connection losses; recreate only a channel closed while the connection is up.
                while (!_stopping && !(channel.IsClosed && connection.IsOpen))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }

                if (!_stopping)
                {
                    Log.ConsumerRestarting(_logger, _settings.Queue, channel.CloseReason?.ReplyText ?? "channel closed");
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (PsrConfigurationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is BrokerUnreachableException or OperationInterruptedException or AlreadyClosedException or IOException
                or TimeoutException)
            {
                Log.ConsumerRestarting(_logger, _settings.Queue, ex.Message);
            }

            if (_stopping)
            {
                return;
            }

            try
            {
                await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    /// <summary>
    /// Stops receiving (<c>basic.cancel</c>), waits up to <paramref name="gracePeriod"/> for the deliveries in progress, then
    /// cancels their handlers (which abandon them) and closes the channel.
    /// </summary>
    /// <param name="gracePeriod">How long deliveries in progress may run.</param>
    /// <param name="cancellationToken">Ends the wait early (host shutdown timeout).</param>
    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        _stopping = true;
        if (_channel is { IsOpen: true } channel && _consumerTag is { } tag)
        {
            try
            {
                await channel.BasicCancelAsync(tag, noWait: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationInterruptedException or AlreadyClosedException or IOException or TimeoutException)
            {
                // The channel is going away anyway.
            }
        }

        await WaitIdleAsync(gracePeriod, cancellationToken).ConfigureAwait(false);
        await _handlers.CancelAsync().ConfigureAwait(false);
        await WaitIdleAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        if (_channel is { } open)
        {
            try
            {
                await open.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationInterruptedException or AlreadyClosedException or IOException or TimeoutException)
            {
                // Already closed.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        await _handlers.CancelAsync().ConfigureAwait(false);
        if (_channel is { } channel)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _handlers.Dispose();
    }

    private async Task WaitIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (InFlight > 0)
            {
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Grace period over.
        }
    }

    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs args)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            var properties = args.BasicProperties;
            var delivery = new PsrDelivery(
                _settings.Queue, properties.MessageId, properties.Type, args.RoutingKey, args.Redelivered, Headers(properties.Headers), args.Body.ToArray());
            DeliveryOutcome outcome;
            try
            {
                outcome = await _handler(delivery, _handlers.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_handlers.IsCancellationRequested)
            {
                outcome = DeliveryOutcome.Abandon;
            }
            catch (Exception ex)
            {
                Log.HandlerFailed(_logger, delivery.MessageId, _settings.Queue, ex);
                outcome = DeliveryOutcome.Requeue;
            }

            if (outcome == DeliveryOutcome.Requeue && !_stopping)
            {
                // Spaces out redeliveries of a failing message (e.g. while the database is down).
                await Task.Delay(RequeueDelay, CancellationToken.None).ConfigureAwait(false);
            }

            await SettleAsync(channel, args.DeliveryTag, outcome).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private static async Task SettleAsync(IChannel channel, ulong deliveryTag, DeliveryOutcome outcome)
    {
        try
        {
            switch (outcome)
            {
                case DeliveryOutcome.Ack:
                    await channel.BasicAckAsync(deliveryTag, multiple: false, CancellationToken.None).ConfigureAwait(false);
                    break;
                case DeliveryOutcome.Requeue:
                    await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, CancellationToken.None).ConfigureAwait(false);
                    break;
                case DeliveryOutcome.DeadLetter:
                    await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is OperationInterruptedException or AlreadyClosedException or IOException or TimeoutException)
        {
            // The channel closed: the broker delivers the message again and the handlers are idempotent.
        }
    }

    private static Dictionary<string, string> Headers(IDictionary<string, object?>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in headers ?? new Dictionary<string, object?>())
        {
            result[key] = value switch
            {
                null => string.Empty,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            };
        }

        return result;
    }
}
