using System.Text.Json;
using PythonScriptRunner.EntityFrameworkCore;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>Maps run messages to outbox rows with their RabbitMQ destination (published by <see cref="OutboxRelay"/>).</summary>
internal sealed class RabbitMqRunMessageOutbox(IOutboxWriter outbox) : IRunMessageOutbox
{
    /// <summary>Version of the message contracts (header <c>psr-schema-version</c>).</summary>
    public const string SchemaVersion = "1";

    public bool IsEnabled => true;

    public void ExecuteStep(PsrDbContext db, ExecuteStepCommand command, DateTimeOffset availableAt, string? traceParent) =>
        outbox.Enqueue(db, new OutboxMessage(PsrMessageTypes.ExecuteStep, PsrTopology.CommandsExchange, PsrTopology.ExecuteStepRoutingKey, Json(command))
        {
            AvailableAt = availableAt,
            Headers = Headers(traceParent),
        });

    public void CancelRun(PsrDbContext db, CancelRunCommand command, DateTimeOffset now) =>
        outbox.Enqueue(db, new OutboxMessage(PsrMessageTypes.CancelRun, PsrTopology.ControlExchange, string.Empty, Json(command))
        {
            AvailableAt = now,
            Headers = Headers(null),
        });

    public void Publish(PsrDbContext db, RunEvent runEvent, string? traceParent) =>
        outbox.Enqueue(db, new OutboxMessage(PsrMessageTypes.RunEvent, PsrTopology.EventsExchange, $"{runEvent.Type}.{runEvent.RoutingTarget}", Json(runEvent))
        {
            Id = runEvent.EventId,
            AvailableAt = runEvent.OccurredAt,
            Headers = Headers(traceParent),
        });

    private static System.Text.Json.Nodes.JsonNode Json<T>(T message) => JsonSerializer.SerializeToNode(message, PsrMessageJson.Options)!;

    private static Dictionary<string, string> Headers(string? traceParent)
    {
        var headers = new Dictionary<string, string> { ["psr-schema-version"] = SchemaVersion };
        if (traceParent is not null)
        {
            headers["traceparent"] = traceParent;
        }

        return headers;
    }
}
