using System.Text.Json;
using System.Text.Json.Nodes;

namespace PythonScriptRunner.Core.Pipelines;

/// <summary>Applies the safe widenings allowed by bindings (e.g. <c>integer → decimal</c> becomes a decimal string).</summary>
internal static class Coercion
{
    public static JsonNode Coerce(JsonNode value, FieldType target)
    {
        if (target.Kind == FieldKind.Decimal && value is JsonValue number && number.GetValueKind() == JsonValueKind.Number)
        {
            return JsonValue.Create(number.ToJsonString());
        }

        if (target.Kind == FieldKind.List && value is JsonArray items)
        {
            return new JsonArray([.. items.Select(item => item is null ? null : Coerce(item, target.ItemType!))]);
        }

        return value.DeepClone();
    }
}
