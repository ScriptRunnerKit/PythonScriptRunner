using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace PythonScriptRunner.EntityFrameworkCore.Catalog;

/// <summary>
/// Catalog stored in PostgreSQL. Only published versions run; definitions and "latest published" lookups are cached for
/// <see cref="PsrEntityFrameworkOptions.LatestVersionCacheDuration"/> and invalidated in this process on every change.
/// </summary>
internal sealed class EfCatalog(IDbContextFactory<PsrDbContext> factory, PsrEntityFrameworkOptions options, TimeProvider time) : ICatalog
{
    private readonly ConcurrentDictionary<(string Key, int? Version), Cached<StageDefinition>> _stages = new();
    private readonly ConcurrentDictionary<(string Key, int? Version), Cached<PipelineDefinition>> _pipelines = new();

    public async ValueTask<StageDefinition?> FindStageAsync(string key, int? version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_stages.TryGetValue((key, version), out var cached) && cached.ExpiresAt > time.GetUtcNow())
        {
            return cached.Value;
        }

        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var query = db.StageVersions.AsNoTracking().Include(v => v.Stage).Where(v => v.Stage.Key == key);
            query = version is { } number
                ? query.Where(v => v.Version == number)
                : query.Where(v => v.Version == v.Stage.LatestPublishedVersion);
            var row = await query.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            if (row.Status != VersionStatus.Published)
            {
                throw new VersionNotRunnableException(key, row.Version, row.Status);
            }

            var stage = CatalogMapping.ToStage(row.Stage, row);
            _stages[(key, version)] = new(stage, time.GetUtcNow() + options.LatestVersionCacheDuration);
            return stage;
        }
    }

    public async ValueTask<PipelineDefinition?> FindPipelineAsync(string key, int? version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_pipelines.TryGetValue((key, version), out var cached) && cached.ExpiresAt > time.GetUtcNow())
        {
            return cached.Value;
        }

        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var query = db.PipelineVersions.AsNoTracking().Include(v => v.Pipeline).Where(v => v.Pipeline.Key == key);
            query = version is { } number
                ? query.Where(v => v.Version == number)
                : query.Where(v => v.Version == v.Pipeline.LatestPublishedVersion);
            var row = await query.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            if (row.Status != VersionStatus.Published)
            {
                throw new VersionNotRunnableException(key, row.Version, row.Status);
            }

            var steps = await LoadStepsAsync(db, row.Id, cancellationToken).ConfigureAwait(false);
            var pipeline = CatalogMapping.ToPipeline(row.Pipeline, row, steps);
            _pipelines[(key, version)] = new(pipeline, time.GetUtcNow() + options.LatestVersionCacheDuration);
            return pipeline;
        }
    }

    /// <summary>Forgets cached definitions of a stage (called after every change in this process).</summary>
    public void InvalidateStage(string key)
    {
        foreach (var entry in _stages.Keys.Where(k => k.Key == key))
        {
            _stages.TryRemove(entry, out _);
        }
    }

    /// <summary>Forgets cached definitions of a pipeline.</summary>
    public void InvalidatePipeline(string key)
    {
        foreach (var entry in _pipelines.Keys.Where(k => k.Key == key))
        {
            _pipelines.TryRemove(entry, out _);
        }
    }

    public static async Task<List<StepRow>> LoadStepsAsync(PsrDbContext db, Guid pipelineVersionId, CancellationToken cancellationToken) =>
        await db.PipelineSteps.AsNoTracking()
            .Where(s => s.PipelineVersionId == pipelineVersionId)
            .OrderBy(s => s.Order)
            .Select(s => new StepRow(s, s.StageVersion.Stage.Key, s.StageVersion.Version))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private sealed record Cached<T>(T Value, DateTimeOffset ExpiresAt);
}
