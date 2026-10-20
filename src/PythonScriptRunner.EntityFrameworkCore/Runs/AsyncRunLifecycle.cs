using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Entities;
using PythonScriptRunner.EntityFrameworkCore.Runs;
using PythonScriptRunner.EntityFrameworkCore.Scheduling;

namespace PythonScriptRunner.EntityFrameworkCore;

/// <summary>
/// Transactional lifecycle of queued runs (specs 005 and 006; D-112): creation with the outbox, claiming a step under a lease,
/// completing it (next step, retry, end of the run), the lease reaper, the per-pipeline concurrency queue and the cron
/// scheduler. Every change and its messages commit together; the queue and worker packages only move messages and processes.
/// Rows are always locked in the same order (pipeline or run first, then steps) to avoid deadlocks.
/// </summary>
public sealed class AsyncRunLifecycle
{
    private readonly IDbContextFactory<PsrDbContext> _factory;
    private readonly ArtifactStore _artifacts;
    private readonly IIdempotencyStore _idempotency;
    private readonly RunLoader _loader;
    private readonly IRunMessageOutbox _messages;
    private readonly PsrClock _clock;
    private readonly ICatalog _catalog;
    private readonly IRunEngine _engine;
    private readonly PsrEntityFrameworkOptions _options;

    internal AsyncRunLifecycle(
        IDbContextFactory<PsrDbContext> factory,
        ArtifactStore artifacts,
        IIdempotencyStore idempotency,
        RunLoader loader,
        IRunMessageOutbox messages,
        PsrClock clock,
        ICatalog catalog,
        IRunEngine engine,
        PsrEntityFrameworkOptions options)
    {
        _factory = factory;
        _artifacts = artifacts;
        _idempotency = idempotency;
        _loader = loader;
        _messages = messages;
        _clock = clock;
        _catalog = catalog;
        _engine = engine;
        _options = options;
    }

    /// <summary>
    /// Creates a queued run in one transaction: idempotency key, run (<c>Queued</c>, or <c>Pending</c> when its pipeline is at
    /// <c>max_concurrent_runs</c>), steps, input files, <c>ExecuteStep</c> of the first step and <c>run.queued</c>.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<RunHandle> CreateAsync(NewAsyncRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        EnsureQueue();
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = await _clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
                var handle = await CreateAsync(db, run, now, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return handle;
            }
        }
    }

    /// <summary>Writes a <c>run.rejected</c> event (a queue command that created no run).</summary>
    /// <param name="rejection">The event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PublishRejectedAsync(RunEvent rejection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            _messages.Publish(db, rejection, null);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Claims the step of an <c>ExecuteStep</c> command: <c>Queued → Running</c> only if the attempt is the expected one
    /// (duplicates and stale commands return <c>null</c>), records the attempt with a lease and loads what the engine needs.
    /// A cancelled run ends here (<c>null</c>).
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="workerId">Worker that takes the lease.</param>
    /// <param name="leaseDuration">Lease length (renewed with <see cref="RenewLeaseAsync"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ClaimedStep?> ClaimStepAsync(ExecuteStepCommand command, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        var runId = command.RunId;
        var createdAt = runId.CreatedAt;
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var attemptId = AttemptId.New();
            string triggeredBy;
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = await _clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
                var run = await LockRunAsync(db, runId, cancellationToken).ConfigureAwait(false);
                var step = run?.Steps.SingleOrDefault(s => s.Id == command.StepRunId.Value);
                if (run is null || step is null || step.Status != StepStatus.Queued || step.CurrentAttempt != command.Attempt - 1)
                {
                    return null;
                }

                if (run.Status is RunStatus.Cancelled or RunStatus.Cancelling)
                {
                    // A command delivered after the cancellation (e.g. a delayed retry): the step and the run end here.
                    step.Status = StepStatus.Cancelled;
                    step.FinishedAt = now;
                    if (run.Status == RunStatus.Cancelling)
                    {
                        await FinishCancelledAsync(db, run, now, cancellationToken).ConfigureAwait(false);
                    }

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                if (run.Status is not (RunStatus.Queued or RunStatus.Running))
                {
                    return null;
                }

                triggeredBy = command.TriggeredBy ?? run.TriggeredBy;
                step.Status = StepStatus.Running;
                step.CurrentAttempt = command.Attempt;
                step.StartedAt = now;
                step.FinishedAt = null;
                db.StepAttempts.Add(new StepAttemptEntity
                {
                    Id = attemptId.Value,
                    StepRunId = step.Id,
                    RunId = run.Id,
                    RunCreatedAt = createdAt,
                    Attempt = command.Attempt,
                    Cause = command.Cause,
                    TriggeredBy = triggeredBy,
                    Status = AttemptStatus.Running,
                    WorkerId = workerId,
                    LeaseExpiresAt = now + leaseDuration,
                    StartedAt = now,
                });
                if (run.Status == RunStatus.Queued)
                {
                    run.Status = RunStatus.Running;
                    run.StartedAt ??= now;
                    Publish(db, run, RunEventFactory.For(run, RunEventTypes.RunStarted, now));
                }

                Publish(db, run, RunEventFactory.ForStep(run, step, command.Attempt, RunEventTypes.StepStarted, now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            db.ChangeTracker.Clear();
            var loaded = await RunLoader.LoadAsync(db, runId, cancellationToken).ConfigureAwait(false)
                ?? throw new RunNotFoundException(runId);
            var claimed = loaded.Run.Steps.Single(s => s.Id == command.StepRunId.Value);
            var (_, pipeline) = loaded.Run.Kind is RunKind.Pipeline or RunKind.PipelineTest
                ? await RunDefinitions.LoadAsync(db, loaded.Run, cancellationToken).ConfigureAwait(false)
                : default;
            var stage = await RunDefinitions.LoadStageAsync(db, claimed, cancellationToken).ConfigureAwait(false);
            var (inputs, inputFiles) = _loader.Inputs(loaded);
            return new ClaimedStep
            {
                RunId = runId,
                Kind = loaded.Run.Kind,
                StepRunId = command.StepRunId,
                Order = claimed.Order,
                StepKey = claimed.StepKey,
                AttemptId = attemptId,
                Attempt = command.Attempt,
                Cause = command.Cause,
                TriggeredBy = triggeredBy,
                WorkerId = workerId,
                Stage = stage,
                Pipeline = pipeline,
                RunInputs = inputs,
                RunInputFiles = inputFiles,
                Steps = _loader.ToStepResults(loaded),
                TraceParent = loaded.Run.TraceParent,
            };
        }
    }

    /// <summary>Extends the lease of a claimed attempt. <c>false</c> when the reaper took it: the caller must stop the process.</summary>
    /// <param name="step">The claimed step.</param>
    /// <param name="leaseDuration">New lease length from now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> RenewLeaseAsync(ClaimedStep step, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        var createdAt = step.RunId.CreatedAt;
        var now = await _clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.StepAttempts
                .Where(a => a.Id == step.AttemptId.Value && a.RunCreatedAt == createdAt && a.Status == AttemptStatus.Running && a.WorkerId == step.WorkerId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.LeaseExpiresAt, now + leaseDuration), cancellationToken).ConfigureAwait(false) == 1;
        }
    }

    /// <summary>
    /// Records the outcome of a claimed attempt and decides what comes next, in one transaction: next step queued, automatic
    /// retry (delayed through the outbox), run succeeded, failed or cancelled. <see cref="StepCompletion.LeaseLost"/> when
    /// the reaper took the lease meanwhile (nothing is recorded).
    /// </summary>
    /// <param name="step">The claimed step.</param>
    /// <param name="execution">Result of <see cref="IRunEngine.ExecuteStepAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<StepCompletion> CompleteStepAsync(ClaimedStep step, StepExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(execution);
        var createdAt = step.RunId.CreatedAt;
        var result = execution.Step;
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = await _clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
                var attempt = execution.Attempt ?? new AttemptResult
                {
                    Id = step.AttemptId,
                    Number = step.Attempt,
                    Cause = step.Cause,
                    Status = AttemptStatus.Failed,
                    StartedAt = now,
                    Duration = TimeSpan.Zero,
                    Overhead = TimeSpan.Zero,
                    Error = result.Error,
                };
                if (!await AttemptRows.FinishAsync(db, createdAt, attempt, now, step.WorkerId, cancellationToken).ConfigureAwait(false))
                {
                    return StepCompletion.LeaseLost;
                }

                var run = await LockRunAsync(db, step.RunId, cancellationToken).ConfigureAwait(false) ?? throw new RunNotFoundException(step.RunId);
                var row = run.Steps.Single(s => s.Id == step.StepRunId.Value);
                if (execution.Inputs is { } inputs)
                {
                    row.Inputs = ArtifactRefs.ToStored(inputs, execution.InputFiles).ToJsonString();
                }

                var completion = result.Status switch
                {
                    StepStatus.Succeeded => await SucceededAsync(db, step, execution, run, row, now, cancellationToken).ConfigureAwait(false),
                    StepStatus.Cancelled => await EndRunAsync(db, run, row, StepStatus.Cancelled, result.Error, now, cancellationToken).ConfigureAwait(false),
                    _ => await FailedAsync(db, step, execution, run, row, now, cancellationToken).ConfigureAwait(false),
                };
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return completion;
            }
        }
    }

    /// <summary>
    /// Handles attempts whose lease expired (worker lost; D-85): a cancelling run ends <c>Cancelled</c>; an idempotent stage or
    /// one with retries left gets a new attempt (<see cref="AttemptCause.LeaseRecovery"/>, at most
    /// <paramref name="maxRecoveries"/> per step); otherwise the step fails with <see cref="ErrorKind.WorkerLost"/>.
    /// </summary>
    /// <param name="batchSize">Attempts handled per call (locked with <c>SKIP LOCKED</c>, so reapers do not collide).</param>
    /// <param name="maxRecoveries">Recoveries per step (QA-006-2: 3).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<LeaseReaping> ReapExpiredLeasesAsync(int batchSize = 50, int maxRecoveries = 3, CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = await _clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
                var expired = await db.StepAttempts.FromSql($"""
                    SELECT * FROM psr.step_attempts
                    WHERE status = 'Running' AND lease_expires_at < {now}
                    ORDER BY lease_expires_at
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                    """).ToListAsync(cancellationToken).ConfigureAwait(false);
                int recovered = 0, failed = 0, cancelled = 0;
                foreach (var attempt in expired)
                {
                    var error = new RunError(ErrorKind.WorkerLost, $"The worker {attempt.WorkerId} stopped renewing the lease of this attempt.")
                    {
                        Code = "WORKER_LOST",
                    };
                    attempt.Status = AttemptStatus.WorkerLost;
                    attempt.ErrorKind = ErrorKind.WorkerLost;
                    attempt.Error = StoredJson.Error(error);
                    attempt.FinishedAt = now;
                    attempt.DurationMs = (int)(now - attempt.StartedAt).TotalMilliseconds;
                    attempt.LeaseExpiresAt = null;

                    var run = await LockRunAsync(db, new RunId(attempt.RunId), cancellationToken).ConfigureAwait(false);
                    var step = run?.Steps.SingleOrDefault(s => s.Id == attempt.StepRunId);
                    if (run is null || step is null || step.Status != StepStatus.Running || step.CurrentAttempt != attempt.Attempt)
                    {
                        continue;
                    }

                    if (run.Status == RunStatus.Cancelling)
                    {
                        await EndRunAsync(db, run, step, StepStatus.Cancelled, new RunError(ErrorKind.Cancelled, "The run was cancelled."), now, cancellationToken)
                            .ConfigureAwait(false);
                        cancelled++;
                        continue;
                    }

                    var stage = await RunDefinitions.LoadStageAsync(db, step, cancellationToken).ConfigureAwait(false);
                    var series = await SeriesAsync(db, step, cancellationToken).ConfigureAwait(false);
                    var recoveries = series.Count(a => a.Cause == AttemptCause.LeaseRecovery);
                    if ((stage.Idempotent || RetriesUsed(series, stage) < stage.Retry.MaxAttempts) && recoveries < maxRecoveries)
                    {
                        step.Status = StepStatus.Queued;
                        step.Error = StoredJson.Error(error with { StepKey = step.StepKey });
                        _messages.ExecuteStep(db, new ExecuteStepCommand(new RunId(run.Id), run.CreatedAt, new StepRunId(step.Id), attempt.Attempt + 1)
                        {
                            Cause = AttemptCause.LeaseRecovery,
                            TriggeredBy = attempt.TriggeredBy,
                        }, now, run.TraceParent);
                        Publish(db, run, RunEventFactory.ForStep(run, step, attempt.Attempt, RunEventTypes.StepRetryScheduled, now) with
                        {
                            Error = RunEventError.From(error),
                            RetryAt = now,
                        });
                        recovered++;
                    }
                    else
                    {
                        await EndRunAsync(db, run, step, StepStatus.Failed, error with { StepKey = step.StepKey }, now, cancellationToken).ConfigureAwait(false);
                        failed++;
                    }
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LeaseReaping(recovered, failed, cancelled);
            }
        }
    }

    /// <summary>
    /// Fires the schedules due now (D-41, D-44, D-45, D-77): each one locked with <c>SKIP LOCKED</c>, so with N workers an
    /// occurrence fires once. Missed occurrences become one recovery run (<c>missedOccurrences</c> in its trigger details);
    /// an invalid firing creates no run, writes <c>last_error</c> and <c>run.rejected</c>, and the schedule stays enabled.
    /// </summary>
    /// <param name="batchSize">Schedules handled per call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ScheduleFiring> FireDueSchedulesAsync(int batchSize = 50, CancellationToken cancellationToken = default)
    {
        EnsureQueue();
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var now = await _clock.GetUtcNowAsync(cancellationToken).ConfigureAwait(false);
                var due = await db.Schedules.FromSql($"""
                    SELECT *, xmin FROM psr.schedules
                    WHERE is_enabled AND next_run_at <= {now}
                    ORDER BY next_run_at
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                    """).ToListAsync(cancellationToken).ConfigureAwait(false);
                int fired = 0, rejected = 0;
                foreach (var schedule in due)
                {
                    var pipeline = await db.Pipelines.AsNoTracking().Where(p => p.Id == schedule.PipelineId)
                        .Select(p => new { p.Key, p.LatestPublishedVersion }).SingleAsync(cancellationToken).ConfigureAwait(false);
                    var details = new JsonObject { ["scheduleId"] = schedule.Id.ToString(), ["scheduledFor"] = schedule.NextRunAt };
                    CronSchedule? cron = null;
                    try
                    {
                        cron = CronSchedule.Parse(schedule.Cron, schedule.TimeZone);
                        var missed = cron.CountOccurrences(schedule.NextRunAt!.Value, now);
                        if (missed > 1)
                        {
                            details["missedOccurrences"] = missed;
                        }

                        // The latest version is read from the database (not the catalog cache): a new publication applies at once (D-77).
                        var definition = await ScheduleRules.ResolveAsync(_catalog, pipeline.Key, schedule.PinnedVersion ?? pipeline.LatestPublishedVersion,
                            cancellationToken).ConfigureAwait(false);
                        var inputs = StoredJson.Object(schedule.Inputs) ?? [];
                        ScheduleRules.EnsureNoFileInputs(definition, inputs);
                        var handle = await CreateAsync(db, new NewAsyncRun
                        {
                            Kind = RunKind.Pipeline,
                            Pipeline = definition,
                            Inputs = _engine.BindInputs(definition.Inputs, inputs),
                            Trigger = RunTrigger.Schedule,
                            TriggeredBy = $"system:schedule:{schedule.Id}",
                            ScheduleId = new ScheduleId(schedule.Id),
                            TriggerDetails = details,
                        }, now, cancellationToken).ConfigureAwait(false);
                        schedule.LastRunId = handle.RunId.Value;
                        schedule.LastError = null;
                        fired++;
                    }
                    catch (PsrException ex)
                    {
                        schedule.LastError = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000];
                        _messages.Publish(db, new RunEvent
                        {
                            EventId = Guid.CreateVersion7(now),
                            Type = RunEventTypes.RunRejected,
                            OccurredAt = now,
                            Kind = RunKind.Pipeline,
                            PipelineKey = pipeline.Key,
                            Trigger = RunTrigger.Schedule,
                            TriggerDetails = details,
                            Error = Rejection(ex),
                        }, null);
                        rejected++;
                    }

                    schedule.LastFiredAt = now;
                    schedule.NextRunAt = cron?.Next(now);
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ScheduleFiring(fired, rejected);
            }
        }
    }

    /// <summary>Runs waiting for a free slot of their pipeline (gauge <c>psr.runs.pending</c>).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> CountPendingRunsAsync(CancellationToken cancellationToken = default)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.Runs.CountAsync(r => r.Status == RunStatus.Pending, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reasons of a rejected request.</summary>
    /// <param name="exception">Why it was rejected.</param>
    public static RunEventError Rejection(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var (code, reasons) = exception switch
        {
            InputValidationException invalid => ("INPUT_INVALID", invalid.Errors.Select(e => $"{e.Field}: {e.Message}").ToList()),
            PsrDomainException domain => (domain.Code, [domain.Message]),
            PipelineNotFoundException => ("PIPELINE_NOT_FOUND", [exception.Message]),
            VersionNotFoundException or VersionNotRunnableException => ("VERSION_NOT_RUNNABLE", [exception.Message]),
            _ => ("REJECTED", (List<string>)[exception.Message]),
        };
        return new RunEventError("Rejected", code, exception.Message, null) { Reasons = reasons };
    }

    /// <summary>Creates the run inside the caller's transaction.</summary>
    internal async Task<RunHandle> CreateAsync(PsrDbContext db, NewAsyncRun run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (targetKey, targetVersion, steps) = run switch
        {
            { Pipeline: { } pipeline } => (pipeline.Key, pipeline.Version,
                (IReadOnlyList<PlannedStep>)[.. pipeline.Steps.Select((s, i) => new PlannedStep(s.Key, i + 1, s.StageKey, s.StageVersion))]),
            { Stage: { } stage } => (stage.Key, stage.Version, [new PlannedStep(stage.Key, 1, stage.Key, stage.Version)]),
            _ => throw new ArgumentException("Set the stage or the pipeline of the run.", nameof(run)),
        };
        var runId = RunId.New(now);
        if ((run.IdempotencyKey ?? run.ImplicitIdempotencyKey) is { } key
            && await _idempotency.ReserveAsync(db, IdempotencyScopes.For(run.Kind, targetKey), key, runId, cancellationToken).ConfigureAwait(false) is { } existingId
            && await db.Runs.SingleOrDefaultAsync(r => r.Id == existingId.Value && r.CreatedAt == existingId.CreatedAt, cancellationToken).ConfigureAwait(false)
                is { } existing)
        {
            if (run.AnnounceDuplicate)
            {
                Publish(db, existing, RunEventFactory.For(existing, RunEventTypes.RunQueued, now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return new RunHandle(existingId, existing.Status, Deduplicated: true);
        }

        var (row, retention) = await RunRows.NewAsync(db, runId, run.Kind, targetKey, targetVersion, steps, cancellationToken).ConfigureAwait(false);
        var status = RunStatus.Queued;
        if (row.PipelineId is { } pipelineId && await LockPipelineAsync(db, pipelineId, cancellationToken).ConfigureAwait(false) is { } limit
            && await CountActiveAsync(db, pipelineId, cancellationToken).ConfigureAwait(false) >= limit)
        {
            status = RunStatus.Pending;
        }

        row.Mode = ExecutionMode.Async;
        row.Status = status;
        row.Trigger = run.Trigger;
        row.TriggeredBy = run.TriggeredBy;
        row.Inputs = run.Inputs.Wire.ToJsonString();
        row.IdempotencyKey = run.IdempotencyKey;
        row.CorrelationId = run.CorrelationId;
        row.TraceParent = run.TraceParent;
        row.ScheduleId = run.ScheduleId?.Value;
        row.TriggerDetails = run.TriggerDetails?.ToJsonString();
        row.QueuedAt = now;
        db.Runs.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await RunRows.StoreFilesAsync(db, _artifacts, runId, null, ArtifactDirection.Input, run.Inputs.Wire, run.Inputs.Files,
            retention ?? _options.Retention.Artifacts, cancellationToken).ConfigureAwait(false);
        row.Inputs = ArtifactRefs.ToStored(run.Inputs.Wire, run.Inputs.Files).ToJsonString();
        if (status == RunStatus.Queued)
        {
            QueueStep(db, row, row.Steps.Single(s => s.Order == 1), now, AttemptCause.Initial, row.TriggeredBy);
        }

        Publish(db, row, RunEventFactory.For(row, RunEventTypes.RunQueued, now));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RunHandle(runId, status, Deduplicated: false);
    }

    /// <summary>
    /// Cancels a run inside the caller's transaction (its row is locked here): waiting runs end now; running ones move to
    /// <c>Cancelling</c>, every instance is told to stop the process, and a queued run between steps ends now.
    /// </summary>
    /// <returns>The status after the call, or <c>null</c> when the run does not exist.</returns>
    internal async Task<RunStatus?> CancelAsync(PsrDbContext db, RunId runId, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var run = await LockRunAsync(db, runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        switch (run.Status)
        {
            case RunStatus.Pending or RunStatus.Queued:
                run.CancelRequestedAt = now;
                run.CancelRequestedBy = actor.Subject;
                await FinishCancelledAsync(db, run, now, cancellationToken).ConfigureAwait(false);
                break;
            case RunStatus.Running or RunStatus.Cancelling:
                run.Status = RunStatus.Cancelling;
                run.CancelRequestedAt ??= now;
                run.CancelRequestedBy ??= actor.Subject;
                var running = run.Steps.FirstOrDefault(s => s.Status == StepStatus.Running);
                _messages.CancelRun(db, new CancelRunCommand(runId, running is null ? null : new StepRunId(running.Id), actor.Subject), now);
                if (run.Mode == ExecutionMode.Async && running is null)
                {
                    // Between steps (e.g. waiting for a retry): nothing is executing, so the run ends now.
                    await FinishCancelledAsync(db, run, now, cancellationToken).ConfigureAwait(false);
                }

                break;
            default:
                throw new PsrDomainException(PsrErrorCodes.RunNotCancellable, $"Run {runId} already finished ({run.Status}).");
        }

        return run.Status;
    }

    /// <summary>
    /// Queues a failed run again inside the caller's transaction (D-76, QA-003-3): successful steps keep their outputs and the
    /// first unsuccessful step gets a new attempt (<see cref="AttemptCause.Reprocess"/>).
    /// </summary>
    internal async Task<bool> QueueReprocessAsync(PsrDbContext db, RunId runId, Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureQueue();
        var run = await LockRunAsync(db, runId, cancellationToken).ConfigureAwait(false);
        if (run is not { Status: RunStatus.Failed })
        {
            return false;
        }

        run.Status = RunStatus.Running;
        run.Mode = ExecutionMode.Async;
        run.Outputs = null;
        run.Error = null;
        run.FinishedAt = null;
        foreach (var skipped in run.Steps.Where(s => s.Status == StepStatus.Skipped))
        {
            skipped.Status = StepStatus.Pending;
        }

        var step = run.Steps.OrderBy(s => s.Order).First(s => s.Status != StepStatus.Succeeded);
        QueueStep(db, run, step, now, step.CurrentAttempt == 0 ? AttemptCause.Initial : AttemptCause.Reprocess, actor.Subject);
        Publish(db, run, RunEventFactory.For(run, RunEventTypes.RunStarted, now));
        return true;
    }

    private async Task<StepCompletion> SucceededAsync(
        PsrDbContext db, ClaimedStep claimed, StepExecution execution, RunEntity run, StepRunEntity step, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = execution.Step;
        var retention = await RunRows.ArtifactRetentionAsync(db, claimed.RunId, cancellationToken).ConfigureAwait(false) ?? _options.Retention.Artifacts;
        await RunRows.StoreFilesAsync(db, _artifacts, claimed.RunId, claimed.StepRunId, ArtifactDirection.Output, result.Outputs!, result.Files, retention,
            cancellationToken).ConfigureAwait(false);
        step.Outputs = ArtifactRefs.ToStored(result.Outputs!, result.Files).ToJsonString();
        step.Status = StepStatus.Succeeded;
        step.Error = null;
        step.FinishedAt = now;
        Publish(db, run, RunEventFactory.ForStep(run, step, claimed.Attempt, RunEventTypes.StepSucceeded, now));
        if (run.Status == RunStatus.Cancelling)
        {
            await FinishCancelledAsync(db, run, now, cancellationToken).ConfigureAwait(false);
            return StepCompletion.RunCancelled;
        }

        if (run.Steps.SingleOrDefault(s => s.Order == step.Order + 1) is { } next)
        {
            QueueStep(db, run, next, now, next.CurrentAttempt == 0 ? AttemptCause.Initial : AttemptCause.Reprocess, claimed.TriggeredBy);
            return StepCompletion.NextStepQueued;
        }

        run.Outputs = execution.RunOutputs is { } outputs ? ArtifactRefs.ToStored(outputs, execution.RunOutputFiles).ToJsonString() : "{}";
        run.Status = RunStatus.Succeeded;
        run.Error = null;
        run.FinishedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.RunId == run.Id && a.RunCreatedAt == run.CreatedAt)
            .ToDictionaryAsync(a => a.Id, cancellationToken).ConfigureAwait(false);
        Publish(db, run, RunEventFactory.Succeeded(run, artifacts, now));
        await PromotePendingAsync(db, run, now, cancellationToken).ConfigureAwait(false);
        return StepCompletion.RunSucceeded;
    }

    private async Task<StepCompletion> FailedAsync(
        PsrDbContext db, ClaimedStep claimed, StepExecution execution, RunEntity run, StepRunEntity step, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = execution.Step;
        var error = result.Error ?? new RunError(ErrorKind.ProcessCrashed, "The step failed.") { StepKey = step.StepKey };
        if (run.Status != RunStatus.Cancelling && execution.Attempt is not null)
        {
            var series = await SeriesAsync(db, step, cancellationToken).ConfigureAwait(false);
            var used = RetriesUsed(series, claimed.Stage);
            if (used < claimed.Stage.Retry.MaxAttempts)
            {
                var retryAt = now + claimed.Stage.Retry.GetDelay(used + 1);
                step.Status = StepStatus.Queued;
                step.Error = StoredJson.Error(error);
                QueueStep(db, run, step, retryAt, AttemptCause.AutoRetry, claimed.TriggeredBy);
                Publish(db, run, RunEventFactory.ForStep(run, step, claimed.Attempt, RunEventTypes.StepRetryScheduled, now) with
                {
                    Error = RunEventError.From(error),
                    RetryAt = retryAt,
                });
                return StepCompletion.RetryScheduled;
            }
        }

        var status = result.Status == StepStatus.TimedOut ? StepStatus.TimedOut : StepStatus.Failed;
        return await EndRunAsync(db, run, step, status, error, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the run at <paramref name="step"/>: the step gets its final status, the following steps are skipped, the run fails
    /// (or is cancelled) and its pipeline slot is released.
    /// </summary>
    private async Task<StepCompletion> EndRunAsync(
        PsrDbContext db, RunEntity run, StepRunEntity step, StepStatus status, RunError? error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        step.Status = status;
        step.Error = StoredJson.Error(error);
        step.FinishedAt = now;
        if (status == StepStatus.Cancelled || run.Status == RunStatus.Cancelling)
        {
            await FinishCancelledAsync(db, run, now, cancellationToken).ConfigureAwait(false);
            return StepCompletion.RunCancelled;
        }

        Publish(db, run, RunEventFactory.ForStep(run, step, step.CurrentAttempt, RunEventTypes.StepFailed, now) with { Error = RunEventError.From(error) });
        foreach (var later in run.Steps.Where(s => s.Order > step.Order && s.Status is StepStatus.Pending or StepStatus.Queued))
        {
            later.Status = StepStatus.Skipped;
        }

        run.Status = RunStatus.Failed;
        run.Error = StoredJson.Error(error);
        run.FinishedAt = now;
        Publish(db, run, RunEventFactory.For(run, RunEventTypes.RunFailed, now) with { Error = RunEventError.From(error) });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PromotePendingAsync(db, run, now, cancellationToken).ConfigureAwait(false);
        return StepCompletion.RunFailed;
    }

    /// <summary>Ends a run as cancelled: open steps are cancelled, pending ones skipped, <c>run.cancelled</c>, slot released.</summary>
    private async Task FinishCancelledAsync(PsrDbContext db, RunEntity run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var step in run.Steps)
        {
            if (step.Status is StepStatus.Running or StepStatus.Queued)
            {
                step.Status = StepStatus.Cancelled;
                step.FinishedAt = now;
            }
            else if (step.Status == StepStatus.Pending)
            {
                step.Status = StepStatus.Skipped;
            }
        }

        run.Status = RunStatus.Cancelled;
        run.FinishedAt = now;
        run.Error ??= StoredJson.Error(new RunError(ErrorKind.Cancelled, $"Cancelled by {run.CancelRequestedBy ?? "the system"}."));
        Publish(db, run, RunEventFactory.For(run, RunEventTypes.RunCancelled, now));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PromotePendingAsync(db, run, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A finished queued run frees a slot of its pipeline: the oldest <c>Pending</c> runs become <c>Queued</c> (FIFO; D-39).</summary>
    private async Task PromotePendingAsync(PsrDbContext db, RunEntity finished, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (finished.Mode != ExecutionMode.Async || finished.PipelineId is not { } pipelineId)
        {
            return;
        }

        var limit = await LockPipelineAsync(db, pipelineId, cancellationToken).ConfigureAwait(false);
        var active = await CountActiveAsync(db, pipelineId, cancellationToken).ConfigureAwait(false);
        while (limit is null || active < limit)
        {
            var waiting = await db.Runs.FromSql($"""
                SELECT * FROM psr.runs
                WHERE pipeline_id = {pipelineId} AND mode = 'Async' AND status = 'Pending'
                ORDER BY created_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (waiting.Count == 0)
            {
                break;
            }

            var run = waiting[0];
            var first = await db.StepRuns.SingleAsync(s => s.RunId == run.Id && s.RunCreatedAt == run.CreatedAt && s.Order == 1, cancellationToken)
                .ConfigureAwait(false);
            run.Status = RunStatus.Queued;
            QueueStep(db, run, first, now, AttemptCause.Initial, run.TriggeredBy);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            active++;
        }
    }

    /// <summary>Marks the step <c>Queued</c> and writes the <c>ExecuteStep</c> of its next attempt.</summary>
    private void QueueStep(PsrDbContext db, RunEntity run, StepRunEntity step, DateTimeOffset availableAt, AttemptCause cause, string triggeredBy)
    {
        step.Status = StepStatus.Queued;
        step.FinishedAt = null;
        _messages.ExecuteStep(db, new ExecuteStepCommand(new RunId(run.Id), run.CreatedAt, new StepRunId(step.Id), step.CurrentAttempt + 1)
        {
            Cause = cause,
            TriggeredBy = triggeredBy,
        }, availableAt, run.TraceParent);
    }

    private void Publish(PsrDbContext db, RunEntity run, RunEvent runEvent) => _messages.Publish(db, runEvent, run.TraceParent);

    private void EnsureQueue()
    {
        if (!_messages.IsEnabled)
        {
            throw new PsrConfigurationException("Queued runs need a run queue: register the RabbitMQ package (AddRabbitMq).");
        }
    }

    /// <summary>Locks the run row (<c>FOR UPDATE</c>) and loads it with its steps for change.</summary>
    private static async Task<RunEntity?> LockRunAsync(PsrDbContext db, RunId runId, CancellationToken cancellationToken)
    {
        var createdAt = runId.CreatedAt;
        var locked = await IdempotencyStore.ScalarAsync<int?>(db, "SELECT 1 FROM psr.runs WHERE id = $1 AND created_at = $2 FOR UPDATE", cancellationToken,
            runId.Value, createdAt).ConfigureAwait(false);
        return locked is null
            ? null
            : await db.Runs.Include(r => r.Steps).SingleAsync(r => r.Id == runId.Value && r.CreatedAt == createdAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Locks the pipeline row and returns its <c>max_concurrent_runs</c> (creations and releases of a pipeline queue up here).</summary>
    private static Task<int?> LockPipelineAsync(PsrDbContext db, Guid pipelineId, CancellationToken cancellationToken) =>
        IdempotencyStore.ScalarAsync<int?>(db, "SELECT max_concurrent_runs FROM psr.pipelines WHERE id = $1 FOR UPDATE", cancellationToken, pipelineId);

    /// <summary>Queued runs of the pipeline that hold a slot (synchronous runs do not count; QA-006-1).</summary>
    private static Task<int> CountActiveAsync(PsrDbContext db, Guid pipelineId, CancellationToken cancellationToken) =>
        db.Runs.CountAsync(r => r.PipelineId == pipelineId && r.Mode == ExecutionMode.Async
            && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.Cancelling), cancellationToken);

    /// <summary>Attempts of the current series of a step: since its last <c>Initial</c> or <c>Reprocess</c> attempt.</summary>
    private static async Task<List<StepAttemptEntity>> SeriesAsync(PsrDbContext db, StepRunEntity step, CancellationToken cancellationToken)
    {
        var attempts = await db.StepAttempts.AsNoTracking()
            .Where(a => a.StepRunId == step.Id && a.RunCreatedAt == step.RunCreatedAt)
            .OrderBy(a => a.Attempt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var start = attempts.FindLastIndex(a => a.Cause is AttemptCause.Initial or AttemptCause.Reprocess);
        return start < 0 ? attempts : attempts[start..];
    }

    /// <summary>Automatic retries used by a series; a recovery of a non-idempotent stage uses a retry (D-85).</summary>
    private static int RetriesUsed(IEnumerable<StepAttemptEntity> series, StageDefinition stage) =>
        series.Count(a => a.Cause == AttemptCause.AutoRetry || (a.Cause == AttemptCause.LeaseRecovery && !stage.Idempotent));
}
