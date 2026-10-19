using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Queries, cancels and reprocesses persisted runs.</summary>
public interface IRunManager
{
    /// <summary>Reads a run with its steps, attempts and files (logs are read with <see cref="GetAttemptLogAsync"/>).</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run, or <c>null</c> when it does not exist or is beyond the retention.</returns>
    Task<RunSnapshot?> GetAsync(RunId runId, CancellationToken cancellationToken = default);

    /// <summary>Lists runs newest first inside a time window (default: last 24 hours; at most 90 days).</summary>
    /// <param name="query">Filters, window and cursor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<KeysetPage<RunSummary>> ListAsync(RunQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reads the stdout or stderr tail of an attempt.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="attemptId">Attempt identifier.</param>
    /// <param name="stream">Which stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AttemptLog?> GetAttemptLogAsync(RunId runId, AttemptId attemptId, LogStream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a run: pending/queued runs end <c>Cancelled</c>; running runs move to <c>Cancelling</c> and their processes are stopped.
    /// <c>RUN_NOT_CANCELLABLE</c> when the run already finished.
    /// </summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="actor">Who cancels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CancelAsync(RunId runId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reprocesses a failed run with the same version and inputs: successful steps keep their outputs and the failed step gets a
    /// new attempt (<see cref="AttemptCause.Reprocess"/>). Errors: <c>RUN_NOT_FAILED</c>, <c>ARTIFACTS_EXPIRED</c>.
    /// </summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="actor">Who reprocesses.</param>
    /// <param name="mode"><see cref="ExecutionMode.Sync"/> runs it now in this process; <see cref="ExecutionMode.Async"/> needs a run queue.</param>
    /// <param name="cancellationToken">Cancellation token (cancels a synchronous reprocess).</param>
    /// <returns>The run after reprocessing (sync) or after it was queued (async).</returns>
    Task<RunSnapshot> ReprocessAsync(RunId runId, Actor actor, ExecutionMode mode = ExecutionMode.Async, CancellationToken cancellationToken = default);
}

/// <summary>Filters of a run list. The window is required to keep queries on recent partitions.</summary>
public sealed record RunQuery
{
    /// <summary>Start of the window (inclusive); default: <see cref="To"/> minus 24 hours.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>End of the window (exclusive); default: now.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Only runs of this pipeline.</summary>
    public string? PipelineKey { get; init; }

    /// <summary>Only single-stage runs of this stage.</summary>
    public string? StageKey { get; init; }

    /// <summary>Only runs of this kind.</summary>
    public RunKind? Kind { get; init; }

    /// <summary>Only runs with this status.</summary>
    public RunStatus? Status { get; init; }

    /// <summary>Only runs started this way.</summary>
    public RunTrigger? Trigger { get; init; }

    /// <summary>Cursor returned by the previous page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Items per page (1-100). Default: 50.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>A run in a list.</summary>
/// <param name="Id">Run identifier.</param>
/// <param name="Kind">What was executed.</param>
/// <param name="Mode">Sync or async.</param>
/// <param name="Status">Status.</param>
/// <param name="TargetKey">Pipeline or stage key.</param>
/// <param name="TargetVersion">Pipeline or stage version.</param>
/// <param name="Trigger">How the run started.</param>
/// <param name="TriggeredBy">Who started it.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="StartedAt">Start time.</param>
/// <param name="FinishedAt">End time.</param>
/// <param name="Error">Failure.</param>
public sealed record RunSummary(
    RunId Id,
    RunKind Kind,
    ExecutionMode Mode,
    RunStatus Status,
    string TargetKey,
    int TargetVersion,
    RunTrigger Trigger,
    string TriggeredBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    RunError? Error);

/// <summary>A persisted run with its steps, attempts and files.</summary>
public sealed record RunSnapshot
{
    /// <summary>Run identifier.</summary>
    public required RunId Id { get; init; }

    /// <summary>What was executed.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>Sync or async.</summary>
    public required ExecutionMode Mode { get; init; }

    /// <summary>Status.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>Pipeline or stage key.</summary>
    public required string TargetKey { get; init; }

    /// <summary>Pipeline or stage version.</summary>
    public required int TargetVersion { get; init; }

    /// <summary>How the run started.</summary>
    public required RunTrigger Trigger { get; init; }

    /// <summary>Who started it.</summary>
    public required string TriggeredBy { get; init; }

    /// <summary>Validated inputs; files as <c>{"$artifact": "&lt;id&gt;"}</c>.</summary>
    public required JsonObject Inputs { get; init; }

    /// <summary>Declared outputs; files as <c>{"$artifact": "&lt;id&gt;"}</c>.</summary>
    public JsonObject? Outputs { get; init; }

    /// <summary>Failure.</summary>
    public RunError? Error { get; init; }

    /// <summary>Idempotency key given by the caller.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset? QueuedAt { get; init; }

    /// <summary>Start time.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>End time.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>When cancellation was requested.</summary>
    public DateTimeOffset? CancelRequestedAt { get; init; }

    /// <summary>Who requested cancellation.</summary>
    public string? CancelRequestedBy { get; init; }

    /// <summary>When the run is deleted (pipelines with a shorter retention).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Steps in order.</summary>
    public required IReadOnlyList<StepRunSnapshot> Steps { get; init; }

    /// <summary>Input and output files.</summary>
    public required IReadOnlyList<ArtifactInfo> Artifacts { get; init; }
}

/// <summary>A persisted step of a run.</summary>
public sealed record StepRunSnapshot
{
    /// <summary>Step identifier.</summary>
    public required StepRunId Id { get; init; }

    /// <summary>Step key (the stage key for single-stage runs).</summary>
    public required string StepKey { get; init; }

    /// <summary>1-based position.</summary>
    public required int Order { get; init; }

    /// <summary>Executed stage.</summary>
    public required string StageKey { get; init; }

    /// <summary>Exact stage version.</summary>
    public required int StageVersion { get; init; }

    /// <summary>Status.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>Number of the last attempt (0 = not executed).</summary>
    public required int CurrentAttempt { get; init; }

    /// <summary>Inputs after resolving the bindings.</summary>
    public JsonObject? Inputs { get; init; }

    /// <summary>Validated outputs.</summary>
    public JsonObject? Outputs { get; init; }

    /// <summary>Failure of the last attempt.</summary>
    public RunError? Error { get; init; }

    /// <summary>Start time.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>End time.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Attempts in order.</summary>
    public required IReadOnlyList<AttemptSnapshot> Attempts { get; init; }
}

/// <summary>A persisted attempt (one Python process).</summary>
public sealed record AttemptSnapshot
{
    /// <summary>Attempt identifier.</summary>
    public required AttemptId Id { get; init; }

    /// <summary>1-based number within the step.</summary>
    public required int Number { get; init; }

    /// <summary>Why it started.</summary>
    public required AttemptCause Cause { get; init; }

    /// <summary>Who started it (user for reprocessing, <c>system:*</c> otherwise).</summary>
    public required string TriggeredBy { get; init; }

    /// <summary>Status.</summary>
    public required AttemptStatus Status { get; init; }

    /// <summary>Worker that executed it (asynchronous runs).</summary>
    public string? WorkerId { get; init; }

    /// <summary>Python environment used.</summary>
    public string? EnvironmentHash { get; init; }

    /// <summary>Exit code of the process.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Failure.</summary>
    public RunError? Error { get; init; }

    /// <summary>Start time.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>End time.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Total duration.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Time spent by the runner outside the Python process.</summary>
    public TimeSpan? Overhead { get; init; }

    /// <summary>Logs captured (content is read with <see cref="IRunManager.GetAttemptLogAsync"/>).</summary>
    public IReadOnlyList<LogStream> Logs { get; init; } = [];
}

/// <summary>The stdout or stderr tail of an attempt.</summary>
/// <param name="Stream">Which stream.</param>
/// <param name="Content">Captured text (secret values masked).</param>
/// <param name="Truncated">Whether older output was discarded.</param>
/// <param name="OriginalBytes">Total bytes written by the process.</param>
public sealed record AttemptLog(LogStream Stream, string Content, bool Truncated, long OriginalBytes);

/// <summary>Metadata of a stored file.</summary>
public sealed record ArtifactInfo
{
    /// <summary>File identifier.</summary>
    public required ArtifactId Id { get; init; }

    /// <summary>Run that owns the file.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Step that produced the file (<c>null</c> for run inputs).</summary>
    public string? StepKey { get; init; }

    /// <summary>Input or output.</summary>
    public required ArtifactDirection Direction { get; init; }

    /// <summary>Field that carries the file.</summary>
    public required string FieldName { get; init; }

    /// <summary>Position in a <c>list&lt;file&gt;</c>.</summary>
    public int? ListIndex { get; init; }

    /// <summary>File name.</summary>
    public required string FileName { get; init; }

    /// <summary>MIME type.</summary>
    public required string ContentType { get; init; }

    /// <summary>Size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>SHA-256 as lowercase hex.</summary>
    public required string Sha256 { get; init; }

    /// <summary>When the content is deleted.</summary>
    public required DateTimeOffset ContentExpiresAt { get; init; }

    /// <summary>When the content was deleted (downloads then fail with <c>ARTIFACT_EXPIRED</c>).</summary>
    public DateTimeOffset? ContentDeletedAt { get; init; }
}

/// <summary>Stores and streams run files.</summary>
public interface IArtifactStore
{
    /// <summary>Stores a file (streamed; never fully loaded in memory).</summary>
    /// <param name="artifact">Metadata.</param>
    /// <param name="content">Readable content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ArtifactInfo> SaveAsync(NewArtifact artifact, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Reads the metadata of a file.</summary>
    /// <param name="artifactId">File identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ArtifactInfo?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken = default);

    /// <summary>Opens the content for streaming. <c>ARTIFACT_EXPIRED</c> when the content was deleted by the retention.</summary>
    /// <param name="artifactId">File identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArtifactNotFoundException">The file does not exist.</exception>
    Task<Stream> OpenReadAsync(ArtifactId artifactId, CancellationToken cancellationToken = default);
}

/// <summary>Metadata of a file to store.</summary>
public sealed record NewArtifact
{
    /// <summary>Identifier to use (new when omitted).</summary>
    public ArtifactId? Id { get; init; }

    /// <summary>Run that owns the file.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Step that produced the file (<c>null</c> for run inputs).</summary>
    public StepRunId? StepRunId { get; init; }

    /// <summary>Input or output.</summary>
    public required ArtifactDirection Direction { get; init; }

    /// <summary>Field that carries the file.</summary>
    public required string FieldName { get; init; }

    /// <summary>Position in a <c>list&lt;file&gt;</c>.</summary>
    public int? ListIndex { get; init; }

    /// <summary>File name.</summary>
    public required string FileName { get; init; }

    /// <summary>MIME type.</summary>
    public required string ContentType { get; init; }

    /// <summary>How long the content is kept; <c>null</c> uses the global retention.</summary>
    public TimeSpan? Retention { get; init; }
}
