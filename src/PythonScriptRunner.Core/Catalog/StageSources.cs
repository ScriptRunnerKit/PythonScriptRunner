using System.Collections.Frozen;

namespace PythonScriptRunner.Core.Catalog;

/// <summary>Supplies stage definitions to the <see cref="CompositeCatalog"/>.</summary>
internal interface IStageSource
{
    /// <summary>Human-readable origin used in error messages.</summary>
    string Description { get; }

    IReadOnlyList<StageDefinition> LoadStages();
}

/// <summary>Stages defined in code with <see cref="StageBuilder"/>.</summary>
internal sealed class StaticStageSource(StageDefinition stage) : IStageSource
{
    public string Description => $"code ({stage.Key})";

    public IReadOnlyList<StageDefinition> LoadStages() => [stage];
}

/// <summary>In-memory catalog built once from every registered <see cref="IStageSource"/>.</summary>
internal sealed class CompositeCatalog(IEnumerable<IStageSource> sources) : ICatalog
{
    private readonly Lazy<FrozenDictionary<string, SortedList<int, StageDefinition>>> _stages =
        new(() => Load(sources), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Loads every source now (fail fast at startup).</summary>
    public void EnsureLoaded() => _ = _stages.Value;

    public ValueTask<StageDefinition?> FindStageAsync(string key, int? version, CancellationToken cancellationToken = default)
    {
        if (!_stages.Value.TryGetValue(key, out var versions))
        {
            return ValueTask.FromResult<StageDefinition?>(null);
        }

        if (version is null)
        {
            return ValueTask.FromResult<StageDefinition?>(versions.Values[^1]);
        }

        return ValueTask.FromResult(versions.GetValueOrDefault(version.Value));
    }

    private static FrozenDictionary<string, SortedList<int, StageDefinition>> Load(IEnumerable<IStageSource> sources)
    {
        var stages = new Dictionary<string, SortedList<int, StageDefinition>>(StringComparer.Ordinal);
        var origins = new Dictionary<(string, int), string>();
        foreach (var source in sources)
        {
            foreach (var stage in source.LoadStages())
            {
                StageDefinitionValidator.EnsureValid(stage, source.Description);
                if (origins.TryGetValue((stage.Key, stage.Version), out var other))
                {
                    throw new PsrConfigurationException(
                        $"Stage '{stage.Key}@{stage.Version}' is defined twice: {other} and {source.Description}.");
                }

                origins[(stage.Key, stage.Version)] = source.Description;
                if (!stages.TryGetValue(stage.Key, out var versions))
                {
                    stages[stage.Key] = versions = [];
                }

                versions[stage.Version] = stage;
            }
        }

        return stages.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
