using System.Text.Json.Nodes;

namespace PythonScriptRunner.Core.Catalog;

/// <summary>Maps <see cref="FieldDefinition"/> to and from the manifest/protocol JSON (camelCase).</summary>
internal static class FieldDefinitionJson
{
    public static JsonObject ToJson(FieldDefinition field)
    {
        var type = field.Type;
        var json = new JsonObject
        {
            ["name"] = field.Name,
            ["type"] = type.WireName,
            ["required"] = field.Required,
        };
        if (type.ItemType is { } item)
        {
            json["itemType"] = item.WireName;
        }

        if (type.EnumValues is { } values)
        {
            json["enumValues"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);
        }

        if (field.Default is not null)
        {
            json["default"] = field.Default.DeepClone();
        }

        if (field.Description is not null)
        {
            json["description"] = field.Description;
        }

        if (field.ContentTypes is { } contentTypes)
        {
            json["contentTypes"] = new JsonArray([.. contentTypes.Select(v => (JsonNode)JsonValue.Create(v))]);
        }

        if (field.Columns is { } columns)
        {
            json["columns"] = new JsonArray([.. columns.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["type"] = c.Type.WireName })]);
        }

        return json;
    }

    public static JsonArray ToJson(IEnumerable<FieldDefinition> fields) => new([.. fields.Select(f => (JsonNode)ToJson(f))]);

    /// <exception cref="ArgumentException">The JSON is not a valid field declaration.</exception>
    public static FieldDefinition FromJson(JsonObject json)
    {
        var name = json["name"]?.GetValue<string>() ?? throw new ArgumentException("Field without 'name'.");
        var type = FieldType.Parse(
            json["type"]?.GetValue<string>() ?? throw new ArgumentException($"Field '{name}' without 'type'."),
            json["itemType"]?.GetValue<string>(),
            Strings(json["enumValues"]));
        return new FieldDefinition(name, type)
        {
            Required = json["required"]?.GetValue<bool>() ?? true,
            Default = json["default"]?.DeepClone(),
            Description = json["description"]?.GetValue<string>(),
            ContentTypes = Strings(json["contentTypes"]),
            Columns = json["columns"] is JsonArray columns
                ? [.. columns.Select(c => new TableColumn(c!["name"]!.GetValue<string>(), FieldType.Parse(c["type"]!.GetValue<string>())))]
                : null,
        };
    }

    private static string[]? Strings(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(v => v!.GetValue<string>())] : null;
}
