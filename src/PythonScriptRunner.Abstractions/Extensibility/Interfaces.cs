using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Source of stage and pipeline definitions (memory, folder or database).</summary>
public interface ICatalog
{
    /// <summary>Finds a stage version.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="version">Exact version, or <c>null</c> for the latest published version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The definition, or <c>null</c> when not found.</returns>
    /// <exception cref="VersionNotRunnableException">The version exists but is a draft or archived.</exception>
    ValueTask<StageDefinition?> FindStageAsync(string key, int? version, CancellationToken cancellationToken = default);

    /// <summary>Finds a pipeline version.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="version">Exact version, or <c>null</c> for the latest published version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The definition, or <c>null</c> when not found.</returns>
    /// <exception cref="VersionNotRunnableException">The version exists but is a draft or archived.</exception>
    ValueTask<PipelineDefinition?> FindPipelineAsync(string key, int? version, CancellationToken cancellationToken = default);
}

/// <summary>Validates stage definitions (contract, limits and secret names).</summary>
public interface IStageValidator
{
    /// <summary>Validates a stage definition.</summary>
    /// <param name="stage">The definition.</param>
    /// <returns>Errors (code <c>STAGE_DEFINITION</c>) and warnings.</returns>
    ValidationReport Validate(StageDefinition stage);
}

/// <summary>Resolves the secrets declared by stages.</summary>
public interface ISecretProvider
{
    /// <summary>Returns the value of a secret, or <c>null</c> when it is not configured.</summary>
    /// <param name="name">Secret name (e.g. <c>SMTP_PASSWORD</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// Receives every transition of a run so it can be persisted. The default implementation does nothing; the
/// EntityFrameworkCore package stores runs, steps, attempts, logs and files.
/// </summary>
public interface IRunRecorder
{
    /// <summary>A run is about to start (inputs already validated). Reserves the idempotency key.</summary>
    /// <param name="run">Run data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The existing run when the idempotency key was already used (the new run must not start); otherwise <c>null</c>.</returns>
    ValueTask<RunResult?> RunStartingAsync(RunStarting run, CancellationToken cancellationToken);

    /// <summary>A failed run is being reprocessed: it is running again and skipped steps are pending.</summary>
    /// <param name="run">Run data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RunResumingAsync(RunResuming run, CancellationToken cancellationToken);

    /// <summary>A step started with its resolved inputs.</summary>
    /// <param name="step">Step data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask StepStartedAsync(StepStarted step, CancellationToken cancellationToken);

    /// <summary>An attempt (one Python process) started.</summary>
    /// <param name="attempt">Attempt data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask AttemptStartedAsync(AttemptStarted attempt, CancellationToken cancellationToken);

    /// <summary>An attempt finished (with its logs).</summary>
    /// <param name="attempt">Attempt data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask AttemptFinishedAsync(AttemptFinished attempt, CancellationToken cancellationToken);

    /// <summary>A step finished (succeeded with outputs and files, failed, cancelled or skipped).</summary>
    /// <param name="step">Step data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask StepFinishedAsync(StepFinished step, CancellationToken cancellationToken);

    /// <summary>A run finished.</summary>
    /// <param name="run">Final data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RunFinishedAsync(RunFinished run, CancellationToken cancellationToken);
}

/// <summary>A run is about to start.</summary>
public sealed record RunStarting
{
    /// <summary>Run identifier (its timestamp is the creation time).</summary>
    public required RunId RunId { get; init; }

    /// <summary>What is executed.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>Stage or pipeline key.</summary>
    public required string TargetKey { get; init; }

    /// <summary>Stage or pipeline version.</summary>
    public required int TargetVersion { get; init; }

    /// <summary>How the run started.</summary>
    public required RunTrigger Trigger { get; init; }

    /// <summary>Who requested the run.</summary>
    public required string TriggeredBy { get; init; }

    /// <summary>Deduplication key given by the caller.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Correlation given by the caller.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>W3C trace context of the caller (<c>traceparent</c>).</summary>
    public string? TraceParent { get; init; }

    /// <summary>Validated inputs in wire format (files carry metadata only).</summary>
    public required JsonObject Inputs { get; init; }

    /// <summary>Input files by field name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> InputFiles { get; init; } = new Dictionary<string, IReadOnlyList<PsrFile>>();

    /// <summary>Steps that will run, in order.</summary>
    public required IReadOnlyList<PlannedStep> Steps { get; init; }

    /// <summary>Start time.</summary>
    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>A step of a run that is about to start.</summary>
/// <param name="StepKey">Step key (the stage key for single-stage runs).</param>
/// <param name="Order">1-based position.</param>
/// <param name="StageKey">Stage key.</param>
/// <param name="StageVersion">Exact stage version.</param>
public sealed record PlannedStep(string StepKey, int Order, string StageKey, int StageVersion);

/// <summary>A failed run is being reprocessed.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="TriggeredBy">Who reprocesses.</param>
/// <param name="ResumedAt">When.</param>
public sealed record RunResuming(RunId RunId, string TriggeredBy, DateTimeOffset ResumedAt);

/// <summary>A step started.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="StepKey">Step key.</param>
/// <param name="Order">1-based position.</param>
/// <param name="Inputs">Resolved inputs in wire format.</param>
/// <param name="InputFiles">Input files by field name.</param>
/// <param name="StartedAt">Start time.</param>
public sealed record StepStarted(
    RunId RunId, string StepKey, int Order, JsonObject Inputs, IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> InputFiles, DateTimeOffset StartedAt);

/// <summary>An attempt started.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="StepKey">Step key.</param>
/// <param name="Order">1-based position of the step.</param>
/// <param name="AttemptId">Attempt identifier.</param>
/// <param name="Number">1-based attempt number.</param>
/// <param name="Cause">Why it started.</param>
/// <param name="TriggeredBy">Who started it.</param>
/// <param name="StartedAt">Start time.</param>
public sealed record AttemptStarted(
    RunId RunId, string StepKey, int Order, AttemptId AttemptId, int Number, AttemptCause Cause, string TriggeredBy, DateTimeOffset StartedAt);

/// <summary>An attempt finished.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="StepKey">Step key.</param>
/// <param name="Order">1-based position of the step.</param>
/// <param name="Attempt">Attempt outcome.</param>
public sealed record AttemptFinished(RunId RunId, string StepKey, int Order, AttemptResult Attempt);

/// <summary>A step finished.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="Step">Step outcome (outputs and files when it succeeded).</param>
/// <param name="FinishedAt">End time.</param>
public sealed record StepFinished(RunId RunId, StepResult Step, DateTimeOffset FinishedAt);

/// <summary>A run finished.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="Status">Final status.</param>
/// <param name="FinishedAt">End time.</param>
/// <param name="Outputs">Declared outputs in wire format.</param>
/// <param name="OutputFiles">Declared file outputs by name.</param>
/// <param name="Error">Failure, when the run did not succeed.</param>
public sealed record RunFinished(
    RunId RunId,
    RunStatus Status,
    DateTimeOffset FinishedAt,
    JsonObject? Outputs,
    IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>? OutputFiles,
    RunError? Error);
