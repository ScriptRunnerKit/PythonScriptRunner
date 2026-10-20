namespace PythonScriptRunner;

/// <summary>
/// Queues runs for the workers (requires the persistence and RabbitMQ packages). The run, its first step and the message
/// are stored in one transaction, so a run is queued even while the broker is down.
/// </summary>
public interface IRunQueue
{
    /// <summary>Queues a published pipeline.</summary>
    /// <param name="pipelineKey">Pipeline key.</param>
    /// <param name="inputs">Pipeline inputs (same forms as <see cref="IPythonRunner.RunPipelineAsync"/>; files are stored in the database).</param>
    /// <param name="options">Version, idempotency key, correlation and caller identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run: <see cref="RunStatus.Queued"/>, or <see cref="RunStatus.Pending"/> when the pipeline is at its concurrency limit.</returns>
    /// <exception cref="PipelineNotFoundException">The pipeline does not exist.</exception>
    /// <exception cref="VersionNotFoundException">The requested version does not exist.</exception>
    /// <exception cref="InputValidationException">The inputs do not match the contract (no run is created).</exception>
    Task<RunHandle> EnqueuePipelineAsync(string pipelineKey, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Queues a published stage as a single-step run.</summary>
    /// <param name="stageKey">Stage key.</param>
    /// <param name="inputs">Stage inputs.</param>
    /// <param name="options">Version, idempotency key, correlation and caller identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The queued run.</returns>
    /// <exception cref="StageNotFoundException">The stage does not exist.</exception>
    /// <exception cref="VersionNotFoundException">The requested version does not exist.</exception>
    /// <exception cref="InputValidationException">The inputs do not match the contract (no run is created).</exception>
    Task<RunHandle> EnqueueStageAsync(string stageKey, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>A queued run.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="Status"><see cref="RunStatus.Queued"/> or <see cref="RunStatus.Pending"/> (the current status when deduplicated).</param>
/// <param name="Deduplicated">Whether an earlier run with the same idempotency key was returned.</param>
public sealed record RunHandle(RunId RunId, RunStatus Status, bool Deduplicated);
