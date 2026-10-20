using System.Text.Json.Nodes;

namespace PythonScriptRunner.Core.Pipelines;

/// <summary>Maps <c>*.pipeline.json</c> (compact binding syntax) to <see cref="PipelineDefinition"/>.</summary>
internal static class PipelineJson
{
    private const string PipelinePrefix = "$pipeline.";
    private const string StepsPrefix = "$steps.";

    /// <exception cref="ArgumentException">A binding or a stage reference is malformed.</exception>
    public static PipelineDefinition FromJson(JsonObject json) => new()
    {
        Key = json["key"]!.GetValue<string>(),
        Version = json["version"]!.GetValue<int>(),
        Name = json["name"]!.GetValue<string>(),
        Description = json["description"]?.GetValue<string>(),
        Inputs = json["inputs"] is JsonArray inputs ? [.. inputs.Select(f => FieldDefinitionJson.FromJson(f!.AsObject()))] : [],
        Steps = [.. json["steps"]!.AsArray().Select(s => ParseStep(s!.AsObject()))],
        Outputs = json["outputs"] is JsonArray outputs
            ? [.. outputs.Select(o => new PipelineOutputDefinition(o!["name"]!.GetValue<string>(), ParseStepSource(o["source"]!.GetValue<string>()))
            {
                Description = o["description"]?.GetValue<string>(),
            })]
            : [],
    };

    private static PipelineStepDefinition ParseStep(JsonObject step)
    {
        var (stageKey, stageVersion) = PipelineBuilder.ParseStageReference(step["stage"]!.GetValue<string>());
        var bindings = step["bindings"] is JsonObject map
            ? map.Select(pair => new InputBinding(pair.Key, ParseSource(pair.Value))).ToList()
            : [];
        return new PipelineStepDefinition(step["key"]!.GetValue<string>(), stageKey, stageVersion)
        {
            DisplayName = step["displayName"]?.GetValue<string>(),
            Bindings = bindings,
        };
    }

    private static BindingSource ParseSource(JsonNode? node)
    {
        if (node is JsonObject constant && constant.TryGetPropertyValue("const", out var value))
        {
            return new ConstantSource(value?.DeepClone());
        }

        var text = node?.GetValue<string>() ?? throw new ArgumentException("Binding must be a string or {\"const\": value}.");
        if (text.StartsWith(PipelinePrefix, StringComparison.Ordinal))
        {
            return new PipelineInputSource(text[PipelinePrefix.Length..]);
        }

        return ParseStepSource(text);
    }

    private static StepOutputSource ParseStepSource(string text)
    {
        var parts = text.StartsWith(StepsPrefix, StringComparison.Ordinal) ? text[StepsPrefix.Length..].Split('.') : [];
        return parts.Length == 2
            ? new StepOutputSource(parts[0], parts[1])
            : throw new ArgumentException($"Invalid binding '{text}'; use $pipeline.<field>, $steps.<step>.<field> or {{\"const\": value}}.");
    }
}
