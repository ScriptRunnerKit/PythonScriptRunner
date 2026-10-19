using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PythonScriptRunner.Core.Values;

/// <summary>One contract violation of a wire value.</summary>
internal readonly record struct WireError(string Path, string Code, string Message);

/// <summary>Validates wire values (JSON) against declared field types.</summary>
internal static partial class WireValidator
{
    internal const string FileMarker = "$file";

    [GeneratedRegex(@"(Z|z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex OffsetSuffix();

    public static void Validate(JsonNode? value, FieldDefinition field, string path, List<WireError> errors) =>
        Validate(value, field.Type, field, path, errors);

    private static void Validate(JsonNode? value, FieldType type, FieldDefinition field, string path, List<WireError> errors)
    {
        if (value is null)
        {
            errors.Add(new(path, "NULL_VALUE", "null is not allowed here"));
            return;
        }

        switch (type.Kind)
        {
            case FieldKind.Number:
                if (!TryGetDouble(value, out var number))
                {
                    errors.Add(Mismatch(path, "number", value));
                }
                else if (!double.IsFinite(number))
                {
                    errors.Add(new(path, "NON_FINITE_NUMBER", "number must be finite"));
                }

                break;
            case FieldKind.Integer:
                if (!TryGetInteger(value, out _))
                {
                    errors.Add(Mismatch(path, "integer", value));
                }

                break;
            case FieldKind.Decimal:
                if (!TryGetString(value, out var text)
                    || !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    errors.Add(new(path, "INVALID_DECIMAL", "expected a decimal number as a string (e.g. \"12.50\")"));
                }

                break;
            case FieldKind.String:
                if (!TryGetString(value, out _))
                {
                    errors.Add(Mismatch(path, "string", value));
                }

                break;
            case FieldKind.Bool:
                if (value.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add(Mismatch(path, "bool", value));
                }

                break;
            case FieldKind.Date:
                if (!TryGetString(value, out var date)
                    || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    errors.Add(new(path, "INVALID_DATE", "expected an ISO 8601 date (yyyy-MM-dd)"));
                }

                break;
            case FieldKind.DateTime:
                ValidateDateTime(value, path, errors);
                break;
            case FieldKind.Enum:
                if (!TryGetString(value, out var option) || !type.EnumValues!.Contains(option, StringComparer.Ordinal))
                {
                    errors.Add(new(path, "ENUM_VALUE_INVALID", $"expected one of: {string.Join(", ", type.EnumValues!)}"));
                }

                break;
            case FieldKind.Json:
                break;
            case FieldKind.Table:
                ValidateTable(value, field, path, errors);
                break;
            case FieldKind.File:
                ValidateFile(value, field, path, errors);
                break;
            case FieldKind.List:
                if (value is not JsonArray array)
                {
                    errors.Add(Mismatch(path, "list", value));
                    break;
                }

                for (var i = 0; i < array.Count; i++)
                {
                    Validate(array[i], type.ItemType!, field, $"{path}[{i}]", errors);
                }

                break;
        }
    }

    private static void ValidateDateTime(JsonNode value, string path, List<WireError> errors)
    {
        if (!TryGetString(value, out var text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add(new(path, "INVALID_DATETIME", "expected an ISO 8601 date-time"));
        }
        else if (!OffsetSuffix().IsMatch(text))
        {
            errors.Add(new(path, "NAIVE_DATETIME", "date-time must include a UTC offset (e.g. -03:00 or Z)"));
        }
    }

    private static void ValidateTable(JsonNode value, FieldDefinition field, string path, List<WireError> errors)
    {
        if (value is not JsonArray rows || rows.Any(row => row is not JsonObject))
        {
            errors.Add(new(path, "TYPE_MISMATCH", "expected a list of objects (records)"));
            return;
        }

        if (field.Columns is not { Count: > 0 } columns)
        {
            return;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]!.AsObject();
            foreach (var column in columns)
            {
                var cellPath = $"{path}[{i}].{column.Name}";
                if (!row.TryGetPropertyValue(column.Name, out var cell))
                {
                    errors.Add(new(cellPath, "MISSING_COLUMN", "declared column is missing"));
                }
                else if (cell is not null)
                {
                    Validate(cell, column.Type, field, cellPath, errors);
                }
            }
        }
    }

    private static void ValidateFile(JsonNode value, FieldDefinition field, string path, List<WireError> errors)
    {
        if (value is not JsonObject reference
            || reference[FileMarker] is not JsonObject info
            || !TryGetString(info["path"] ?? info["name"], out _))
        {
            errors.Add(new(path, "TYPE_MISMATCH", "expected a file reference"));
            return;
        }

        if (field.ContentTypes is { Count: > 0 } allowed
            && TryGetString(info["contentType"], out var contentType)
            && !allowed.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new(path, "CONTENT_TYPE_NOT_ALLOWED", $"content type '{contentType}' is not one of: {string.Join(", ", allowed)}"));
        }
    }

    internal static bool TryGetString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String && jsonValue.TryGetValue(out string? text))
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    // JsonValue built from .NET primitives (e.g. JsonValue.Create(1)) only converts to its own type,
    // so fall back to the JSON text, which is always invariant.
    private static bool TryGetDouble(JsonNode node, out double value)
    {
        value = 0;
        return node is JsonValue jsonValue
            && jsonValue.GetValueKind() == JsonValueKind.Number
            && (jsonValue.TryGetValue(out value)
                || double.TryParse(jsonValue.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value));
    }

    private static bool TryGetInteger(JsonNode node, out long value)
    {
        value = 0;
        return node is JsonValue jsonValue
            && jsonValue.GetValueKind() == JsonValueKind.Number
            && (jsonValue.TryGetValue(out value)
                || long.TryParse(jsonValue.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value));
    }

    private static WireError Mismatch(string path, string expected, JsonNode value) =>
        new(path, "TYPE_MISMATCH", $"expected {expected}, got {value.GetValueKind().ToString().ToLowerInvariant()}");
}
