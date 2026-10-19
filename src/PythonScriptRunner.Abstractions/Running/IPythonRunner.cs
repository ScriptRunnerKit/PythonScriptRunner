namespace PythonScriptRunner;

/// <summary>Runs stages and pipelines synchronously in the current process (one Python process per attempt).</summary>
public interface IPythonRunner
{
    /// <summary>Runs a published stage and waits for its result.</summary>
    /// <param name="stageKey">Stage key.</param>
    /// <param name="inputs">Anonymous object, dictionary, <c>JsonObject</c> or record; files as <see cref="PsrFile"/>.</param>
    /// <param name="options">Version, idempotency key and caller identity.</param>
    /// <param name="cancellationToken">Cancels the run (the Python process tree is stopped).</param>
    /// <returns>The result; failures during execution are reported in <see cref="RunResult.Status"/>.</returns>
    /// <exception cref="StageNotFoundException">The stage does not exist.</exception>
    /// <exception cref="VersionNotFoundException">The requested version does not exist.</exception>
    /// <exception cref="InputValidationException">The inputs do not match the contract (no run is created).</exception>
    Task<RunResult> RunStageAsync(
        string stageKey, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Runs a published pipeline step by step and waits for its result.</summary>
    /// <param name="pipelineKey">Pipeline key.</param>
    /// <param name="inputs">Pipeline inputs (same forms as <see cref="RunStageAsync"/>).</param>
    /// <param name="options">Version, idempotency key and caller identity.</param>
    /// <param name="cancellationToken">Cancels the run (the running step is stopped; the next ones are skipped).</param>
    /// <returns>The result with the declared pipeline outputs; failures are reported in <see cref="RunResult.Status"/>.</returns>
    /// <exception cref="PipelineNotFoundException">The pipeline does not exist.</exception>
    /// <exception cref="VersionNotFoundException">The requested version does not exist.</exception>
    /// <exception cref="InputValidationException">The inputs do not match the pipeline contract (no run is created).</exception>
    Task<RunResult> RunPipelineAsync(
        string pipelineKey, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Options of one run.</summary>
public sealed record RunOptions
{
    /// <summary>Version to run; <c>null</c> runs the latest published version.</summary>
    public int? Version { get; init; }

    /// <summary>Deduplicates repeated requests for 24 hours (requires persistence).</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Who requested the run. Default: <c>system:library</c>.</summary>
    public string? TriggeredBy { get; init; }

    /// <summary>How the run was started. Default: <see cref="RunTrigger.Library"/>.</summary>
    public RunTrigger Trigger { get; init; } = RunTrigger.Library;
}
