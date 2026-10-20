using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>
/// Persists every run executed by the engine in this process (synchronous runs, draft tests and reprocessing): run,
/// steps, attempts, log tails and files. Rows are addressed by run id + creation time (the partition) + step order. With a
/// queue registered, every transition also writes its event to the outbox in the same transaction (D-105).
/// </summary>
internal sealed class EfRunRecorder(
    IDbContextFactory<PsrDbContext> factory,
    ArtifactStore artifacts,
    IIdempotencyStore idempotency,
    RunLoader loader,
    IRunMessageOutbox messages,
    PsrEntityFrameworkOptions options) : IRunRecorder
{
    private static readonly RunStatus[] Terminal = [RunStatus.Succeeded, RunStatus.Failed, RunStatus.Cancelled];

    public async ValueTask<RunResult?> RunStartingAsync(RunStarting run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                if (run.IdempotencyKey is { } key
                    && await idempotency.ReserveAsync(db, IdempotencyScopes.For(run.Kind, run.TargetKey), key, run.RunId, cancellationToken).ConfigureAwait(false)
                        is { } existingId)
                {
                    var existing = await RunLoader.LoadAsync(db, existingId, cancellationToken).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        return loader.ToResult(existing, deduplicated: true);
                    }
                }

                var (row, retention) = await RunRows.NewAsync(db, run.RunId, run.Kind, run.TargetKey, run.TargetVersion, run.Steps, cancellationToken)
                    .ConfigureAwait(false);
                row.Mode = ExecutionMode.Sync;
                row.Trigger = run.Trigger;
                row.TriggeredBy = run.TriggeredBy;
                row.Status = RunStatus.Running;
                row.Inputs = run.Inputs.ToJsonString();
                row.IdempotencyKey = run.IdempotencyKey;
                row.CorrelationId = run.CorrelationId;
                row.TraceParent = run.TraceParent;
                row.StartedAt = run.StartedAt;
                db.Runs.Add(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await RunRows.StoreFilesAsync(db, artifacts, run.RunId, null, ArtifactDirection.Input, run.Inputs, run.InputFiles,
                    retention ?? options.Retention.Artifacts, cancellationToken).ConfigureAwait(false);
                row.Inputs = ArtifactRefs.ToStored(run.Inputs, run.InputFiles).ToJsonString();
                messages.Publish(db, RunEventFactory.For(row, RunEventTypes.RunStarted, run.StartedAt), row.TraceParent);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
    }

    public async ValueTask RunResumingAsync(RunResuming run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var createdAt = run.RunId.CreatedAt;
        await WriteAsync(async db =>
        {
            await db.Runs.Where(r => r.Id == run.RunId.Value && r.CreatedAt == createdAt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatus.Running)
                    .SetProperty(r => r.Outputs, (string?)null)
                    .SetProperty(r => r.Error, (string?)null)
                    .SetProperty(r => r.FinishedAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
            await db.StepRuns.Where(s => s.RunId == run.RunId.Value && s.RunCreatedAt == createdAt && s.Status == StepStatus.Skipped)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.Pending), cancellationToken).ConfigureAwait(false);
            await PublishAsync(db, run.RunId, row => RunEventFactory.For(row, RunEventTypes.RunStarted, run.ResumedAt), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StepStartedAsync(StepStarted step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        var createdAt = step.RunId.CreatedAt;
        var inputs = ArtifactRefs.ToStored(step.Inputs, step.InputFiles).ToJsonString();
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.StepRuns.Where(s => s.RunId == step.RunId.Value && s.RunCreatedAt == createdAt && s.Order == step.Order)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, StepStatus.Running)
                    .SetProperty(x => x.Inputs, inputs)
                    .SetProperty(x => x.StartedAt, step.StartedAt)
                    .SetProperty(x => x.FinishedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.Error, (string?)null), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask AttemptStartedAsync(AttemptStarted attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var createdAt = attempt.RunId.CreatedAt;
        await WriteAsync(async db =>
        {
            var step = await db.StepRuns.AsNoTracking()
                .SingleAsync(s => s.RunId == attempt.RunId.Value && s.RunCreatedAt == createdAt && s.Order == attempt.Order, cancellationToken)
                .ConfigureAwait(false);
            db.StepAttempts.Add(new StepAttemptEntity
            {
                Id = attempt.AttemptId.Value,
                StepRunId = step.Id,
                RunId = attempt.RunId.Value,
                RunCreatedAt = createdAt,
                Attempt = attempt.Number,
                Cause = attempt.Cause,
                TriggeredBy = attempt.TriggeredBy,
                Status = AttemptStatus.Running,
                StartedAt = attempt.StartedAt,
            });
            await PublishAsync(db, attempt.RunId, row => RunEventFactory.ForStep(row, step, attempt.Number, RunEventTypes.StepStarted, attempt.StartedAt),
                cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await db.StepRuns.Where(s => s.Id == step.Id && s.RunCreatedAt == createdAt)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentAttempt, attempt.Number), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AttemptFinishedAsync(AttemptFinished attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var createdAt = attempt.RunId.CreatedAt;
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await AttemptRows.FinishAsync(db, createdAt, attempt.Attempt, attempt.Attempt.StartedAt + attempt.Attempt.Duration, workerId: null, cancellationToken)
                .ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask StepFinishedAsync(StepFinished step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        var result = step.Step;
        var createdAt = step.RunId.CreatedAt;
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var row = await db.StepRuns.SingleAsync(s => s.RunId == step.RunId.Value && s.RunCreatedAt == createdAt && s.Order == result.Order, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status == StepStatus.Succeeded && result.Outputs is { } outputs)
                {
                    var retention = await RunRows.ArtifactRetentionAsync(db, step.RunId, cancellationToken).ConfigureAwait(false);
                    await RunRows.StoreFilesAsync(db, artifacts, step.RunId, new StepRunId(row.Id), ArtifactDirection.Output, outputs, result.Files,
                        retention ?? options.Retention.Artifacts, cancellationToken).ConfigureAwait(false);
                    row.Outputs = ArtifactRefs.ToStored(outputs, result.Files).ToJsonString();
                }

                row.Status = result.Status;
                row.Error = StoredJson.Error(result.Error);
                row.FinishedAt = result.Status == StepStatus.Skipped ? null : step.FinishedAt;
                row.CurrentAttempt = result.Attempts.Count == 0 ? row.CurrentAttempt : result.Attempts[^1].Number;
                var type = result.Status switch
                {
                    StepStatus.Succeeded => RunEventTypes.StepSucceeded,
                    StepStatus.Failed or StepStatus.TimedOut => RunEventTypes.StepFailed,
                    _ => null,
                };
                if (type is not null)
                {
                    await PublishAsync(db, step.RunId, run => RunEventFactory.ForStep(run, row, row.CurrentAttempt, type, step.FinishedAt) with
                    {
                        Error = RunEventError.From(result.Error),
                    }, cancellationToken).ConfigureAwait(false);
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask RunFinishedAsync(RunFinished run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var createdAt = run.RunId.CreatedAt;
        var outputs = run.Outputs is null ? null : ArtifactRefs.ToStored(run.Outputs, run.OutputFiles).ToJsonString();
        var error = StoredJson.Error(run.Error);
        await WriteAsync(async db =>
        {
            await db.Runs.Where(r => r.Id == run.RunId.Value && r.CreatedAt == createdAt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, run.Status)
                    .SetProperty(r => r.Outputs, outputs)
                    .SetProperty(r => r.Error, error)
                    .SetProperty(r => r.FinishedAt, run.FinishedAt), cancellationToken).ConfigureAwait(false);
            if (Terminal.Contains(run.Status))
            {
                // Steps interrupted by an unexpected error (or never reached) must not stay open.
                var interrupted = run.Status == RunStatus.Cancelled ? StepStatus.Cancelled : StepStatus.Failed;
                await db.StepRuns.Where(s => s.RunId == run.RunId.Value && s.RunCreatedAt == createdAt && (s.Status == StepStatus.Running || s.Status == StepStatus.Queued))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, interrupted).SetProperty(x => x.FinishedAt, run.FinishedAt), cancellationToken)
                    .ConfigureAwait(false);
                await db.StepRuns.Where(s => s.RunId == run.RunId.Value && s.RunCreatedAt == createdAt && s.Status == StepStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.Skipped), cancellationToken).ConfigureAwait(false);
                await db.StepAttempts.Where(a => a.RunId == run.RunId.Value && a.RunCreatedAt == createdAt && a.Status == AttemptStatus.Running)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, run.Status == RunStatus.Cancelled ? AttemptStatus.Cancelled : AttemptStatus.Failed)
                        .SetProperty(a => a.FinishedAt, run.FinishedAt), cancellationToken).ConfigureAwait(false);
            }

            if (messages.IsEnabled && await RunHeaderAsync(db, run.RunId, cancellationToken).ConfigureAwait(false) is { } row)
            {
                RunEvent? runEvent = run.Status switch
                {
                    RunStatus.Succeeded => RunEventFactory.Succeeded(row,
                        await db.Artifacts.AsNoTracking().Where(a => a.RunId == row.Id && a.RunCreatedAt == createdAt)
                            .ToDictionaryAsync(a => a.Id, cancellationToken).ConfigureAwait(false),
                        run.FinishedAt),
                    RunStatus.Failed => RunEventFactory.For(row, RunEventTypes.RunFailed, run.FinishedAt) with { Error = RunEventError.From(run.Error) },
                    RunStatus.Cancelled => RunEventFactory.For(row, RunEventTypes.RunCancelled, run.FinishedAt),
                    _ => null,
                };
                if (runEvent is not null)
                {
                    messages.Publish(db, runEvent, row.TraceParent);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the changes in one transaction when events are written (so a change and its event commit together).</summary>
    private async Task WriteAsync(Func<PsrDbContext, Task> change, CancellationToken cancellationToken)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            IDbContextTransaction? transaction = messages.IsEnabled
                ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : null;
            try
            {
                await change(db).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (transaction is not null)
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Adds an event of the run to the outbox (saved by the caller); nothing without a queue.</summary>
    private async Task PublishAsync(PsrDbContext db, RunId runId, Func<RunEntity, RunEvent> build, CancellationToken cancellationToken)
    {
        if (messages.IsEnabled && await RunHeaderAsync(db, runId, cancellationToken).ConfigureAwait(false) is { } row)
        {
            messages.Publish(db, build(row), row.TraceParent);
        }
    }

    private static Task<RunEntity?> RunHeaderAsync(PsrDbContext db, RunId runId, CancellationToken cancellationToken)
    {
        var createdAt = runId.CreatedAt;
        return db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId.Value && r.CreatedAt == createdAt, cancellationToken);
    }
}
