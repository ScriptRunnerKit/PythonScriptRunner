using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>Configures a run event subscription (<c>AddRunEventHandler</c>).</summary>
public sealed partial class RunEventSubscriptionBuilder
{
    private readonly List<string> _patterns = [];
    private string? _name;
    private string? _instancePrefix;
    private ushort _concurrency = 1;

    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Durable subscription: queue <c>psr.subscriber.&lt;name&gt;</c> shared by every instance (each event is handled once) and
    /// kept while the application is down.
    /// </summary>
    /// <param name="name">Subscription name (lowercase letters, digits, '-' and '.').</param>
    public RunEventSubscriptionBuilder Subscription(string name)
    {
        _name = Validate(name);
        _instancePrefix = null;
        return this;
    }

    /// <summary>
    /// Per-instance subscription: exclusive, auto-deleted queue <c>psr.&lt;prefix&gt;.&lt;instance&gt;</c>, so every instance
    /// receives every event while it runs (e.g. the dashboard pushing updates to its browsers; D-110).
    /// </summary>
    /// <param name="prefix">Queue prefix (e.g. <c>dashboard</c>).</param>
    public RunEventSubscriptionBuilder PerInstance(string prefix)
    {
        _instancePrefix = Validate(prefix);
        _name = null;
        return this;
    }

    /// <summary>
    /// Routing key patterns of the events (topic syntax: <c>*</c> one word, <c>#</c> zero or more), e.g.
    /// <c>run.succeeded.calculo-progressao</c> or <c>run.failed.*</c>. Default: every event (<c>#</c>).
    /// </summary>
    /// <param name="patterns">Patterns.</param>
    public RunEventSubscriptionBuilder On(params string[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns.AddRange(patterns.Where(p => !string.IsNullOrWhiteSpace(p)));
        return this;
    }

    /// <summary>Events handled in parallel (default: 1, which keeps the order of delivery).</summary>
    /// <param name="concurrency">1-256.</param>
    public RunEventSubscriptionBuilder WithConcurrency(int concurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(concurrency, 256);
        _concurrency = (ushort)concurrency;
        return this;
    }

    internal RunEventSubscription Build(Type handlerType)
    {
        if (_name is null && _instancePrefix is null)
        {
            throw new PsrConfigurationException($"The subscription of {handlerType.Name} needs Subscription(name) or PerInstance(prefix).");
        }

        return new RunEventSubscription(handlerType, _name, _instancePrefix, _patterns.Count > 0 ? [.. _patterns.Distinct()] : ["#"], _concurrency);
    }

    private static string Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Length <= 64 && NamePattern().IsMatch(name)
            ? name
            : throw new PsrConfigurationException($"'{name}' is not a valid subscription name (lowercase letters, digits, '-' and '.'; up to 64 chars).");
    }
}

/// <summary>A registered subscription.</summary>
internal sealed record RunEventSubscription(Type HandlerType, string? Name, string? InstancePrefix, IReadOnlyList<string> Patterns, ushort Concurrency)
{
    public string Queue(string instanceId) => Name is not null ? PsrTopology.SubscriberQueue(Name) : PsrTopology.InstanceQueue(InstancePrefix!, instanceId);
}

/// <summary>Consumes the queue of one subscription and calls its <see cref="IRunEventHandler"/> (resolved in a scope per event).</summary>
internal sealed class RunEventSubscriptionService(
    PsrBroker broker, RunEventSubscription subscription, IServiceScopeFactory scopes, ILogger<RunEventSubscriptionService> logger)
    : ConsumerService(broker, logger)
{
    protected override QueueConsumerSettings Settings()
    {
        var queue = subscription.Queue(Broker.InstanceId);
        var durable = subscription.Name is not null;
        return new()
        {
            Queue = queue,
            Prefetch = subscription.Concurrency,
            Declare = async (channel, cancellationToken) =>
            {
                if (durable && !Broker.Options.DeclareTopology)
                {
                    if (!await PsrTopology.ExistsAsync(await Broker.ConnectAsync(cancellationToken).ConfigureAwait(false),
                            async check => await check.QueueDeclarePassiveAsync(queue, cancellationToken).ConfigureAwait(false), cancellationToken)
                        .ConfigureAwait(false))
                    {
                        throw new PsrConfigurationException($"The queue '{queue}' of subscription {subscription.HandlerType.Name} is missing and DeclareTopology is false.");
                    }

                    return;
                }

                await channel.QueueDeclareAsync(queue, durable, exclusive: !durable, autoDelete: !durable,
                    durable ? PsrTopology.QuorumArguments(Broker.Options.DeliveryLimit) : null, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var pattern in subscription.Patterns)
                {
                    await channel.QueueBindAsync(queue, PsrTopology.EventsExchange, pattern, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            },
        };
    }

    protected override async Task<DeliveryOutcome> HandleAsync(PsrDelivery delivery, CancellationToken cancellationToken)
    {
        if (Read<RunEvent>(delivery, PsrMessageTypes.RunEvent) is not { } runEvent)
        {
            return DeliveryOutcome.DeadLetter;
        }

        using var activity = RabbitMqDiagnostics.StartFrom("psr.message.consume", ActivityKind.Consumer, delivery.TraceParent);
        activity?.SetTag("messaging.destination.name", delivery.Queue);
        activity?.SetTag("psr.event.type", runEvent.Type);
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var handler = (IRunEventHandler)scope.ServiceProvider.GetRequiredService(subscription.HandlerType);
            await handler.HandleAsync(runEvent, cancellationToken).ConfigureAwait(false);
        }

        return DeliveryOutcome.Ack;
    }
}

/// <summary>Starts one consumer per registered subscription.</summary>
internal sealed class RunEventSubscriptionHost(IEnumerable<RunEventSubscription> subscriptions, IServiceProvider services) : IHostedService
{
    private readonly List<RunEventSubscriptionService> _consumers = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in subscriptions)
        {
            var consumer = ActivatorUtilities.CreateInstance<RunEventSubscriptionService>(services, subscription);
            _consumers.Add(consumer);
            await consumer.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll(_consumers.Select(c => c.StopAsync(cancellationToken))).ConfigureAwait(false);
}
