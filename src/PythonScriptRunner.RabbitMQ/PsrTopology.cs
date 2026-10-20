using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>Exchanges, queues and routing keys of PythonScriptRunner (spec 005).</summary>
public static class PsrTopology
{
    /// <summary>Internal commands (direct).</summary>
    public const string CommandsExchange = "psr.commands";

    /// <summary>Public commands (direct); external systems only need write permission here.</summary>
    public const string PublicExchange = "psr.public";

    /// <summary>Run events (topic): <c>&lt;event&gt;.&lt;pipeline key&gt;</c> or <c>&lt;event&gt;.stage:&lt;stage key&gt;</c>.</summary>
    public const string EventsExchange = "psr.events";

    /// <summary>Control messages to every instance (fanout).</summary>
    public const string ControlExchange = "psr.control";

    /// <summary>Dead letters (fanout).</summary>
    public const string DeadLetterExchange = "psr.dlx";

    /// <summary>One message per step to execute.</summary>
    public const string ExecuteStepQueue = "psr.step.execute";

    /// <summary>Routing key of <see cref="ExecuteStepQueue"/>.</summary>
    public const string ExecuteStepRoutingKey = "step.execute";

    /// <summary>Public <c>StartPipelineRun</c> commands.</summary>
    public const string StartPipelineQueue = "psr.pipeline.start";

    /// <summary>Routing key of <see cref="StartPipelineQueue"/>.</summary>
    public const string StartPipelineRoutingKey = "pipeline.start";

    /// <summary>Messages that could not be processed (manual inspection).</summary>
    public const string DeadLetterQueue = "psr.dead-letter";

    /// <summary>Control queue of an instance (exclusive, auto-delete).</summary>
    /// <param name="instanceId">Instance identifier.</param>
    public static string ControlQueue(string instanceId) => $"psr.control.{instanceId}";

    /// <summary>Durable queue of a named event subscription.</summary>
    /// <param name="name">Subscription name.</param>
    public static string SubscriberQueue(string name) => $"psr.subscriber.{name}";

    /// <summary>Exclusive event queue of one instance (e.g. <c>psr.dashboard.&lt;instance&gt;</c>).</summary>
    /// <param name="prefix">Queue prefix.</param>
    /// <param name="instanceId">Instance identifier.</param>
    public static string InstanceQueue(string prefix, string instanceId) => $"psr.{prefix}.{instanceId}";

    /// <summary>Arguments of a quorum command queue with a delivery limit and dead-lettering.</summary>
    /// <param name="deliveryLimit">Deliveries before dead-lettering.</param>
    internal static Dictionary<string, object?> QuorumArguments(int deliveryLimit) => new()
    {
        ["x-queue-type"] = "quorum",
        ["x-delivery-limit"] = deliveryLimit,
        ["x-dead-letter-exchange"] = DeadLetterExchange,
    };

    /// <summary>Declares (idempotently) or checks the shared topology.</summary>
    /// <param name="connection">Open connection.</param>
    /// <param name="options">Transport settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="PsrConfigurationException">Checking only and something is missing.</exception>
    internal static async Task EnsureAsync(IConnection connection, RabbitMqOptions options, CancellationToken cancellationToken)
    {
        (string Name, string Type)[] exchanges =
        [
            (CommandsExchange, ExchangeType.Direct),
            (PublicExchange, ExchangeType.Direct),
            (EventsExchange, ExchangeType.Topic),
            (ControlExchange, ExchangeType.Fanout),
            (DeadLetterExchange, ExchangeType.Fanout),
        ];
        (string Name, IDictionary<string, object?> Arguments, string Exchange, string RoutingKey)[] queues =
        [
            (ExecuteStepQueue, QuorumArguments(options.DeliveryLimit), CommandsExchange, ExecuteStepRoutingKey),
            (StartPipelineQueue, QuorumArguments(options.DeliveryLimit), PublicExchange, StartPipelineRoutingKey),
            (DeadLetterQueue, new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, DeadLetterExchange, string.Empty),
        ];

        if (options.DeclareTopology)
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (channel.ConfigureAwait(false))
            {
                foreach (var (name, type) in exchanges)
                {
                    await channel.ExchangeDeclareAsync(name, type, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                foreach (var (name, arguments, exchange, routingKey) in queues)
                {
                    await channel.QueueDeclareAsync(name, durable: true, exclusive: false, autoDelete: false, arguments, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    await channel.QueueBindAsync(name, exchange, routingKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        var missing = new List<string>();
        foreach (var (name, _) in exchanges)
        {
            if (!await ExistsAsync(connection, channel => channel.ExchangeDeclarePassiveAsync(name, cancellationToken), cancellationToken).ConfigureAwait(false))
            {
                missing.Add($"exchange '{name}'");
            }
        }

        foreach (var (name, _, _, _) in queues)
        {
            if (!await ExistsAsync(connection, async channel => await channel.QueueDeclarePassiveAsync(name, cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false))
            {
                missing.Add($"queue '{name}'");
            }
        }

        if (missing.Count > 0)
        {
            throw new PsrConfigurationException(
                $"The RabbitMQ topology is incomplete and DeclareTopology is false: missing {string.Join(", ", missing)}. " +
                "Create them (see the documentation of spec 005) or set DeclareTopology = true.");
        }
    }

    /// <summary>A passive declaration closes its channel when the entity is missing, so each check uses its own channel.</summary>
    internal static async Task<bool> ExistsAsync(IConnection connection, Func<IChannel, Task> check, CancellationToken cancellationToken)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            try
            {
                await check(channel).ConfigureAwait(false);
                return true;
            }
            catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == Constants.NotFound)
            {
                return false;
            }
        }
    }
}
