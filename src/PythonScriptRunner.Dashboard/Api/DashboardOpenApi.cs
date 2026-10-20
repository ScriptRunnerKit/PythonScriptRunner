using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PythonScriptRunner.Dashboard.Api;

/// <summary>
/// The internal OpenAPI document of the dashboard (D-26): only its endpoints, and the JSON shapes written by custom converters
/// (identifiers, field declarations, binding sources) described, so the panel types generated from the snapshot are exact.
/// </summary>
internal static class DashboardOpenApi
{
    /// <summary>Document name (kept out of the host documents).</summary>
    public const string Document = "psr-dashboard";

    private static readonly string[] FieldTypes = ["number", "integer", "decimal", "string", "bool", "date", "datetime", "enum", "json", "table", "file", "list"];

    public static void Configure(OpenApiOptions options)
    {
        options.ShouldInclude = description => description.GroupName == Document;
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "PythonScriptRunner Dashboard (internal API)";
            document.Info.Version = "v1";
            return Task.CompletedTask;
        });
        options.AddSchemaTransformer((schema, context, _) =>
        {
            var type = context.JsonTypeInfo.Type;
            if (IsIdentifier(type))
            {
                schema.Type = JsonSchemaType.String;
                schema.Format = "uuid";
            }
            else if (type == typeof(FieldDefinition))
            {
                DescribeField(schema);
            }
            else if (ElementType(type) == typeof(FieldDefinition))
            {
                // Lists of declarations (inputs, outputs) get no item schema from the generator: the converter hides the shape.
                var item = new OpenApiSchema();
                DescribeField(item);
                schema.Items = item;
            }
            else if (type == typeof(BindingSource))
            {
                DescribeBindingSource(schema);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Descriptions come from XML comments and keep the line endings of the checkout (CRLF on Windows); the served document uses
    /// LF everywhere, so it is the same on every OS (its snapshot feeds the panel types).
    /// </summary>
    public static string NormalizeLineEndings(string json)
    {
        var document = JsonNode.Parse(json);
        Normalize(document);
        return document?.ToJsonString() ?? json;

        static JsonNode? Fix(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) && text.Contains('\r', StringComparison.Ordinal)
                ? JsonValue.Create(text.ReplaceLineEndings("\n"))
                : null;

        static void Normalize(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (name, child) in obj.ToList())
                    {
                        if (Fix(child) is { } fixedValue)
                        {
                            obj[name] = fixedValue;
                        }
                        else
                        {
                            Normalize(child);
                        }
                    }

                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (Fix(array[i]) is { } fixedValue)
                        {
                            array[i] = fixedValue;
                        }
                        else
                        {
                            Normalize(array[i]);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>Strongly typed identifiers (<c>RunId</c>, <c>ArtifactId</c>...) are written as their GUID text.</summary>
    private static bool IsIdentifier(Type type) =>
        type is { IsValueType: true, Namespace: "PythonScriptRunner" } && type.Name.EndsWith("Id", StringComparison.Ordinal)
        && type.GetProperty("Value")?.PropertyType == typeof(Guid);

    private static Type? ElementType(Type type) =>
        type.IsArray ? type.GetElementType()
        : type.IsGenericType && type.GetGenericArguments() is [var item] && typeof(IEnumerable<>).MakeGenericType(item).IsAssignableFrom(type) ? item
        : null;

    /// <summary>Field declaration in the manifest format (data mapping §2.1).</summary>
    private static void DescribeField(OpenApiSchema schema)
    {
        schema.Type = JsonSchemaType.Object;
        schema.Required = new HashSet<string> { "name", "type" };
        schema.Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["name"] = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[a-z_][a-z0-9_]{0,63}$" },
            ["type"] = Names(FieldTypes),
            ["itemType"] = Names(FieldTypes[..^1]),
            ["required"] = new OpenApiSchema { Type = JsonSchemaType.Boolean, Default = JsonValue.Create(true) },
            ["default"] = new OpenApiSchema { Description = "Default value in wire format (not for files and tables)." },
            ["description"] = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 500 },
            ["enumValues"] = Strings(),
            ["contentTypes"] = Strings(),
            ["columns"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Required = new HashSet<string> { "name", "type" },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["name"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["type"] = Names(FieldTypes[..7]),
                    },
                },
            },
        };
    }

    /// <summary><c>{"kind": "pipeline", "field"}</c>, <c>{"kind": "step", "step", "field"}</c> or <c>{"kind": "constant", "value"}</c>.</summary>
    private static void DescribeBindingSource(OpenApiSchema schema)
    {
        static OpenApiSchema Kind(string kind, params string[] fields)
        {
            var properties = new Dictionary<string, IOpenApiSchema> { ["kind"] = Names([kind]) };
            foreach (var field in fields)
            {
                properties[field] = field == "value" ? new OpenApiSchema() : new OpenApiSchema { Type = JsonSchemaType.String };
            }

            return new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Required = new HashSet<string>(["kind", .. fields.Where(f => f != "value")]),
                Properties = properties,
            };
        }

        schema.OneOf = [Kind("pipeline", "field"), Kind("step", "step", "field"), Kind("constant", "value")];
    }

    private static OpenApiSchema Names(IEnumerable<string> names) =>
        new() { Type = JsonSchemaType.String, Enum = [.. names.Select(n => (JsonNode)JsonValue.Create(n))] };

    private static OpenApiSchema Strings() => new() { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } };
}
