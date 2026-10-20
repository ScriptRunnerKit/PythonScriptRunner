using System.Diagnostics;
using PythonScriptRunner.EntityFrameworkCore;

namespace PythonScriptRunner.RabbitMQ;

/// <summary>Queues runs: validates them like a synchronous run, then stores run + first step + message in one transaction.</summary>
internal sealed class RabbitMqRunQueue(ICatalog catalog, IRunEngine engine, AsyncRunLifecycle lifecycle) : IRunQueue
{
    public async Task<RunHandle> EnqueuePipelineAsync(string pipelineKey, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineKey);
        var pipeline = await catalog.FindPipelineAsync(pipelineKey, options?.Version, cancellationToken).ConfigureAwait(false)
            ?? throw (options?.Version is { } version && await catalog.FindPipelineAsync(pipelineKey, null, cancellationToken).ConfigureAwait(false) is not null
                ? new VersionNotFoundException(pipelineKey, version)
                : new PipelineNotFoundException(pipelineKey));
        var bound = engine.BindInputs(pipeline.Inputs, inputs);
        return await EnqueueAsync(new NewAsyncRun
        {
            Kind = RunKind.Pipeline,
            Pipeline = pipeline,
            Inputs = bound,
            TriggeredBy = string.Empty,
        }, pipeline.Key, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunHandle> EnqueueStageAsync(string stageKey, object? inputs, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);
        var stage = await catalog.FindStageAsync(stageKey, options?.Version, cancellationToken).ConfigureAwait(false)
            ?? throw (options?.Version is { } version && await catalog.FindStageAsync(stageKey, null, cancellationToken).ConfigureAwait(false) is not null
                ? new VersionNotFoundException(stageKey, version)
                : new StageNotFoundException(stageKey));
        var bound = engine.BindInputs(stage.Inputs, inputs);
        return await EnqueueAsync(new NewAsyncRun
        {
            Kind = RunKind.Stage,
            Stage = stage,
            Inputs = bound,
            TriggeredBy = string.Empty,
        }, stage.Key, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RunHandle> EnqueueAsync(NewAsyncRun run, string targetKey, RunOptions? options, CancellationToken cancellationToken)
    {
        // The worker activities of every step become children of this one (the trace context travels with the run).
        using var activity = RabbitMqDiagnostics.Source.StartActivity("psr.run.enqueue", ActivityKind.Producer);
        activity?.SetTag("psr.run.kind", run.Kind.ToString());
        activity?.SetTag("psr.target.key", targetKey);
        var handle = await lifecycle.CreateAsync(run with
        {
            Trigger = options?.Trigger ?? RunTrigger.Library,
            TriggeredBy = options?.TriggeredBy ?? Actor.Library.Subject,
            IdempotencyKey = options?.IdempotencyKey,
            CorrelationId = options?.CorrelationId,
            TraceParent = Activity.Current?.Id,
        }, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("psr.run.id", handle.RunId.ToString());
        return handle;
    }
}
