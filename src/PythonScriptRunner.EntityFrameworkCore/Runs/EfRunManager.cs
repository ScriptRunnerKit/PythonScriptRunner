using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Infrastructure;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>Queries, cancels and reprocesses persisted runs.</summary>
internal sealed class EfRunManager(
    IDbContextFactory<PsrDbContext> factory,
    RunLoader loader,
    IRunEngine engine,
    AsyncRunLifecycle lifecycle,
    IRunMessageOutbox messages,
    PsrClock clock,
    TimeProvider time) : IRunManager
{
    public async Task<RunSnapshot?> GetAsync(RunId runId, CancellationToken cancellationToken = default)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var loaded = await RunLoader.LoadAsync(db, runId, cancellationToken).ConfigureAwait(false);
            return loaded is null ? null : RunLoader.ToSnapshot(loaded);
        }
    }

    public async Task<KeysetPage<RunSummary>> ListAsync(RunQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (from, to) = TimeWindow.Resolve(query.From, query.To, time);
        var limit = Math.Clamp(query.Limit, 1, 100);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var runs = db.Runs.AsNoTracking().Where(r => r.CreatedAt >= from && r.CreatedAt < to);
            if (query.PipelineKey is { } pipelineKey)
            {
                var pipelineId = await db.Pipelines.Where(p => p.Key == pipelineKey).Select(p => (Guid?)p.Id)
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                runs = runs.Where(r => r.PipelineId == pipelineId);
            }

            if (query.StageKey is { } stageKey)
            {
                runs = runs.Where(r => r.StageKey == stageKey);
            }

            if (query.Kind is { } kind)
            {
                runs = runs.Where(r => r.Kind == kind);
            }

            if (query.Status is { } status)
            {
                runs = runs.Where(r => r.Status == status);
            }

            if (query.Trigger is { } trigger)
            {
                runs = runs.Where(r => r.Trigger == trigger);
            }

            if (KeysetCursor.Decode(query.Cursor) is { } cursor)
            {
                runs = runs.Where(r => r.CreatedAt < cursor.At || (r.CreatedAt == cursor.At && r.Id < cursor.Id));
            }

            var rows = await runs.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(limit + 1)
                .Select(r => new
                {
                    r.Id,
                    r.Kind,
                    r.Mode,
                    r.Status,
                    r.PipelineKey,
                    r.PipelineVersion,
                    r.StageKey,
                    r.StageVersion,
                    r.Trigger,
                    r.TriggeredBy,
                    r.CreatedAt,
                    r.StartedAt,
                    r.FinishedAt,
                    r.Error,
                })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var items = rows.Take(limit).Select(r => new RunSummary(
                new RunId(r.Id), r.Kind, r.Mode, r.Status, r.PipelineKey ?? r.StageKey ?? string.Empty, r.PipelineVersion ?? r.StageVersion ?? 0,
                r.Trigger, r.TriggeredBy, r.CreatedAt, r.StartedAt, r.FinishedAt, StoredJson.Error(r.Error))).ToList();
            var next = rows.Count > limit ? KeysetCursor.Encode(rows[limit - 1].CreatedAt, rows[limit - 1].Id) : null;
            return new KeysetPage<RunSummary>(items, next);
        }
    }

    public async Task<AttemptLog?> GetAttemptLogAsync(RunId runId, AttemptId attemptId, LogStream stream, CancellationToken cancellationToken = default)
    {
        var createdAt = runId.CreatedAt;
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.AttemptLogs.AsNoTracking()
                .Where(l => l.AttemptId == attemptId.Value && l.RunCreatedAt == createdAt && l.Stream == stream)
                .Select(l => new AttemptLog(l.Stream, l.Content, l.Truncated, l.OriginalBytes))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(RunId runId, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RunStatus? status;
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = await clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
                status = await lifecycle.CancelAsync(db, runId, actor, now, cancellationToken).ConfigureAwait(false) ?? throw new RunNotFoundException(runId);
                var createdAt = runId.CreatedAt;
                var target = await db.Runs.Where(r => r.Id == runId.Value && r.CreatedAt == createdAt).Select(r => r.PipelineKey ?? r.StageKey)
                    .SingleAsync(cancellationToken).ConfigureAwait(false);
                AuditLog.Add(db, new AuditEntry(actor, AuditActions.RunCancelled, "Run", runId.Value, target), now);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (status == RunStatus.Cancelling)
        {
            // Runs executing in this process stop now; the queue stops the others (CancelRun to every instance).
            engine.RequestCancellation(runId);
        }
    }

    public async Task<RunSnapshot> ReprocessAsync(RunId runId, Actor actor, ExecutionMode mode = ExecutionMode.Async, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (mode == ExecutionMode.Async)
        {
            return await QueueReprocessAsync(runId, actor, cancellationToken).ConfigureAwait(false);
        }

        var request = await PrepareResumeAsync(runId, actor, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await engine.ResumeAsync(request, cancellationToken).ConfigureAwait(false);
            await result.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            await RestoreFailedAsync(runId).ConfigureAwait(false);
            throw;
        }

        return await GetAsync(runId, CancellationToken.None).ConfigureAwait(false) ?? throw new RunNotFoundException(runId);
    }

    /// <summary>A reprocess that could not start leaves the run failed (it can be reprocessed again).</summary>
    private async Task RestoreFailedAsync(RunId runId)
    {
        var createdAt = runId.CreatedAt;
        var db = await factory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.Runs.Where(r => r.Id == runId.Value && r.CreatedAt == createdAt && r.Status == RunStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Failed)).ConfigureAwait(false);
        }
    }

    /// <summary>Queues the failed step for the workers (the run keeps its id, version and inputs).</summary>
    private async Task<RunSnapshot> QueueReprocessAsync(RunId runId, Actor actor, CancellationToken cancellationToken)
    {
        if (!messages.IsEnabled)
        {
            throw new PsrConfigurationException(
                "Asynchronous reprocessing needs a run queue (PythonScriptRunner.RabbitMQ). Use ExecutionMode.Sync to reprocess in this process.");
        }

        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var loaded = await RunLoader.LoadAsync(db, runId, cancellationToken).ConfigureAwait(false) ?? throw new RunNotFoundException(runId);
            var now = await clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
            EnsureReprocessable(loaded, runId, now);
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                if (!await lifecycle.QueueReprocessAsync(db, runId, actor, now, cancellationToken).ConfigureAwait(false))
                {
                    throw new PsrDomainException(PsrErrorCodes.RunNotFailed, $"Run {runId} is already being reprocessed.");
                }

                AuditLog.Add(db, new AuditEntry(actor, AuditActions.RunReprocessed, "Run", runId.Value, RunLoader.TargetKey(loaded.Run),
                    new JsonObject { ["mode"] = nameof(ExecutionMode.Async) }), now);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return await GetAsync(runId, CancellationToken.None).ConfigureAwait(false) ?? throw new RunNotFoundException(runId);
    }

    /// <summary>Only failed runs whose files are still retained can be reprocessed.</summary>
    private static void EnsureReprocessable(LoadedRun loaded, RunId runId, DateTimeOffset now)
    {
        var run = loaded.Run;
        if (run.Status != RunStatus.Failed)
        {
            throw new PsrDomainException(PsrErrorCodes.RunNotFailed, $"Only failed runs can be reprocessed; run {runId} is {run.Status}.");
        }

        var needed = ArtifactRefs.Referenced(StoredJson.Object(run.Inputs))
            .Concat(run.Steps.Where(s => s.Status == StepStatus.Succeeded).SelectMany(s => ArtifactRefs.Referenced(StoredJson.Object(s.Outputs))))
            .ToHashSet();
        var expired = needed.Where(id => !loaded.Artifacts.TryGetValue(id, out var a) || a.ContentDeletedAt is not null || a.ContentExpiresAt <= now).ToList();
        if (expired.Count > 0)
        {
            throw new PsrDomainException(PsrErrorCodes.ArtifactsExpired,
                $"Run {runId} cannot be reprocessed: {expired.Count} file(s) it needs are beyond the retention.");
        }
    }

    /// <summary>Checks the run can be reprocessed, claims it (Failed → Running) and loads the state to resume.</summary>
    private async Task<ResumeRequest> PrepareResumeAsync(RunId runId, Actor actor, CancellationToken cancellationToken)
    {
        var createdAt = runId.CreatedAt;
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var loaded = await RunLoader.LoadAsync(db, runId, cancellationToken).ConfigureAwait(false) ?? throw new RunNotFoundException(runId);
            var run = loaded.Run;
            var now = time.GetUtcNow();
            EnsureReprocessable(loaded, runId, now);
            var (stage, pipeline) = await RunDefinitions.LoadAsync(db, run, cancellationToken).ConfigureAwait(false);
            var claimed = await db.Runs.Where(r => r.Id == run.Id && r.CreatedAt == createdAt && r.Status == RunStatus.Failed)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Running), cancellationToken).ConfigureAwait(false);
            if (claimed == 0)
            {
                throw new PsrDomainException(PsrErrorCodes.RunNotFailed, $"Run {runId} is already being reprocessed.");
            }

            AuditLog.Add(db, new AuditEntry(actor, AuditActions.RunReprocessed, "Run", run.Id, RunLoader.TargetKey(run),
                new JsonObject { ["mode"] = nameof(ExecutionMode.Sync) }), now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var (inputs, inputFiles) = loader.Inputs(loaded);
            return new ResumeRequest
            {
                RunId = runId,
                Kind = run.Kind,
                Stage = stage,
                Pipeline = pipeline,
                Inputs = inputs,
                InputFiles = inputFiles,
                PreviousSteps = loader.ToStepResults(loaded),
                TriggeredBy = actor.Subject,
            };
        }
    }
}
