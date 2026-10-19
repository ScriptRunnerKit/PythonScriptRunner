using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>
/// Persists every run executed by the engine in this process (synchronous runs, draft tests and reprocessing): run,
/// steps, attempts, log tails and files. Rows are addressed by run id + creation time (the partition) + step order.
/// </summary>
internal sealed class EfRunRecorder(
    IDbContextFactory<PsrDbContext> factory,
    ArtifactStore artifacts,
    IIdempotencyStore idempotency,
    RunLoader loader,
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

                var (row, retention) = await NewRunAsync(db, run, cancellationToken).ConfigureAwait(false);
                db.Runs.Add(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                await StoreFilesAsync(db, run.RunId, null, ArtifactDirection.Input, run.Inputs, run.InputFiles, retention, cancellationToken).ConfigureAwait(false);
                row.Inputs = ArtifactRefs.ToStored(run.Inputs, run.InputFiles).ToJsonString();
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
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.Runs.Where(r => r.Id == run.RunId.Value && r.CreatedAt == createdAt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatus.Running)
                    .SetProperty(r => r.Outputs, (string?)null)
                    .SetProperty(r => r.Error, (string?)null)
                    .SetProperty(r => r.FinishedAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
            await db.StepRuns.Where(s => s.RunId == run.RunId.Value && s.RunCreatedAt == createdAt && s.Status == StepStatus.Skipped)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.Pending), cancellationToken).ConfigureAwait(false);
        }
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
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var stepRunId = await db.StepRuns
                .Where(s => s.RunId == attempt.RunId.Value && s.RunCreatedAt == createdAt && s.Order == attempt.Order)
                .Select(s => s.Id).SingleAsync(cancellationToken).ConfigureAwait(false);
            db.StepAttempts.Add(new StepAttemptEntity
            {
                Id = attempt.AttemptId.Value,
                StepRunId = stepRunId,
                RunId = attempt.RunId.Value,
                RunCreatedAt = createdAt,
                Attempt = attempt.Number,
                Cause = attempt.Cause,
                TriggeredBy = attempt.TriggeredBy,
                Status = AttemptStatus.Running,
                StartedAt = attempt.StartedAt,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await db.StepRuns.Where(s => s.Id == stepRunId && s.RunCreatedAt == createdAt)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentAttempt, attempt.Number), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask AttemptFinishedAsync(AttemptFinished attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var createdAt = attempt.RunId.CreatedAt;
        var result = attempt.Attempt;
        var error = StoredJson.Error(result.Error);
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.StepAttempts.Where(a => a.Id == result.Id.Value && a.RunCreatedAt == createdAt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, result.Status)
                    .SetProperty(a => a.ExitCode, result.ExitCode)
                    .SetProperty(a => a.ErrorKind, result.Error == null ? null : result.Error.Kind)
                    .SetProperty(a => a.Error, error)
                    .SetProperty(a => a.EnvironmentHash, result.EnvironmentHash)
                    .SetProperty(a => a.FinishedAt, result.StartedAt + result.Duration)
                    .SetProperty(a => a.DurationMs, (int)result.Duration.TotalMilliseconds)
                    .SetProperty(a => a.OverheadMs, (int)result.Overhead.TotalMilliseconds), cancellationToken).ConfigureAwait(false);
            foreach (var (stream, tail) in (ReadOnlySpan<(LogStream, LogTail)>)[(LogStream.Stdout, result.Stdout), (LogStream.Stderr, result.Stderr)])
            {
                if (tail.OriginalBytes > 0 || tail.Content.Length > 0)
                {
                    db.AttemptLogs.Add(new AttemptLogEntity
                    {
                        AttemptId = result.Id.Value,
                        RunCreatedAt = createdAt,
                        Stream = stream,
                        Content = tail.Content,
                        Truncated = tail.Truncated,
                        OriginalBytes = tail.OriginalBytes,
                    });
                }
            }

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
                    var retention = await ArtifactRetentionAsync(db, step.RunId, cancellationToken).ConfigureAwait(false);
                    await StoreFilesAsync(db, step.RunId, new StepRunId(row.Id), ArtifactDirection.Output, outputs, result.Files, retention, cancellationToken)
                        .ConfigureAwait(false);
                    row.Outputs = ArtifactRefs.ToStored(outputs, result.Files).ToJsonString();
                }

                row.Status = result.Status;
                row.Error = StoredJson.Error(result.Error);
                row.FinishedAt = result.Status == StepStatus.Skipped ? null : step.FinishedAt;
                row.CurrentAttempt = result.Attempts.Count == 0 ? row.CurrentAttempt : result.Attempts[^1].Number;
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
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
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
        }
    }

    /// <summary>Builds the run row and its pending steps; resolves catalog ids and the retention of the pipeline.</summary>
    private async Task<(RunEntity Run, TimeSpan? ArtifactRetention)> NewRunAsync(PsrDbContext db, RunStarting run, CancellationToken cancellationToken)
    {
        var createdAt = run.RunId.CreatedAt;
        var row = new RunEntity
        {
            Id = run.RunId.Value,
            CreatedAt = createdAt,
            Kind = run.Kind,
            Mode = ExecutionMode.Sync,
            Trigger = run.Trigger,
            TriggeredBy = run.TriggeredBy,
            Status = RunStatus.Running,
            Inputs = run.Inputs.ToJsonString(),
            IdempotencyKey = run.IdempotencyKey,
            StartedAt = run.StartedAt,
        };

        TimeSpan? artifactRetention = null;
        var pipelineSteps = new Dictionary<int, Guid>();
        if (run.Kind is RunKind.Pipeline or RunKind.PipelineTest)
        {
            var pipeline = await db.PipelineVersions.AsNoTracking()
                .Where(v => v.Pipeline.Key == run.TargetKey && v.Version == run.TargetVersion)
                .Select(v => new { VersionId = v.Id, v.PipelineId, v.Pipeline.ArtifactRetentionDays, v.Pipeline.RunRetentionDays })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            row.PipelineKey = run.TargetKey;
            row.PipelineVersion = run.TargetVersion;
            if (pipeline is not null)
            {
                row.PipelineId = pipeline.PipelineId;
                row.PipelineVersionId = pipeline.VersionId;
                row.ExpiresAt = pipeline.RunRetentionDays is { } days ? createdAt.AddDays(days) : null;
                artifactRetention = pipeline.ArtifactRetentionDays is { } artifactDays ? TimeSpan.FromDays(artifactDays) : null;
                pipelineSteps = await db.PipelineSteps.AsNoTracking().Where(s => s.PipelineVersionId == pipeline.VersionId)
                    .ToDictionaryAsync(s => s.Order, s => s.Id, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            row.StageKey = run.TargetKey;
            row.StageVersion = run.TargetVersion;
        }

        var stageKeys = run.Steps.Select(s => s.StageKey).Distinct().ToList();
        var stageVersions = await db.StageVersions.AsNoTracking()
            .Where(v => stageKeys.Contains(v.Stage.Key))
            .Select(v => new { v.Id, v.Stage.Key, v.Version })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        Guid? StageVersionId(string key, int version) => stageVersions.FirstOrDefault(v => v.Key == key && v.Version == version)?.Id;

        if (row.Kind is RunKind.Stage or RunKind.StageTest)
        {
            row.StageVersionId = StageVersionId(run.TargetKey, run.TargetVersion);
        }

        foreach (var step in run.Steps)
        {
            row.Steps.Add(new StepRunEntity
            {
                Id = Guid.CreateVersion7(createdAt),
                RunId = row.Id,
                RunCreatedAt = createdAt,
                PipelineStepId = pipelineSteps.TryGetValue(step.Order, out var pipelineStepId) ? pipelineStepId : null,
                StepKey = step.StepKey,
                Order = step.Order,
                StageVersionId = StageVersionId(step.StageKey, step.StageVersion),
                StageKey = step.StageKey,
                StageVersion = step.StageVersion,
                Status = StepStatus.Pending,
            });
        }

        return (row, artifactRetention);
    }

    private static async Task<TimeSpan?> ArtifactRetentionAsync(PsrDbContext db, RunId runId, CancellationToken cancellationToken)
    {
        var createdAt = runId.CreatedAt;
        var days = await db.Runs.AsNoTracking()
            .Where(r => r.Id == runId.Value && r.CreatedAt == createdAt && r.PipelineId != null)
            .Join(db.Pipelines, r => r.PipelineId, p => p.Id, (r, p) => p.ArtifactRetentionDays)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return days is { } value ? TimeSpan.FromDays(value) : null;
    }

    /// <summary>Stores the files that are not stored yet (a file passed between steps keeps its artifact).</summary>
    private async Task StoreFilesAsync(
        PsrDbContext db,
        RunId runId,
        StepRunId? stepRunId,
        ArtifactDirection direction,
        JsonObject wire,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> files,
        TimeSpan? retention,
        CancellationToken cancellationToken)
    {
        foreach (var (field, list) in files)
        {
            var isList = wire[field] is JsonArray;
            for (var i = 0; i < list.Count; i++)
            {
                var file = list[i];
                if (file.ArtifactId is not null)
                {
                    continue;
                }

                var content = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                await using (content.ConfigureAwait(false))
                {
                    var stored = await artifacts.SaveAsync(db, new NewArtifact
                    {
                        RunId = runId,
                        StepRunId = stepRunId,
                        Direction = direction,
                        FieldName = field,
                        ListIndex = isList ? i : null,
                        FileName = file.FileName,
                        ContentType = file.ContentType,
                        Retention = retention ?? options.Retention.Artifacts,
                    }, content, cancellationToken).ConfigureAwait(false);
                    file.AttachArtifact(new ArtifactId(stored.Id));
                }
            }
        }
    }
}
