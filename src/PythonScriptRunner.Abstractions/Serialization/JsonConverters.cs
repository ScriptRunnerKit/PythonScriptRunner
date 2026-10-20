using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PythonScriptRunner;

/// <summary>A strongly typed identifier backed by a GUID.</summary>
/// <typeparam name="TSelf">The identifier type.</typeparam>
internal interface IGuidId<TSelf>
    where TSelf : struct, IGuidId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}

/// <summary>Writes a strongly typed identifier as its GUID text.</summary>
/// <typeparam name="T">The identifier type.</typeparam>
internal sealed class GuidIdJsonConverter<T> : JsonConverter<T>
    where T : struct, IGuidId<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => T.From(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        T.From(Guid.Parse(reader.GetString()!));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value.ToString());
}

/// <summary>Writes a <see cref="FieldDefinition"/> in the manifest format (data mapping §2.1).</summary>
internal sealed class FieldDefinitionJsonConverter : JsonConverter<FieldDefinition>
{
    public override FieldDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("A field declaration must be an object.");
        try
        {
            return FieldDefinitionJson.FromJson(node);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, FieldDefinition value, JsonSerializerOptions options) =>
        FieldDefinitionJson.ToJson(value).WriteTo(writer);
}

/// <summary>
/// Writes a <see cref="BindingSource"/> as <c>{"kind": "pipeline", "field"}</c>, <c>{"kind": "step", "step", "field"}</c> or
/// <c>{"kind": "constant", "value"}</c> (data mapping §4.5).
/// </summary>
internal sealed class BindingSourceJsonConverter : JsonConverter<BindingSource>
{
    public override BindingSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("A binding source must be an object.");
        string Text(string name) => node[name]?.GetValue<string>() ?? throw new JsonException($"The binding source needs '{name}'.");
        return Text("kind") switch
        {
            "pipeline" => new PipelineInputSource(Text("field")),
            "step" => new StepOutputSource(Text("step"), Text("field")),
            "constant" => new ConstantSource(node["value"]?.DeepClone()),
            var kind => throw new JsonException($"Unknown binding kind '{kind}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, BindingSource value, JsonSerializerOptions options)
    {
        JsonObject node = value switch
        {
            PipelineInputSource input => new() { ["kind"] = "pipeline", ["field"] = input.Field },
            StepOutputSource output => new() { ["kind"] = "step", ["step"] = output.Step, ["field"] = output.Field },
            ConstantSource constant => new() { ["kind"] = "constant", ["value"] = constant.Value?.DeepClone() },
            _ => throw new JsonException($"Unknown binding source {value.GetType().Name}."),
        };
        node.WriteTo(writer);
    }
}
