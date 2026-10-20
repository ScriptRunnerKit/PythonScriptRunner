using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Entities;
using PythonScriptRunner.EntityFrameworkCore.Infrastructure;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>Queries, cancels and reprocesses persisted runs.</summary>
internal sealed class EfRunManager(
    IDbContextFactory<PsrDbContext> factory,
    RunLoader loader,
    IRunEngine engine,
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
        var createdAt = runId.CreatedAt;
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var runs = db.Runs.Where(r => r.Id == runId.Value && r.CreatedAt == createdAt);
            var steps = db.StepRuns.Where(s => s.RunId == runId.Value && s.RunCreatedAt == createdAt);
            var now = time.GetUtcNow();
            var error = StoredJson.Error(new RunError(ErrorKind.Cancelled, $"Cancelled by {actor.Subject}."));

            // Transitions are conditional on the current status, so a run that finishes meanwhile is never reopened.
            var waiting = await runs.Where(r => r.Status == RunStatus.Pending || r.Status == RunStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatus.Cancelled)
                    .SetProperty(r => r.FinishedAt, now)
                    .SetProperty(r => r.Error, error)
                    .SetProperty(r => r.CancelRequestedAt, now)
                    .SetProperty(r => r.CancelRequestedBy, actor.Subject), cancellationToken).ConfigureAwait(false);
            if (waiting > 0)
            {
                await steps.Where(s => s.Status == StepStatus.Queued)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.Cancelled), cancellationToken).ConfigureAwait(false);
                await steps.Where(s => s.Status == StepStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.Skipped), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var running = await runs.Where(r => r.Status == RunStatus.Running || r.Status == RunStatus.Cancelling)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, RunStatus.Cancelling)
                        .SetProperty(r => r.CancelRequestedAt, r => r.CancelRequestedAt ?? now)
                        .SetProperty(r => r.CancelRequestedBy, r => r.CancelRequestedBy ?? actor.Subject), cancellationToken).ConfigureAwait(false);
                if (running == 0)
                {
                    var status = await runs.Select(r => (RunStatus?)r.Status).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new RunNotFoundException(runId);
                    throw new PsrDomainException(PsrErrorCodes.RunNotCancellable, $"Run {runId} already finished ({status}).");
                }

                // Runs executing in this process stop now; the queue package stops the others (control messages).
                engine.RequestCancellation(runId);
            }

            var target = await runs.Select(r => r.PipelineKey ?? r.StageKey).SingleAsync(cancellationToken).ConfigureAwait(false);
            AuditLog.Add(db, new AuditEntry(actor, AuditActions.RunCancelled, "Run", runId.Value, target), now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RunSnapshot> ReprocessAsync(RunId runId, Actor actor, ExecutionMode mode = ExecutionMode.Async, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (mode == ExecutionMode.Async)
        {
            throw new PsrConfigurationException(
                "Asynchronous reprocessing needs a run queue (PythonScriptRunner.RabbitMQ). Use ExecutionMode.Sync to reprocess in this process.");
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

    /// <summary>Checks the run can be reprocessed, claims it (Failed → Running) and loads the state to resume.</summary>
    private async Task<ResumeRequest> PrepareResumeAsync(RunId runId, Actor actor, CancellationToken cancellationToken)
    {
        var createdAt = runId.CreatedAt;
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var loaded = await RunLoader.LoadAsync(db, runId, cancellationToken).ConfigureAwait(false) ?? throw new RunNotFoundException(runId);
            var run = loaded.Run;
            if (run.Status != RunStatus.Failed)
            {
                throw new PsrDomainException(PsrErrorCodes.RunNotFailed, $"Only failed runs can be reprocessed; run {runId} is {run.Status}.");
            }

            var now = time.GetUtcNow();
            var needed = ArtifactRefs.Referenced(StoredJson.Object(run.Inputs))
                .Concat(run.Steps.Where(s => s.Status == StepStatus.Succeeded).SelectMany(s => ArtifactRefs.Referenced(StoredJson.Object(s.Outputs))))
                .ToHashSet();
            var expired = needed.Where(id => !loaded.Artifacts.TryGetValue(id, out var a) || a.ContentDeletedAt is not null || a.ContentExpiresAt <= now).ToList();
            if (expired.Count > 0)
            {
                throw new PsrDomainException(PsrErrorCodes.ArtifactsExpired,
                    $"Run {runId} cannot be reprocessed: {expired.Count} file(s) it needs are beyond the retention.");
            }

            var (stage, pipeline) = await LoadDefinitionAsync(db, run, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The exact version the run executed (any status: archived versions can still be reprocessed).</summary>
    private static async Task<(StageDefinition? Stage, PipelineDefinition? Pipeline)> LoadDefinitionAsync(
        PsrDbContext db, RunEntity run, CancellationToken cancellationToken)
    {
        if (run.Kind is RunKind.Pipeline or RunKind.PipelineTest)
        {
            var version = await db.PipelineVersions.AsNoTracking().Include(v => v.Pipeline)
                .SingleOrDefaultAsync(v => v.Id == run.PipelineVersionId, cancellationToken).ConfigureAwait(false)
                ?? throw new VersionNotFoundException(run.PipelineKey ?? string.Empty, run.PipelineVersion ?? 0);
            var steps = await EfCatalog.LoadStepsAsync(db, version.Id, cancellationToken).ConfigureAwait(false);
            return (null, CatalogMapping.ToPipeline(version.Pipeline, version, steps));
        }

        var stageVersion = await db.StageVersions.AsNoTracking().Include(v => v.Stage)
            .SingleOrDefaultAsync(v => v.Id == run.StageVersionId, cancellationToken).ConfigureAwait(false)
            ?? throw new VersionNotFoundException(run.StageKey ?? string.Empty, run.StageVersion ?? 0);
        return (CatalogMapping.ToStage(stageVersion.Stage, stageVersion), null);
    }
}
