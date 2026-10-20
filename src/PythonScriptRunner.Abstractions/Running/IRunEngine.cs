using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>
/// Advanced API of the execution engine, used by the persistence, queue and dashboard packages: runs definitions that do
/// not come from the catalog (drafts), resumes failed runs and cancels runs of this process. Applications normally use
/// <see cref="IPythonRunner"/>.
/// </summary>
public interface IRunEngine
{
    /// <summary>Runs a stage definition (any status) as a single-step run and waits for its result.</summary>
    /// <param name="stage">The definition (e.g. a draft).</param>
    /// <param name="kind"><see cref="RunKind.Stage"/> or <see cref="RunKind.StageTest"/>.</param>
    /// <param name="inputs">Inputs (same forms as <see cref="IPythonRunner.RunStageAsync"/>).</param>
    /// <param name="options">Idempotency key and caller identity (<see cref="RunOptions.Version"/> is ignored).</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="InputValidationException">The inputs do not match the contract.</exception>
    Task<RunResult> RunStageAsync(
        StageDefinition stage, RunKind kind, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Runs a pipeline definition (any status) and waits for its result. Its stages are resolved in the catalog.</summary>
    /// <param name="pipeline">The definition (e.g. a draft).</param>
    /// <param name="kind"><see cref="RunKind.Pipeline"/> or <see cref="RunKind.PipelineTest"/>.</param>
    /// <param name="inputs">Pipeline inputs.</param>
    /// <param name="options">Idempotency key and caller identity (<see cref="RunOptions.Version"/> is ignored).</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="InputValidationException">The inputs do not match the contract.</exception>
    Task<RunResult> RunPipelineAsync(
        PipelineDefinition pipeline, RunKind kind, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a failed run in this process: successful steps keep their outputs, the first unsuccessful step gets a new
    /// attempt (<see cref="AttemptCause.Reprocess"/>) and the following steps run normally. The run keeps its identifier.
    /// </summary>
    /// <param name="request">State of the run to resume.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task<RunResult> ResumeAsync(ResumeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels a run that is executing in this process (including a step started by <see cref="ExecuteStepAsync"/>).</summary>
    /// <param name="runId">Run identifier.</param>
    /// <returns>Whether the run was executing here.</returns>
    bool RequestCancellation(RunId runId);

    /// <summary>Validates inputs against declared fields and converts them to the wire format, without running anything.</summary>
    /// <param name="fields">Declared inputs (of a stage or a pipeline).</param>
    /// <param name="inputs">Inputs (same forms as <see cref="IPythonRunner.RunStageAsync"/>).</param>
    /// <returns>Wire values (files carry metadata only) and the files by field name.</returns>
    /// <exception cref="InputValidationException">The inputs do not match the contract.</exception>
    BoundRunInputs BindInputs(IReadOnlyList<FieldDefinition> fields, object? inputs);

    /// <summary>
    /// Executes one attempt of one step of a run whose state is kept by the caller (the asynchronous worker): resolves the
    /// bindings, downloads the files it needs, runs the Python process and validates the outputs. Nothing is recorded and
    /// there is no automatic retry: the caller stores the result and decides what comes next. Dispose the result to delete
    /// the local files.
    /// </summary>
    /// <param name="request">The step, its run and the outputs of the steps already completed.</param>
    /// <param name="cancellationToken">Cancels the attempt (the process tree is stopped and the step ends <c>Cancelled</c>).</param>
    Task<StepExecution> ExecuteStepAsync(StepExecutionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes work folders left behind by interrupted runs (older than <c>OrphanRetention</c>).</summary>
    /// <returns>How many folders were removed.</returns>
    int RemoveOrphanWorkFolders();
}

/// <summary>Validated inputs.</summary>
/// <param name="Wire">Inputs in wire format; file values carry metadata only.</param>
/// <param name="Files">Files by field name (in list order for <c>list&lt;file&gt;</c>).</param>
public sealed record BoundRunInputs(JsonObject Wire, IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> Files);

/// <summary>One attempt of one step to execute with <see cref="IRunEngine.ExecuteStepAsync"/>.</summary>
public sealed record StepExecutionRequest
{
    /// <summary>Run identifier.</summary>
    public required RunId RunId { get; init; }

    /// <summary>What the run executes.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>Exact stage version of the step (any status).</summary>
    public required StageDefinition Stage { get; init; }

    /// <summary>The pipeline, for pipeline runs (its bindings give the step inputs); <c>null</c> for single-stage runs.</summary>
    public PipelineDefinition? Pipeline { get; init; }

    /// <summary>1-based position of the step.</summary>
    public required int Order { get; init; }

    /// <summary>Validated inputs of the run in wire format.</summary>
    public required JsonObject RunInputs { get; init; }

    /// <summary>Input files of the run by field name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> RunInputFiles { get; init; } = new Dictionary<string, IReadOnlyList<PsrFile>>();

    /// <summary>Earlier steps; the succeeded ones provide outputs and files to the bindings.</summary>
    public IReadOnlyList<StepResult> CompletedSteps { get; init; } = [];

    /// <summary>Identifier of the attempt (already recorded by the caller).</summary>
    public required AttemptId AttemptId { get; init; }

    /// <summary>1-based attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>Why the attempt starts.</summary>
    public required AttemptCause Cause { get; init; }

    /// <summary>Who started the attempt.</summary>
    public required string TriggeredBy { get; init; }
}

/// <summary>Result of <see cref="IRunEngine.ExecuteStepAsync"/>. Dispose it to delete the local files.</summary>
/// <param name="step">
/// The step: <see cref="StepResult.Attempts"/> holds this attempt only (empty when the bindings could not be resolved).
/// </param>
/// <param name="inputs">Resolved step inputs with file metadata; <c>null</c> when the bindings failed.</param>
/// <param name="inputFiles">Input files of the step by field name.</param>
/// <param name="runOutputs">Declared outputs of the run, when this was its last step and it succeeded.</param>
/// <param name="runOutputFiles">Declared file outputs of the run, under the same condition.</param>
/// <param name="cleanup">Deletes the local files.</param>
public sealed class StepExecution(
    StepResult step,
    JsonObject? inputs,
    IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> inputFiles,
    JsonObject? runOutputs,
    IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>? runOutputFiles,
    Func<ValueTask>? cleanup = null) : IAsyncDisposable
{
    private int _disposed;

    /// <summary>The step and its attempt.</summary>
    public StepResult Step { get; } = step;

    /// <summary>The attempt, or <c>null</c> when no process ran because the bindings could not be resolved.</summary>
    public AttemptResult? Attempt => Step.Attempts.Count > 0 ? Step.Attempts[^1] : null;

    /// <summary>Resolved step inputs with file metadata.</summary>
    public JsonObject? Inputs { get; } = inputs;

    /// <summary>Input files of the step by field name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> InputFiles { get; } = inputFiles;

    /// <summary>Declared outputs of the run, when this was its last step and it succeeded.</summary>
    public JsonObject? RunOutputs { get; } = runOutputs;

    /// <summary>Declared file outputs of the run, when this was its last step and it succeeded.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>? RunOutputFiles { get; } = runOutputFiles;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && cleanup is not null)
        {
            await cleanup().ConfigureAwait(false);
        }
    }
}

/// <summary>State of a failed run to resume.</summary>
public sealed record ResumeRequest
{
    /// <summary>Run identifier (kept).</summary>
    public required RunId RunId { get; init; }

    /// <summary>What the run executes.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>The stage, for single-stage runs.</summary>
    public StageDefinition? Stage { get; init; }

    /// <summary>The pipeline, for pipeline runs.</summary>
    public PipelineDefinition? Pipeline { get; init; }

    /// <summary>Validated inputs of the run in wire format.</summary>
    public required JsonObject Inputs { get; init; }

    /// <summary>Input files of the run by field name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> InputFiles { get; init; } = new Dictionary<string, IReadOnlyList<PsrFile>>();

    /// <summary>Steps as recorded: succeeded steps (with outputs and files) are reused; attempts of the others are counted.</summary>
    public required IReadOnlyList<StepResult> PreviousSteps { get; init; }

    /// <summary>Who reprocesses.</summary>
    public required string TriggeredBy { get; init; }
}
