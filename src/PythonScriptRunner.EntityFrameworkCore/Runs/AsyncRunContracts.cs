using System.Text.Json.Nodes;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>A run to queue with <see cref="AsyncRunLifecycle.CreateAsync(NewAsyncRun, CancellationToken)"/>.</summary>
public sealed record NewAsyncRun
{
    /// <summary><see cref="RunKind.Pipeline"/> or <see cref="RunKind.Stage"/>.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>The stage, for single-stage runs.</summary>
    public StageDefinition? Stage { get; init; }

    /// <summary>The pipeline, for pipeline runs.</summary>
    public PipelineDefinition? Pipeline { get; init; }

    /// <summary>Validated inputs (see <see cref="IRunEngine.BindInputs"/>).</summary>
    public required BoundRunInputs Inputs { get; init; }

    /// <summary>How the run started.</summary>
    public RunTrigger Trigger { get; init; } = RunTrigger.Library;

    /// <summary>Who requested it (a user, <c>system:schedule:&lt;id&gt;</c>, <c>queue:&lt;system&gt;</c>...).</summary>
    public required string TriggeredBy { get; init; }

    /// <summary>Deduplication key given by the caller (stored with the run).</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Deduplication key used when the caller gave none (e.g. the AMQP <c>message_id</c>; not stored with the run).</summary>
    public string? ImplicitIdempotencyKey { get; init; }

    /// <summary>Correlation of the caller, returned in the events.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>W3C trace context of the caller (the worker activities become its children).</summary>
    public string? TraceParent { get; init; }

    /// <summary>Schedule that fired the run.</summary>
    public ScheduleId? ScheduleId { get; init; }

    /// <summary>Details of the trigger (e.g. missed occurrences of a schedule).</summary>
    public JsonObject? TriggerDetails { get; init; }

    /// <summary>Publish <c>run.queued</c> again when an earlier run is returned for the same key (queue commands).</summary>
    public bool AnnounceDuplicate { get; init; }
}

/// <summary>A step claimed by a worker: the attempt is recorded with a lease and everything needed to execute it is loaded.</summary>
public sealed record ClaimedStep
{
    /// <summary>Run identifier.</summary>
    public required RunId RunId { get; init; }

    /// <summary>What the run executes.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>Step identifier.</summary>
    public required StepRunId StepRunId { get; init; }

    /// <summary>1-based position.</summary>
    public required int Order { get; init; }

    /// <summary>Step key.</summary>
    public required string StepKey { get; init; }

    /// <summary>Attempt identifier.</summary>
    public required AttemptId AttemptId { get; init; }

    /// <summary>Attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>Why the attempt started.</summary>
    public required AttemptCause Cause { get; init; }

    /// <summary>Who started the attempt.</summary>
    public required string TriggeredBy { get; init; }

    /// <summary>Worker that holds the lease.</summary>
    public required string WorkerId { get; init; }

    /// <summary>Exact stage version of the step.</summary>
    public required StageDefinition Stage { get; init; }

    /// <summary>The pipeline, for pipeline runs.</summary>
    public PipelineDefinition? Pipeline { get; init; }

    /// <summary>Validated inputs of the run.</summary>
    public required JsonObject RunInputs { get; init; }

    /// <summary>Input files of the run (streamed from the database).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> RunInputFiles { get; init; }

    /// <summary>Steps of the run as stored.</summary>
    public required IReadOnlyList<StepResult> Steps { get; init; }

    /// <summary>W3C trace context of the run.</summary>
    public string? TraceParent { get; init; }

    /// <summary>The request for <see cref="IRunEngine.ExecuteStepAsync"/>.</summary>
    public StepExecutionRequest ToExecutionRequest() => new()
    {
        RunId = RunId,
        Kind = Kind,
        Stage = Stage,
        Pipeline = Pipeline,
        Order = Order,
        RunInputs = RunInputs,
        RunInputFiles = RunInputFiles,
        CompletedSteps = Steps,
        AttemptId = AttemptId,
        Attempt = Attempt,
        Cause = Cause,
        TriggeredBy = TriggeredBy,
    };
}

/// <summary>What happened when a step was completed.</summary>
public enum StepCompletion
{
    /// <summary>The next step was queued.</summary>
    NextStepQueued,

    /// <summary>The step failed and an automatic retry was scheduled.</summary>
    RetryScheduled,

    /// <summary>The run succeeded.</summary>
    RunSucceeded,

    /// <summary>The run failed.</summary>
    RunFailed,

    /// <summary>The run was cancelled.</summary>
    RunCancelled,

    /// <summary>The lease was taken by the reaper: nothing was recorded.</summary>
    LeaseLost,
}

/// <summary>Outcome of one reaper cycle.</summary>
/// <param name="Recovered">Attempts restarted (<see cref="AttemptCause.LeaseRecovery"/>).</param>
/// <param name="Failed">Steps failed with <see cref="ErrorKind.WorkerLost"/>.</param>
/// <param name="Cancelled">Cancelling runs finished.</param>
public sealed record LeaseReaping(int Recovered, int Failed, int Cancelled)
{
    /// <summary>Attempts handled.</summary>
    public int Total => Recovered + Failed + Cancelled;
}

/// <summary>Outcome of one scheduler cycle.</summary>
/// <param name="Fired">Runs created.</param>
/// <param name="Rejected">Firings rejected (<c>run.rejected</c>).</param>
public sealed record ScheduleFiring(int Fired, int Rejected)
{
    /// <summary>Schedules handled.</summary>
    public int Total => Fired + Rejected;
}
