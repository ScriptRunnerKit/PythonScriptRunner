namespace PythonScriptRunner.Core.Pipelines;

/// <summary>Validates pipelines against the stages available in the <see cref="ICatalog"/>.</summary>
internal sealed class PipelineValidator(ICatalog catalog) : IPipelineValidator
{
    public async ValueTask<ValidationReport> ValidateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var stages = new Dictionary<(string, int), StageDefinition?>();
        foreach (var step in pipeline.Steps)
        {
            var reference = (step.StageKey, step.StageVersion);
            if (!stages.ContainsKey(reference))
            {
                stages[reference] = await catalog.FindStageAsync(step.StageKey, step.StageVersion, cancellationToken).ConfigureAwait(false);
            }
        }

        return PipelineRules.Validate(pipeline, (key, version) => stages[(key, version)]);
    }
}
