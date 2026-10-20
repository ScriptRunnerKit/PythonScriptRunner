using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PythonScriptRunner.EntityFrameworkCore;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>Runs one <see cref="QueueConsumer"/> for the lifetime of the host.</summary>
internal abstract class ConsumerService(PsrBroker broker, ILogger logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private QueueConsumer? _consumer;
    private Task? _running;

    protected PsrBroker Broker { get; } = broker;

    protected ILogger Logger { get; } = logger;

    /// <summary>How long deliveries in progress may finish when the host stops.</summary>
    protected virtual TimeSpan GracePeriod => TimeSpan.FromSeconds(30);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _consumer = new QueueConsumer(Broker, Settings(), HandleAsync, Logger);
        _running = Task.Run(() => _consumer.RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A host can be stopped more than once (e.g. stop, then dispose).
        if (Interlocked.Exchange(ref _consumer, null) is not { } consumer || _running is not { } running)
        {
            return;
        }

        await consumer.StopAsync(GracePeriod, cancellationToken).ConfigureAwait(false);
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

    protected abstract QueueConsumerSettings Settings();

    protected abstract Task<DeliveryOutcome> HandleAsync(PsrDelivery delivery, CancellationToken cancellationToken);

    /// <summary>Reads the JSON body; <c>null</c> (logged) when it is not valid.</summary>
    protected T? Read<T>(PsrDelivery delivery, string expectedType)
        where T : class
    {
        if (delivery.Type is { Length: > 0 } type && type != expectedType)
        {
            Log.InvalidMessage(Logger, delivery.MessageId, delivery.Queue, $"unknown message type '{type}'");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(delivery.Body.Span, PsrMessageJson.Options)
                ?? throw new JsonException("The body is null.");
        }
        catch (JsonException ex)
        {
            Log.InvalidMessage(Logger, delivery.MessageId, delivery.Queue, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Consumes the public <c>StartPipelineRun</c> command (D-72): valid commands create a run (<c>run.queued</c>); unknown
/// pipelines, invalid inputs and file inputs produce <c>run.rejected</c> with the reasons. Both carry the caller's
/// <c>correlationId</c>.
/// </summary>
internal sealed class StartPipelineRunService(
    PsrBroker broker, ICatalog catalog, IRunEngine engine, AsyncRunLifecycle lifecycle, ILogger<StartPipelineRunService> logger)
    : ConsumerService(broker, logger)
{
    private const int MaxTextLength = 200;

    protected override QueueConsumerSettings Settings() => new() { Queue = PsrTopology.StartPipelineQueue, Prefetch = 8 };

    protected override async Task<DeliveryOutcome> HandleAsync(PsrDelivery delivery, CancellationToken cancellationToken)
    {
        var command = Read<StartPipelineRunCommand>(delivery, PsrMessageTypes.StartPipelineRun);
        if (command is null || string.IsNullOrWhiteSpace(command.PipelineKey))
        {
            return DeliveryOutcome.DeadLetter;
        }

        using var activity = RabbitMqDiagnostics.StartFrom("psr.message.consume", ActivityKind.Consumer, delivery.TraceParent);
        activity?.SetTag("messaging.destination.name", delivery.Queue);
        activity?.SetTag("psr.pipeline.key", command.PipelineKey);
        try
        {
            if (new[] { command.RequestedBy, command.CorrelationId, command.IdempotencyKey }.Any(text => text?.Length > MaxTextLength - 10))
            {
                throw new PsrDomainException("TEXT_TOO_LONG", $"requestedBy, correlationId and idempotencyKey must have up to {MaxTextLength - 10} chars.");
            }

            var pipeline = await catalog.FindPipelineAsync(command.PipelineKey, command.Version, cancellationToken).ConfigureAwait(false)
                ?? throw (command.Version is { } version ? new VersionNotFoundException(command.PipelineKey, version) : new PipelineNotFoundException(command.PipelineKey));
            var inputs = command.Inputs ?? [];
            var files = pipeline.Inputs.Where(f => f.Type.IsFile && (f.Required || inputs.ContainsKey(f.Name))).Select(f => f.Name).ToList();
            if (files.Count > 0)
            {
                throw new PsrDomainException(PsrErrorCodes.FileInputNotAllowed,
                    $"Pipeline '{pipeline.Key}' has file inputs ({string.Join(", ", files)}); the queue command cannot send files.");
            }

            await lifecycle.CreateAsync(new NewAsyncRun
            {
                Kind = RunKind.Pipeline,
                Pipeline = pipeline,
                Inputs = engine.BindInputs(pipeline.Inputs, (JsonObject)inputs.DeepClone()),
                Trigger = RunTrigger.QueueCommand,
                TriggeredBy = command.RequestedBy is { Length: > 0 } requestedBy ? $"queue:{requestedBy}" : "system:queue",
                IdempotencyKey = command.IdempotencyKey,
                ImplicitIdempotencyKey = delivery.MessageId is { Length: > 0 } messageId ? $"message:{messageId}" : null,
                CorrelationId = command.CorrelationId,
                TraceParent = Activity.Current?.Id ?? delivery.TraceParent,
                AnnounceDuplicate = true,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PsrException and not PsrConfigurationException)
        {
            Log.CommandRejected(Logger, command.PipelineKey, ex.Message);
            await lifecycle.PublishRejectedAsync(new RunEvent
            {
                EventId = Guid.CreateVersion7(),
                Type = RunEventTypes.RunRejected,
                OccurredAt = DateTimeOffset.UtcNow,
                Kind = RunKind.Pipeline,
                PipelineKey = command.PipelineKey,
                CorrelationId = command.CorrelationId,
                Trigger = RunTrigger.QueueCommand,
                Error = AsyncRunLifecycle.Rejection(ex),
            }, cancellationToken).ConfigureAwait(false);
        }

        return DeliveryOutcome.Ack;
    }
}

/// <summary>Consumes the control queue of this instance: <c>CancelRun</c> stops the processes of the run if it executes here.</summary>
internal sealed class ControlService(PsrBroker broker, IRunEngine engine, ILogger<ControlService> logger) : ConsumerService(broker, logger)
{
    protected override TimeSpan GracePeriod => TimeSpan.FromSeconds(5);

    protected override QueueConsumerSettings Settings()
    {
        var queue = PsrTopology.ControlQueue(Broker.InstanceId);
        return new()
        {
            Queue = queue,
            Prefetch = 4,
            Declare = async (channel, cancellationToken) =>
            {
                await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await channel.QueueBindAsync(queue, PsrTopology.ControlExchange, string.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
            },
        };
    }

    protected override Task<DeliveryOutcome> HandleAsync(PsrDelivery delivery, CancellationToken cancellationToken)
    {
        if (Read<CancelRunCommand>(delivery, PsrMessageTypes.CancelRun) is not { } command)
        {
            return Task.FromResult(DeliveryOutcome.DeadLetter);
        }

        engine.RequestCancellation(command.RunId);
        return Task.FromResult(DeliveryOutcome.Ack);
    }
}
