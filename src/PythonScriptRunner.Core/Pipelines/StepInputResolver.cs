using System.Text.Json.Nodes;
using PythonScriptRunner.Core.Values;

namespace PythonScriptRunner.Core.Pipelines;

/// <summary>Inputs of one pipeline step after resolving its bindings (files are shared with their source, never copied).</summary>
internal sealed record StepInputs(JsonObject Wire, IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> Files, IReadOnlyList<InputError> Errors);

/// <summary>Resolves the inputs of a step from pipeline inputs, earlier step outputs, constants and implicit bindings.</summary>
internal static class StepInputResolver
{
    public static StepInputs Resolve(
        StageDefinition stage,
        PipelineDefinition pipeline,
        int index,
        JsonObject pipelineInputs,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> pipelineFiles,
        IReadOnlyDictionary<string, StepResult> completed)
    {
        var step = pipeline.Steps[index];
        var wire = new JsonObject();
        var files = new Dictionary<string, IReadOnlyList<PsrFile>>(StringComparer.Ordinal);
        var errors = new List<InputError>();
        foreach (var target in stage.Inputs)
        {
            var source = step.Bindings.FirstOrDefault(b => b.Input == target.Name)?.Source ?? Implicit(target, index, pipeline);
            var (value, sourceFiles) = Read(source, pipelineInputs, pipelineFiles, completed);
            if (target.Type.IsFile)
            {
                if (sourceFiles is { Count: > 0 })
                {
                    files[target.Name] = sourceFiles;
                }
                else if (target.Required)
                {
                    errors.Add(Missing(target, source));
                }

                continue;
            }

            if (value is null)
            {
                if (target.Default is not null)
                {
                    wire[target.Name] = target.Default.DeepClone();
                }
                else if (target.Required)
                {
                    errors.Add(Missing(target, source));
                }

                continue;
            }

            var coerced = Coercion.Coerce(value, target.Type);
            var wireErrors = new List<WireError>();
            WireValidator.Validate(coerced, target, target.Name, wireErrors);
            errors.AddRange(wireErrors.Select(e => new InputError(e.Path, e.Code, e.Message)));
            wire[target.Name] = coerced;
        }

        return new StepInputs(wire, files, errors);
    }

    private static BindingSource? Implicit(FieldDefinition target, int index, PipelineDefinition pipeline)
    {
        if (index > 0)
        {
            return new StepOutputSource(pipeline.Steps[index - 1].Key, target.Name);
        }

        return pipeline.Inputs.Any(f => f.Name == target.Name) ? new PipelineInputSource(target.Name) : null;
    }

    private static (JsonNode? Value, IReadOnlyList<PsrFile>? Files) Read(
        BindingSource? source,
        JsonObject pipelineInputs,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> pipelineFiles,
        IReadOnlyDictionary<string, StepResult> completed) => source switch
        {
            PipelineInputSource input => (pipelineInputs[input.Field], pipelineFiles.GetValueOrDefault(input.Field)),
            StepOutputSource output when completed.TryGetValue(output.Step, out var result) =>
                (result.Outputs?[output.Field], result.Files.GetValueOrDefault(output.Field)),
            ConstantSource constant => (constant.Value, null),
            _ => (null, null),
        };

    private static InputError Missing(FieldDefinition target, BindingSource? source) =>
        new(target.Name, "MISSING_REQUIRED_INPUT", source is null
            ? "required input has no binding and no default"
            : $"required input received no value from {source}");
}
