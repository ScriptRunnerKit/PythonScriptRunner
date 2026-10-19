using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Source of stage definitions (memory, folder or database).</summary>
public interface ICatalog
{
    /// <summary>Finds a stage version.</summary>
    /// <param name="key">Stage key.</param>
    /// <param name="version">Exact version, or <c>null</c> for the latest published version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The definition, or <c>null</c> when not found.</returns>
    ValueTask<StageDefinition?> FindStageAsync(string key, int? version, CancellationToken cancellationToken = default);

    /// <summary>Finds a pipeline version.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="version">Exact version, or <c>null</c> for the latest published version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The definition, or <c>null</c> when not found.</returns>
    ValueTask<PipelineDefinition?> FindPipelineAsync(string key, int? version, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the secrets declared by stages.</summary>
public interface ISecretProvider
{
    /// <summary>Returns the value of a secret, or <c>null</c> when it is not configured.</summary>
    /// <param name="name">Secret name (e.g. <c>SMTP_PASSWORD</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>Receives run transitions so they can be persisted. The default implementation does nothing.</summary>
public interface IRunRecorder
{
    /// <summary>A run started.</summary>
    /// <param name="run">Run data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RunStartedAsync(RunStarted run, CancellationToken cancellationToken);

    /// <summary>An attempt finished.</summary>
    /// <param name="attempt">Attempt data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask AttemptFinishedAsync(AttemptFinished attempt, CancellationToken cancellationToken);

    /// <summary>A run finished.</summary>
    /// <param name="run">Final data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RunFinishedAsync(RunFinished run, CancellationToken cancellationToken);
}

/// <summary>A run started.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="Kind">What is executed.</param>
/// <param name="TargetKey">Stage or pipeline key.</param>
/// <param name="TargetVersion">Stage or pipeline version.</param>
/// <param name="TriggeredBy">Who requested the run.</param>
/// <param name="StartedAt">Start time.</param>
/// <param name="Inputs">Validated inputs in wire format.</param>
public sealed record RunStarted(
    RunId RunId, RunKind Kind, string TargetKey, int TargetVersion, string TriggeredBy, DateTimeOffset StartedAt, JsonObject Inputs);

/// <summary>An attempt finished.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="StepKey">Step key.</param>
/// <param name="Attempt">Attempt outcome.</param>
public sealed record AttemptFinished(RunId RunId, string StepKey, AttemptResult Attempt);

/// <summary>A run finished.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="Status">Final status.</param>
/// <param name="FinishedAt">End time.</param>
/// <param name="Outputs">Declared outputs in wire format.</param>
/// <param name="Error">Failure, when the run did not succeed.</param>
public sealed record RunFinished(RunId RunId, RunStatus Status, DateTimeOffset FinishedAt, JsonObject? Outputs, RunError? Error);
