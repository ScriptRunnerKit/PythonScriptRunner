using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>Loads the exact versions a run executes (any status: archived versions still run and can be reprocessed).</summary>
internal static class RunDefinitions
{
    public static async Task<(StageDefinition? Stage, PipelineDefinition? Pipeline)> LoadAsync(PsrDbContext db, RunEntity run, CancellationToken cancellationToken)
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

    /// <summary>The stage version executed by a step.</summary>
    public static async Task<StageDefinition> LoadStageAsync(PsrDbContext db, StepRunEntity step, CancellationToken cancellationToken)
    {
        var versions = db.StageVersions.AsNoTracking().Include(v => v.Stage);
        var version = await (step.StageVersionId is { } id
                ? versions.Where(v => v.Id == id)
                : versions.Where(v => v.Stage.Key == step.StageKey && v.Version == step.StageVersion))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new VersionNotFoundException(step.StageKey, step.StageVersion);
        return CatalogMapping.ToStage(version.Stage, version);
    }
}
