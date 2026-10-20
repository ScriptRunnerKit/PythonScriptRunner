using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>Builds run rows and stores run files (shared by the synchronous recorder and the queued runs).</summary>
internal static class RunRows
{
    /// <summary>
    /// Builds the run row with its pending steps; resolves catalog ids and the retention of the pipeline. The caller sets the
    /// mode, status, trigger and inputs.
    /// </summary>
    public static async Task<(RunEntity Run, TimeSpan? ArtifactRetention)> NewAsync(
        PsrDbContext db, RunId runId, RunKind kind, string targetKey, int targetVersion, IReadOnlyList<PlannedStep> steps, CancellationToken cancellationToken)
    {
        var createdAt = runId.CreatedAt;
        var row = new RunEntity
        {
            Id = runId.Value,
            CreatedAt = createdAt,
            Kind = kind,
            TriggeredBy = string.Empty,
            Inputs = "{}",
        };

        TimeSpan? artifactRetention = null;
        var pipelineSteps = new Dictionary<int, Guid>();
        if (kind is RunKind.Pipeline or RunKind.PipelineTest)
        {
            var pipeline = await db.PipelineVersions.AsNoTracking()
                .Where(v => v.Pipeline.Key == targetKey && v.Version == targetVersion)
                .Select(v => new { VersionId = v.Id, v.PipelineId, v.Pipeline.ArtifactRetentionDays, v.Pipeline.RunRetentionDays })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            row.PipelineKey = targetKey;
            row.PipelineVersion = targetVersion;
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
            row.StageKey = targetKey;
            row.StageVersion = targetVersion;
        }

        var stageKeys = steps.Select(s => s.StageKey).Distinct().ToList();
        var stageVersions = await db.StageVersions.AsNoTracking()
            .Where(v => stageKeys.Contains(v.Stage.Key))
            .Select(v => new { v.Id, v.Stage.Key, v.Version })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        Guid? StageVersionId(string key, int version) => stageVersions.FirstOrDefault(v => v.Key == key && v.Version == version)?.Id;

        if (kind is RunKind.Stage or RunKind.StageTest)
        {
            row.StageVersionId = StageVersionId(targetKey, targetVersion);
        }

        foreach (var step in steps)
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

    /// <summary>Retention of the files of a run (shortened by its pipeline), or <c>null</c> for the global one.</summary>
    public static async Task<TimeSpan?> ArtifactRetentionAsync(PsrDbContext db, RunId runId, CancellationToken cancellationToken)
    {
        var createdAt = runId.CreatedAt;
        var days = await db.Runs.AsNoTracking()
            .Where(r => r.Id == runId.Value && r.CreatedAt == createdAt && r.PipelineId != null)
            .Join(db.Pipelines, r => r.PipelineId, p => p.Id, (r, p) => p.ArtifactRetentionDays)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return days is { } value ? TimeSpan.FromDays(value) : null;
    }

    /// <summary>Stores the files that are not stored yet (a file passed between steps keeps its artifact).</summary>
    public static async Task StoreFilesAsync(
        PsrDbContext db,
        ArtifactStore artifacts,
        RunId runId,
        StepRunId? stepRunId,
        ArtifactDirection direction,
        JsonObject wire,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> files,
        TimeSpan retention,
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
                        Retention = retention,
                    }, content, cancellationToken).ConfigureAwait(false);
                    file.AttachArtifact(new ArtifactId(stored.Id));
                }
            }
        }
    }
}
