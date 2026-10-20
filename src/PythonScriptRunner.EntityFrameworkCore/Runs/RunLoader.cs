using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>A run read from the database with its steps, attempts, log streams and files.</summary>
internal sealed record LoadedRun(RunEntity Run, IReadOnlyList<(Guid AttemptId, LogStream Stream)> Logs, IReadOnlyDictionary<Guid, ArtifactEntity> Artifacts);

/// <summary>Reads persisted runs (the partition is found from the timestamp of the run identifier).</summary>
internal sealed class RunLoader(ArtifactStore artifactStore)
{
    public static async Task<LoadedRun?> LoadAsync(PsrDbContext db, RunId runId, CancellationToken cancellationToken)
    {
        var createdAt = runId.CreatedAt;
        var run = await db.Runs.AsNoTracking()
            .Include(r => r.Steps).ThenInclude(s => s.Attempts)
            .AsSplitQuery()
            .SingleOrDefaultAsync(r => r.Id == runId.Value && r.CreatedAt == createdAt, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var attemptIds = run.Steps.SelectMany(s => s.Attempts).Select(a => a.Id).ToList();
        var logs = await db.AttemptLogs.AsNoTracking()
            .Where(l => l.RunCreatedAt == createdAt && attemptIds.Contains(l.AttemptId))
            .Select(l => new { l.AttemptId, l.Stream })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(a => a.RunId == runId.Value && a.RunCreatedAt == createdAt)
            .ToDictionaryAsync(a => a.Id, cancellationToken).ConfigureAwait(false);
        return new LoadedRun(run, [.. logs.Select(l => (l.AttemptId, l.Stream))], artifacts);
    }

    public static RunSnapshot ToSnapshot(LoadedRun loaded)
    {
        var run = loaded.Run;
        var stepKeys = run.Steps.ToDictionary(s => s.Id, s => s.StepKey);
        return new RunSnapshot
        {
            Id = new RunId(run.Id),
            Kind = run.Kind,
            Mode = run.Mode,
            Status = run.Status,
            TargetKey = TargetKey(run),
            TargetVersion = TargetVersion(run),
            Trigger = run.Trigger,
            TriggeredBy = run.TriggeredBy,
            Inputs = StoredJson.Object(run.Inputs) ?? [],
            Outputs = StoredJson.Object(run.Outputs),
            Error = StoredJson.Error(run.Error),
            IdempotencyKey = run.IdempotencyKey,
            CorrelationId = run.CorrelationId,
            ScheduleId = run.ScheduleId is { } scheduleId ? new ScheduleId(scheduleId) : null,
            TriggerDetails = StoredJson.Object(run.TriggerDetails),
            CreatedAt = run.CreatedAt,
            QueuedAt = run.QueuedAt,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            CancelRequestedAt = run.CancelRequestedAt,
            CancelRequestedBy = run.CancelRequestedBy,
            ExpiresAt = run.ExpiresAt,
            Steps = [.. run.Steps.OrderBy(s => s.Order).Select(s => new StepRunSnapshot
            {
                Id = new StepRunId(s.Id),
                StepKey = s.StepKey,
                Order = s.Order,
                StageKey = s.StageKey,
                StageVersion = s.StageVersion,
                Status = s.Status,
                CurrentAttempt = s.CurrentAttempt,
                Inputs = StoredJson.Object(s.Inputs),
                Outputs = StoredJson.Object(s.Outputs),
                Error = StoredJson.Error(s.Error),
                StartedAt = s.StartedAt,
                FinishedAt = s.FinishedAt,
                Attempts = [.. s.Attempts.OrderBy(a => a.Attempt).Select(a => new AttemptSnapshot
                {
                    Id = new AttemptId(a.Id),
                    Number = a.Attempt,
                    Cause = a.Cause,
                    TriggeredBy = a.TriggeredBy,
                    Status = a.Status,
                    WorkerId = a.WorkerId,
                    EnvironmentHash = a.EnvironmentHash,
                    ExitCode = a.ExitCode,
                    Error = StoredJson.Error(a.Error),
                    StartedAt = a.StartedAt,
                    FinishedAt = a.FinishedAt,
                    Duration = a.DurationMs is { } duration ? TimeSpan.FromMilliseconds(duration) : null,
                    Overhead = a.OverheadMs is { } overhead ? TimeSpan.FromMilliseconds(overhead) : null,
                    Logs = [.. loaded.Logs.Where(l => l.AttemptId == a.Id).Select(l => l.Stream).Order()],
                })],
            })],
            Artifacts = [.. loaded.Artifacts.Values.OrderBy(a => a.Id)
                .Select(a => ArtifactStore.ToInfo(a, a.StepRunId is { } stepRunId ? stepKeys.GetValueOrDefault(stepRunId) : null))],
        };
    }

    /// <summary>Builds the result returned for a deduplicated request (files are streamed from the database on demand).</summary>
    public RunResult ToResult(LoadedRun loaded, bool deduplicated)
    {
        var run = loaded.Run;
        var (outputs, files) = ArtifactRefs.ToWire(StoredJson.Object(run.Outputs), loaded.Artifacts, artifactStore.OpenReadAsync);
        var duration = run.StartedAt is { } started && run.FinishedAt is { } finished ? finished - started : TimeSpan.Zero;
        return new RunResult(new RunId(run.Id), run.Kind, run.Status, duration, ToStepResults(loaded), outputs, files, StoredJson.Error(run.Error),
            deduplicated: deduplicated);
    }

    /// <summary>Steps as the engine sees them (outputs in wire format with their files).</summary>
    public List<StepResult> ToStepResults(LoadedRun loaded) =>
        [.. loaded.Run.Steps.OrderBy(s => s.Order).Select(s =>
        {
            var (outputs, files) = ArtifactRefs.ToWire(StoredJson.Object(s.Outputs), loaded.Artifacts, artifactStore.OpenReadAsync);
            return new StepResult
            {
                StepKey = s.StepKey,
                Order = s.Order,
                StageKey = s.StageKey,
                StageVersion = s.StageVersion,
                Status = s.Status,
                Attempts = [.. s.Attempts.OrderBy(a => a.Attempt).Select(ToAttempt)],
                Outputs = s.Outputs is null ? null : outputs,
                Files = files,
                Error = StoredJson.Error(s.Error),
            };
        })];

    public (System.Text.Json.Nodes.JsonObject Wire, Dictionary<string, IReadOnlyList<PsrFile>> Files) Inputs(LoadedRun loaded) =>
        ArtifactRefs.ToWire(StoredJson.Object(loaded.Run.Inputs), loaded.Artifacts, artifactStore.OpenReadAsync);

    public static string TargetKey(RunEntity run) => run.PipelineKey ?? run.StageKey ?? string.Empty;

    public static int TargetVersion(RunEntity run) => run.PipelineVersion ?? run.StageVersion ?? 0;

    private static AttemptResult ToAttempt(StepAttemptEntity attempt) => new()
    {
        Id = new AttemptId(attempt.Id),
        Number = attempt.Attempt,
        Cause = attempt.Cause,
        Status = attempt.Status,
        StartedAt = attempt.StartedAt,
        Duration = TimeSpan.FromMilliseconds(attempt.DurationMs ?? 0),
        Overhead = TimeSpan.FromMilliseconds(attempt.OverheadMs ?? 0),
        ExitCode = attempt.ExitCode,
        Error = StoredJson.Error(attempt.Error),
        EnvironmentHash = attempt.EnvironmentHash,
    };
}
