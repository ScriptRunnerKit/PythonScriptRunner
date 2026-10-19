using System.Text.Json;
using System.Text.Json.Nodes;

namespace PythonScriptRunner.EntityFrameworkCore.Catalog;

/// <summary>JSON stored in the <c>jsonb</c> columns (formats of the data mapping §2.1, §4.4, §4.5 and §5.1).</summary>
internal static class StoredJson
{
    public static string Fields(IEnumerable<FieldDefinition> fields) => CatalogManifests.WriteFields(fields).ToJsonString();

    public static IReadOnlyList<FieldDefinition> Fields(string json) => CatalogManifests.ReadFields(JsonNode.Parse(json)!.AsArray());

    public static string Bindings(IEnumerable<InputBinding> bindings) =>
        new JsonArray([.. bindings.Select(b => (JsonNode)new JsonObject { ["input"] = b.Input, ["source"] = Source(b.Source) })]).ToJsonString();

    public static IReadOnlyList<InputBinding> Bindings(string json) =>
        [.. JsonNode.Parse(json)!.AsArray().Select(b => new InputBinding(b!["input"]!.GetValue<string>(), Source(b["source"]!.AsObject())))];

    public static string Outputs(IEnumerable<PipelineOutputDefinition> outputs) =>
        new JsonArray([.. outputs.Select(o =>
        {
            var node = new JsonObject
            {
                ["name"] = o.Name,
                ["source"] = new JsonObject { ["step"] = o.Source.Step, ["field"] = o.Source.Field },
            };
            if (o.Description is not null)
            {
                node["description"] = o.Description;
            }

            return (JsonNode)node;
        })]).ToJsonString();

    public static IReadOnlyList<PipelineOutputDefinition> Outputs(string json) =>
        [.. JsonNode.Parse(json)!.AsArray().Select(o => new PipelineOutputDefinition(
            o!["name"]!.GetValue<string>(),
            new StepOutputSource(o["source"]!["step"]!.GetValue<string>(), o["source"]!["field"]!.GetValue<string>()))
        {
            Description = o["description"]?.GetValue<string>(),
        })];

    public static string? Error(RunError? error)
    {
        if (error is null)
        {
            return null;
        }

        var node = new JsonObject { ["kind"] = error.Kind.ToString(), ["code"] = error.Code, ["message"] = error.Message, ["step"] = error.StepKey };
        if (error.Traceback is not null)
        {
            node["traceback"] = error.Traceback;
        }

        if (error.Details is not null)
        {
            node["details"] = error.Details.DeepClone();
        }

        return node.ToJsonString();
    }

    public static RunError? Error(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var node = JsonNode.Parse(json)!;
        return new RunError(Enum.Parse<ErrorKind>(node["kind"]!.GetValue<string>()), node["message"]?.GetValue<string>() ?? string.Empty)
        {
            Code = node["code"]?.GetValue<string>(),
            StepKey = node["step"]?.GetValue<string>(),
            Traceback = node["traceback"]?.GetValue<string>(),
            Details = node["details"]?.DeepClone(),
        };
    }

    public static string? Node(JsonNode? node) => node?.ToJsonString();

    public static JsonNode? Node(string? json) => json is null ? null : JsonNode.Parse(json);

    public static JsonObject? Object(string? json) => json is null ? null : JsonNode.Parse(json)!.AsObject();

    /// <summary>Whether two JSON texts hold the same value (ignores formatting).</summary>
    public static bool SameJson(string? left, string? right) =>
        left == right || (left is not null && right is not null && JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)));

    private static JsonObject Source(BindingSource source) => source switch
    {
        PipelineInputSource input => new JsonObject { ["kind"] = "pipeline", ["field"] = input.Field },
        StepOutputSource output => new JsonObject { ["kind"] = "step", ["step"] = output.Step, ["field"] = output.Field },
        ConstantSource constant => new JsonObject { ["kind"] = "constant", ["value"] = constant.Value?.DeepClone() },
        _ => throw new JsonException($"Unknown binding source {source.GetType().Name}."),
    };

    private static BindingSource Source(JsonObject json) => json["kind"]!.GetValue<string>() switch
    {
        "pipeline" => new PipelineInputSource(json["field"]!.GetValue<string>()),
        "step" => new StepOutputSource(json["step"]!.GetValue<string>(), json["field"]!.GetValue<string>()),
        "constant" => new ConstantSource(json["value"]?.DeepClone()),
        var kind => throw new JsonException($"Unknown binding kind '{kind}'."),
    };
}
