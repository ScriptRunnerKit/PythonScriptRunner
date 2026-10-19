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

    /// <summary>Cancels a run that is executing in this process.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <returns>Whether the run was executing here.</returns>
    bool RequestCancellation(RunId runId);
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
