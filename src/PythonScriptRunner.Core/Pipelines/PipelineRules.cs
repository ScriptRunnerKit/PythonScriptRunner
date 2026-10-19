using PythonScriptRunner.Core.Catalog;
using PythonScriptRunner.Core.Values;

namespace PythonScriptRunner.Core.Pipelines;

/// <summary>Validation rules of a pipeline (spec 003): structure, bindings, types and outputs.</summary>
internal static class PipelineRules
{
    public const int MaxSteps = 50;

    /// <summary>Validates a pipeline given a way to find the stage version of each step.</summary>
    public static ValidationReport Validate(PipelineDefinition pipeline, Func<string, int, StageDefinition?> findStage)
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        ValidateDefinition(pipeline, errors);

        if (pipeline.Steps.Count is 0 or > MaxSteps)
        {
            errors.Add(new("PIPE_NO_STEPS", $"a pipeline needs 1 to {MaxSteps} steps (has {pipeline.Steps.Count})"));
        }

        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < pipeline.Steps.Count; i++)
        {
            var key = pipeline.Steps[i].Key;
            if (key.Length is < 1 or > 64 || !StageDefinitionValidator.KeyPattern().IsMatch(key))
            {
                errors.Add(new("PIPE_STEP_KEY", "step key must be a slug (a-z, 0-9, '-')") { Step = key });
            }
            else if (!positions.TryAdd(key, i))
            {
                errors.Add(new("PIPE_STEP_KEY", "step key is duplicated") { Step = key });
            }
        }

        var stages = pipeline.Steps.Select(s => findStage(s.StageKey, s.StageVersion)).ToArray();
        for (var i = 0; i < pipeline.Steps.Count; i++)
        {
            if (stages[i] is null)
            {
                var step = pipeline.Steps[i];
                errors.Add(new("PIPE_STAGE_NOT_PUBLISHED", $"stage '{step.StageKey}@{step.StageVersion}' is not published in the catalog") { Step = step.Key });
            }
        }

        var inputs = pipeline.Inputs.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var usedInputs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < pipeline.Steps.Count; i++)
        {
            if (stages[i] is { } stage)
            {
                ValidateStep(pipeline, i, stage, stages, positions, inputs, usedInputs, errors);
            }
        }

        ValidateOutputs(pipeline, stages, positions, errors);

        foreach (var input in pipeline.Inputs)
        {
            if (!usedInputs.Contains(input.Name))
            {
                warnings.Add(new("PIPE_UNUSED_INPUT", "pipeline input is not used by any step") { Field = input.Name });
            }

            if (input.Type.IsFile && input.Required)
            {
                warnings.Add(new("PIPE_FILE_INPUT", "pipelines with a required file input cannot be scheduled or started by the public queue command") { Field = input.Name });
            }
        }

        return new ValidationReport(errors, warnings);
    }

    private static void ValidateDefinition(PipelineDefinition pipeline, List<ValidationIssue> errors)
    {
        var problems = new List<string>();
        if (pipeline.Key.Length is < 3 or > 64 || !StageDefinitionValidator.KeyPattern().IsMatch(pipeline.Key))
        {
            problems.Add("key must be a slug of 3-64 chars (a-z, 0-9, '-')");
        }

        if (pipeline.Version < 1)
        {
            problems.Add("version must be >= 1");
        }

        if (string.IsNullOrWhiteSpace(pipeline.Name) || pipeline.Name.Length > 200)
        {
            problems.Add("name must have 1-200 chars");
        }

        StageDefinitionValidator.ValidateFields(pipeline.Inputs, "inputs", problems);
        errors.AddRange(problems.Select(p => new ValidationIssue("PIPE_DEFINITION", p)));
    }

    private static void ValidateStep(
        PipelineDefinition pipeline,
        int index,
        StageDefinition stage,
        StageDefinition?[] stages,
        Dictionary<string, int> positions,
        Dictionary<string, FieldDefinition> inputs,
        HashSet<string> usedInputs,
        List<ValidationIssue> errors)
    {
        var step = pipeline.Steps[index];
        var targets = stage.Inputs.ToDictionary(f => f.Name, StringComparer.Ordinal);
        foreach (var binding in step.Bindings)
        {
            if (!targets.ContainsKey(binding.Input))
            {
                errors.Add(new("PIPE_UNKNOWN_INPUT", $"stage '{stage.Key}@{stage.Version}' has no input '{binding.Input}'") { Step = step.Key, Field = binding.Input });
            }
        }

        foreach (var duplicate in step.Bindings.GroupBy(b => b.Input).Where(g => g.Count() > 1))
        {
            errors.Add(new("PIPE_DEFINITION", "input is bound more than once") { Step = step.Key, Field = duplicate.Key });
        }

        foreach (var target in stage.Inputs)
        {
            var binding = step.Bindings.FirstOrDefault(b => b.Input == target.Name);
            var issue = binding is null
                ? CheckImplicit(pipeline, index, target, stages, inputs, usedInputs)
                : CheckExplicit(binding.Source, index, target, stages, positions, inputs, usedInputs);
            if (issue is not null)
            {
                errors.Add(issue with { Step = step.Key, Field = target.Name });
            }
        }
    }

    private static ValidationIssue? CheckExplicit(
        BindingSource source,
        int index,
        FieldDefinition target,
        StageDefinition?[] stages,
        Dictionary<string, int> positions,
        Dictionary<string, FieldDefinition> inputs,
        HashSet<string> usedInputs)
    {
        switch (source)
        {
            case PipelineInputSource pipelineInput:
                if (!inputs.TryGetValue(pipelineInput.Field, out var field))
                {
                    return new("PIPE_UNKNOWN_SOURCE", $"pipeline has no input '{pipelineInput.Field}'");
                }

                usedInputs.Add(field.Name);
                return CheckTypes(field, target, pipelineInput.ToString(), MayBeAbsent(field));
            case StepOutputSource stepOutput:
                if (!positions.TryGetValue(stepOutput.Step, out var position))
                {
                    return new("PIPE_UNKNOWN_SOURCE", $"there is no step '{stepOutput.Step}'");
                }

                if (position >= index)
                {
                    return new("PIPE_FORWARD_REF", $"'{stepOutput}' is not an earlier step; pipelines are linear");
                }

                if (stages[position] is not { } sourceStage)
                {
                    return null;
                }

                var output = sourceStage.Outputs.FirstOrDefault(o => o.Name == stepOutput.Field);
                return output is null
                    ? new("PIPE_UNKNOWN_SOURCE", $"step '{stepOutput.Step}' has no output '{stepOutput.Field}'")
                    : CheckTypes(output, target, stepOutput.ToString(), !output.Required);
            case ConstantSource constant:
                if (target.Type.IsFile)
                {
                    return new("PIPE_CONST_INVALID", "file inputs cannot receive constants");
                }

                if (constant.Value is null)
                {
                    return target.Required && target.Default is null
                        ? new("PIPE_UNBOUND_REQUIRED", "required input is bound to a null constant")
                        : null;
                }

                var wireErrors = new List<WireError>();
                WireValidator.Validate(Coercion.Coerce(constant.Value, target.Type), target, target.Name, wireErrors);
                return wireErrors.Count == 0
                    ? null
                    : new("PIPE_CONST_INVALID", string.Join("; ", wireErrors.Select(e => e.Message)));
            default:
                return new("PIPE_DEFINITION", $"unsupported binding source {source.GetType().Name}");
        }
    }

    private static ValidationIssue? CheckImplicit(
        PipelineDefinition pipeline,
        int index,
        FieldDefinition target,
        StageDefinition?[] stages,
        Dictionary<string, FieldDefinition> inputs,
        HashSet<string> usedInputs)
    {
        FieldDefinition? source;
        bool optional;
        string description;
        if (index == 0)
        {
            source = inputs.GetValueOrDefault(target.Name);
            optional = source is not null && MayBeAbsent(source);
            description = $"$pipeline.{target.Name} (implicit)";
            if (source is not null)
            {
                usedInputs.Add(source.Name);
            }
        }
        else
        {
            if (stages[index - 1] is not { } previous)
            {
                return null;
            }

            source = previous.Outputs.FirstOrDefault(o => o.Name == target.Name);
            optional = source is { Required: false };
            description = $"$steps.{pipeline.Steps[index - 1].Key}.{target.Name} (implicit)";
        }

        if (source is not null)
        {
            return CheckTypes(source, target, description, optional);
        }

        return target.Required && target.Default is null
            ? new("PIPE_UNBOUND_REQUIRED", "required input has no binding, no implicit source with the same name and no default")
            : null;
    }

    private static ValidationIssue? CheckTypes(FieldDefinition source, FieldDefinition target, string description, bool sourceMayBeAbsent)
    {
        if (!TypeCompatibility.IsAssignable(source, target))
        {
            return new("PIPE_TYPE_MISMATCH", $"{description} is {source.Type} and cannot feed {target.Type}");
        }

        return sourceMayBeAbsent && target.Required && target.Default is null
            ? new("PIPE_OPTIONAL_TO_REQUIRED", $"{description} is optional but the input is required and has no default")
            : null;
    }

    private static bool MayBeAbsent(FieldDefinition pipelineInput) => !pipelineInput.Required && pipelineInput.Default is null;

    private static void ValidateOutputs(
        PipelineDefinition pipeline, StageDefinition?[] stages, Dictionary<string, int> positions, List<ValidationIssue> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in pipeline.Outputs)
        {
            if (!StageDefinitionValidator.FieldNamePattern().IsMatch(output.Name) || !names.Add(output.Name))
            {
                errors.Add(new("PIPE_OUTPUT_SOURCE", "output name must match ^[a-z_][a-z0-9_]{0,63}$ and be unique") { Field = output.Name });
                continue;
            }

            if (!positions.TryGetValue(output.Source.Step, out var position))
            {
                errors.Add(new("PIPE_OUTPUT_SOURCE", $"there is no step '{output.Source.Step}'") { Field = output.Name });
            }
            else if (stages[position] is { } stage && stage.Outputs.All(o => o.Name != output.Source.Field))
            {
                errors.Add(new("PIPE_OUTPUT_SOURCE", $"step '{output.Source.Step}' has no output '{output.Source.Field}'") { Field = output.Name, Step = output.Source.Step });
            }
        }
    }
}
