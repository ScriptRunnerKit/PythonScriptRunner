using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using PythonScriptRunner.Core.Diagnostics;
using PythonScriptRunner.Core.Pipelines;

namespace PythonScriptRunner.Core.Catalog;

/// <summary>Supplies stage and pipeline definitions to the <see cref="CompositeCatalog"/>.</summary>
internal interface ICatalogSource
{
    /// <summary>Human-readable origin used in error messages.</summary>
    string Description { get; }

    IReadOnlyList<StageDefinition> LoadStages();

    IReadOnlyList<PipelineDefinition> LoadPipelines();
}

/// <summary>A stage or a pipeline defined in code.</summary>
internal sealed class StaticCatalogSource(StageDefinition? stage = null, PipelineDefinition? pipeline = null) : ICatalogSource
{
    public string Description => $"code ({stage?.Key ?? pipeline?.Key})";

    public IReadOnlyList<StageDefinition> LoadStages() => stage is null ? [] : [stage];

    public IReadOnlyList<PipelineDefinition> LoadPipelines() => pipeline is null ? [] : [pipeline];
}

/// <summary>In-memory catalog built once from every registered <see cref="ICatalogSource"/>; pipelines are validated on load.</summary>
internal sealed class CompositeCatalog : ICatalog
{
    private readonly Lazy<Snapshot> _snapshot;

    public CompositeCatalog(IEnumerable<ICatalogSource> sources, ILogger<CompositeCatalog> logger) =>
        _snapshot = new(() => Load([.. sources], logger), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Loads every source now (fail fast at startup).</summary>
    public void EnsureLoaded() => _ = _snapshot.Value;

    public ValueTask<StageDefinition?> FindStageAsync(string key, int? version, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Find(_snapshot.Value.Stages, key, version));

    public ValueTask<PipelineDefinition?> FindPipelineAsync(string key, int? version, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Find(_snapshot.Value.Pipelines, key, version));

    private static T? Find<T>(FrozenDictionary<string, SortedList<int, T>> items, string key, int? version)
        where T : class
    {
        if (!items.TryGetValue(key, out var versions))
        {
            return null;
        }

        return version is null ? versions.Values[^1] : versions.GetValueOrDefault(version.Value);
    }

    private static Snapshot Load(ICatalogSource[] sources, ILogger logger)
    {
        var stages = Collect(sources, s => s.LoadStages(), s => (s.Key, s.Version), "Stage");
        foreach (var (stage, origin) in stages.Values.SelectMany(v => v.Values))
        {
            StageDefinitionValidator.EnsureValid(stage, origin);
        }

        var pipelines = Collect(sources, s => s.LoadPipelines(), p => (p.Key, p.Version), "Pipeline");
        var stageMap = stages.ToFrozenDictionary(p => p.Key, p => new SortedList<int, StageDefinition>(p.Value.ToDictionary(v => v.Key, v => v.Value.Item)), StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (pipeline, origin) in pipelines.Values.SelectMany(v => v.Values))
        {
            var report = PipelineRules.Validate(pipeline, (key, version) => Find(stageMap, key, version));
            problems.AddRange(report.Errors.Select(e => $" - pipeline '{pipeline.Key}@{pipeline.Version}' ({origin}): {e}"));
            foreach (var warning in report.Warnings)
            {
                Log.PipelineWarning(logger, pipeline.Key, pipeline.Version, warning.ToString());
            }
        }

        if (problems.Count > 0)
        {
            throw new PsrConfigurationException("Invalid pipelines:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        return new Snapshot(
            stageMap,
            pipelines.ToFrozenDictionary(p => p.Key, p => new SortedList<int, PipelineDefinition>(p.Value.ToDictionary(v => v.Key, v => v.Value.Item)), StringComparer.Ordinal));
    }

    private static Dictionary<string, SortedList<int, (T Item, string Origin)>> Collect<T>(
        ICatalogSource[] sources, Func<ICatalogSource, IReadOnlyList<T>> load, Func<T, (string Key, int Version)> identity, string kind)
    {
        var items = new Dictionary<string, SortedList<int, (T, string)>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var item in load(source))
            {
                var (key, version) = identity(item);
                if (!items.TryGetValue(key, out var versions))
                {
                    items[key] = versions = [];
                }

                if (versions.TryGetValue(version, out var existing))
                {
                    throw new PsrConfigurationException($"{kind} '{key}@{version}' is defined twice: {existing.Item2} and {source.Description}.");
                }

                versions[version] = (item, source.Description);
            }
        }

        return items;
    }

    private sealed record Snapshot(
        FrozenDictionary<string, SortedList<int, StageDefinition>> Stages,
        FrozenDictionary<string, SortedList<int, PipelineDefinition>> Pipelines);
}
