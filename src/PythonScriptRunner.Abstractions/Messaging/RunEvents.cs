using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PythonScriptRunner;

/// <summary>
/// Event of a run (<c>psr.run-event.v1</c>), published on the <c>psr.events</c> exchange with the routing key
/// <c>&lt;type&gt;.&lt;pipeline key&gt;</c> or <c>&lt;type&gt;.stage:&lt;stage key&gt;</c>. Fields are only added within v1.
/// </summary>
public sealed record RunEvent
{
    /// <summary>Unique event identifier.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Event type (see <see cref="RunEventTypes"/>).</summary>
    public required string Type { get; init; }

    /// <summary>When it happened.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The run (<c>null</c> when a request was rejected before a run existed).</summary>
    public RunId? RunId { get; init; }

    /// <summary>What the run executes.</summary>
    public RunKind? Kind { get; init; }

    /// <summary>Pipeline key, for pipeline runs (and rejected pipeline requests).</summary>
    public string? PipelineKey { get; init; }

    /// <summary>Pipeline version, for pipeline runs.</summary>
    public int? PipelineVersion { get; init; }

    /// <summary>Stage key, for single-stage runs.</summary>
    public string? StageKey { get; init; }

    /// <summary>Stage version, for single-stage runs.</summary>
    public int? StageVersion { get; init; }

    /// <summary>Correlation given by the caller (e.g. the <c>correlationId</c> of <c>StartPipelineRun</c>).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>How the run started.</summary>
    public RunTrigger? Trigger { get; init; }

    /// <summary>Status of the run after the event.</summary>
    public RunStatus? Status { get; init; }

    /// <summary>The step, for <c>step.*</c> events.</summary>
    public RunEventStep? Step { get; init; }

    /// <summary>Failure (<c>step.failed</c>, <c>run.failed</c>, <c>run.rejected</c>).</summary>
    public RunEventError? Error { get; init; }

    /// <summary>
    /// Declared outputs, for <c>run.succeeded</c>: values inline, files as <c>{"$artifact": {id, name, contentType, size}}</c>.
    /// <c>null</c> when larger than 256 KB (see <see cref="OutputsTruncated"/>).
    /// </summary>
    public JsonObject? Outputs { get; init; }

    /// <summary>Whether <see cref="Outputs"/> was omitted because it was too large.</summary>
    public bool OutputsTruncated { get; init; }

    /// <summary>When the next attempt starts, for <c>step.retry-scheduled</c>.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>Details of the trigger (schedule id, scheduled time, missed occurrences).</summary>
    public JsonObject? TriggerDetails { get; init; }

    /// <summary>The target used in the routing key: the pipeline key or <c>stage:&lt;stage key&gt;</c>.</summary>
    [JsonIgnore]
    public string RoutingTarget => PipelineKey ?? (StageKey is { } stage ? $"stage:{stage}" : "unknown");
}

/// <summary>The step of a <c>step.*</c> event.</summary>
/// <param name="Key">Step key.</param>
/// <param name="Order">1-based position.</param>
/// <param name="Attempt">Attempt number.</param>
public sealed record RunEventStep(string Key, int Order, int Attempt);

/// <summary>Failure carried by an event (no traceback).</summary>
/// <param name="Kind">Error kind (an <see cref="ErrorKind"/> name, or <c>Rejected</c>).</param>
/// <param name="Code">Stable code.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Step">Key of the step that failed.</param>
public sealed record RunEventError(string Kind, string? Code, string Message, string? Step)
{
    /// <summary>Why a request was rejected (<c>run.rejected</c>).</summary>
    public IReadOnlyList<string>? Reasons { get; init; }

    /// <summary>Converts a run error.</summary>
    /// <param name="error">The error.</param>
    public static RunEventError? From(RunError? error) =>
        error is null ? null : new(error.Kind.ToString(), error.Code, error.Message, error.StepKey);
}

/// <summary>Types of <see cref="RunEvent"/>.</summary>
public static class RunEventTypes
{
    /// <summary>A run was queued (or a duplicate request returned it).</summary>
    public const string RunQueued = "run.queued";

    /// <summary>A queue command or a schedule was rejected; no run was created.</summary>
    public const string RunRejected = "run.rejected";

    /// <summary>The first step of a run started.</summary>
    public const string RunStarted = "run.started";

    /// <summary>An attempt of a step started.</summary>
    public const string StepStarted = "step.started";

    /// <summary>A step succeeded.</summary>
    public const string StepSucceeded = "step.succeeded";

    /// <summary>A step failed and will be retried automatically.</summary>
    public const string StepRetryScheduled = "step.retry-scheduled";

    /// <summary>A step failed for good.</summary>
    public const string StepFailed = "step.failed";

    /// <summary>A run succeeded.</summary>
    public const string RunSucceeded = "run.succeeded";

    /// <summary>A run failed.</summary>
    public const string RunFailed = "run.failed";

    /// <summary>A run was cancelled.</summary>
    public const string RunCancelled = "run.cancelled";
}

/// <summary>Receives run events from a RabbitMQ subscription (registered with <c>AddRunEventHandler</c>).</summary>
public interface IRunEventHandler
{
    /// <summary>Handles one event. Throwing makes the broker deliver it again (up to the delivery limit).</summary>
    /// <param name="runEvent">The event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task HandleAsync(RunEvent runEvent, CancellationToken cancellationToken);
}
