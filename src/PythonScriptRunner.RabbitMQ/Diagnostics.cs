using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>Tracing of the transport (source <c>PythonScriptRunner</c>, shared by every package).</summary>
internal static class RabbitMqDiagnostics
{
    public static readonly ActivitySource Source = new("PythonScriptRunner");

    /// <summary>Starts an activity whose parent is the W3C <c>traceparent</c> carried by a message (the trace continues across processes).</summary>
    public static Activity? StartFrom(string name, ActivityKind kind, string? traceParent) =>
        ActivityContext.TryParse(traceParent, null, out var parent)
            ? Source.StartActivity(name, kind, parent)
            : Source.StartActivity(name, kind);
}

/// <summary>Metrics of the transport (meter <c>PythonScriptRunner</c>).</summary>
internal sealed class RabbitMqMetrics
{
    private readonly Histogram<double> _publishDuration;
    private int _pending;

    public RabbitMqMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("PythonScriptRunner");
        _publishDuration = meter.CreateHistogram<double>("psr.outbox.publish.duration", "ms", "Time to publish one outbox batch with confirms.");
        meter.CreateObservableGauge("psr.outbox.pending", () => Volatile.Read(ref _pending), description: "Outbox messages not published yet.");
    }

    public void BatchPublished(TimeSpan elapsed, int count) =>
        _publishDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("psr.messages", count));

    public void Pending(int count) => Volatile.Write(ref _pending, count);
}

/// <summary>Source-generated log messages.</summary>
internal static partial class Log
{
    [LoggerMessage(100, LogLevel.Information, "Connected to RabbitMQ at {Endpoint} (declare topology: {DeclareTopology})")]
    public static partial void Connected(ILogger logger, string endpoint, bool declareTopology);

    [LoggerMessage(101, LogLevel.Warning, "RabbitMQ is unreachable ({Reason}); queued runs stay in the outbox until it comes back")]
    public static partial void BrokerUnreachable(ILogger logger, string reason);

    [LoggerMessage(102, LogLevel.Critical, "The RabbitMQ topology is invalid; stopping the application")]
    public static partial void TopologyInvalid(ILogger logger, Exception exception);

    [LoggerMessage(103, LogLevel.Warning, "Outbox message {MessageId} ({MessageType}) was not published: {Error}")]
    public static partial void PublishFailed(ILogger logger, Guid messageId, string messageType, string error);

    [LoggerMessage(104, LogLevel.Error, "Message {MessageId} on {Queue} is invalid and goes to the dead-letter queue: {Reason}")]
    public static partial void InvalidMessage(ILogger logger, string? messageId, string queue, string reason);

    [LoggerMessage(105, LogLevel.Warning, "Handling message {MessageId} on {Queue} failed; it will be delivered again")]
    public static partial void HandlerFailed(ILogger logger, string? messageId, string queue, Exception exception);

    [LoggerMessage(106, LogLevel.Warning, "Consumer of {Queue} stopped ({Reason}); restarting")]
    public static partial void ConsumerRestarting(ILogger logger, string queue, string reason);

    [LoggerMessage(107, LogLevel.Information, "StartPipelineRun for '{PipelineKey}' rejected: {Reason}")]
    public static partial void CommandRejected(ILogger logger, string pipelineKey, string reason);

    [LoggerMessage(108, LogLevel.Error, "The outbox relay failed")]
    public static partial void RelayFailed(ILogger logger, Exception exception);
}
