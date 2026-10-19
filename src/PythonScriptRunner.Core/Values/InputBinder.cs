using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PythonScriptRunner.Core.Values;

/// <summary>An input file of a run (field, position in a list, file).</summary>
internal sealed record BoundFile(string Field, int Index, PsrFile File);

/// <summary>Validated inputs: wire values plus the files to materialize.</summary>
/// <param name="Wire">Inputs in wire format; files carry metadata only (name, content type).</param>
/// <param name="Files">Input files.</param>
internal sealed record BoundInputs(JsonObject Wire, IReadOnlyList<BoundFile> Files);

/// <summary>Converts the caller's inputs (.NET values) to the wire format and validates the contract.</summary>
internal static class InputBinder
{
    public static BoundInputs Bind(IReadOnlyList<FieldDefinition> fields, object? inputs, long maxFileBytes)
    {
        var raw = ReadMembers(inputs);
        var errors = new List<InputError>();
        var declared = fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        errors.AddRange(raw.Keys
            .Where(name => !declared.Contains(name))
            .Order(StringComparer.Ordinal)
            .Select(name => new InputError(name, "UNDECLARED_INPUT", "input is not declared by the stage")));

        var wire = new JsonObject();
        var files = new List<BoundFile>();
        foreach (var field in fields)
        {
            raw.TryGetValue(field.Name, out var value);
            if (value is null)
            {
                if (field.Default is not null)
                {
                    wire[field.Name] = field.Default.DeepClone();
                }
                else if (field.Required)
                {
                    errors.Add(new(field.Name, "MISSING_REQUIRED_INPUT", "required input is missing"));
                }

                continue;
            }

            if (field.Type.IsFile)
            {
                var node = BindFiles(field, value, maxFileBytes, files, errors);
                if (node is not null)
                {
                    wire[field.Name] = node;
                }

                continue;
            }

            var converted = ToWire(value, field.Type, field.Name, errors);
            if (converted is null)
            {
                continue;
            }

            var wireErrors = new List<WireError>();
            WireValidator.Validate(converted, field, field.Name, wireErrors);
            errors.AddRange(wireErrors.Select(e => new InputError(e.Path, e.Code, e.Message)));
            wire[field.Name] = converted;
        }

        return errors.Count > 0 ? throw new InputValidationException(errors) : new BoundInputs(wire, files);
    }

    private static JsonNode? BindFiles(FieldDefinition field, object value, long maxFileBytes, List<BoundFile> files, List<InputError> errors)
    {
        var isList = field.Type.Kind == FieldKind.List;
        PsrFile[]? given = value switch
        {
            PsrFile single when !isList => [single],
            IEnumerable<PsrFile> many when isList => [.. many],
            _ => null,
        };
        if (given is null)
        {
            errors.Add(new(field.Name, "TYPE_MISMATCH", isList ? "expected a list of PsrFile" : "expected a PsrFile"));
            return null;
        }

        var references = new JsonArray();
        for (var i = 0; i < given.Length; i++)
        {
            var file = given[i];
            var path = isList ? $"{field.Name}[{i}]" : field.Name;
            if (field.ContentTypes is { Count: > 0 } allowed && !allowed.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new(path, "CONTENT_TYPE_NOT_ALLOWED", $"content type '{file.ContentType}' is not one of: {string.Join(", ", allowed)}"));
            }

            if (file.Length > maxFileBytes)
            {
                errors.Add(new(path, "FILE_TOO_LARGE", $"file has {file.Length} bytes; the limit is {maxFileBytes}"));
            }

            files.Add(new BoundFile(field.Name, i, file));
            references.Add(new JsonObject
            {
                [WireValidator.FileMarker] = new JsonObject { ["name"] = file.FileName, ["contentType"] = file.ContentType },
            });
        }

        return isList ? references : references[0]!.DeepClone();
    }

    private static JsonNode? ToWire(object value, FieldType type, string path, List<InputError> errors)
    {
        switch (value)
        {
            case JsonNode node:
                return node.DeepClone();
            case JsonElement element:
                return JsonSerializer.SerializeToNode(element);
        }

        switch (type.Kind)
        {
            case FieldKind.Number:
                double? number = value switch
                {
                    double d => d,
                    float f => f,
                    decimal m => (double)m,
                    int or long or short or byte or sbyte or uint or ushort or ulong => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                    _ => null,
                };
                return number is { } n ? JsonValue.Create(n) : Mismatch(path, "number", value, errors);
            case FieldKind.Integer:
                long? integer = value switch
                {
                    int or long or short or byte or sbyte or uint or ushort => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                    ulong u when u <= long.MaxValue => (long)u,
                    _ => null,
                };
                return integer is { } i ? JsonValue.Create(i) : Mismatch(path, "integer", value, errors);
            case FieldKind.Decimal:
                return value switch
                {
                    decimal m => JsonValue.Create(m.ToString(CultureInfo.InvariantCulture)),
                    int or long or short or byte or sbyte or uint or ushort or ulong => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
                    string s => JsonValue.Create(s.Trim()),
                    _ => Mismatch(path, "decimal (decimal, integer or numeric string; not floating point)", value, errors),
                };
            case FieldKind.String:
                return value is string or char ? JsonValue.Create(value.ToString()) : Mismatch(path, "string", value, errors);
            case FieldKind.Bool:
                return value is bool b ? JsonValue.Create(b) : Mismatch(path, "bool", value, errors);
            case FieldKind.Date:
                return value switch
                {
                    DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    string s => JsonValue.Create(s),
                    _ => Mismatch(path, "DateOnly", value, errors),
                };
            case FieldKind.DateTime:
                return value switch
                {
                    DateTimeOffset dto => JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture)),
                    DateTime { Kind: DateTimeKind.Unspecified } => AddError(path, "NAIVE_DATETIME", "DateTime with Kind=Unspecified is ambiguous; use DateTimeOffset", errors),
                    DateTime dt => JsonValue.Create(new DateTimeOffset(dt).ToString("O", CultureInfo.InvariantCulture)),
                    string s => JsonValue.Create(s),
                    _ => Mismatch(path, "DateTimeOffset", value, errors),
                };
            case FieldKind.Enum:
                return value switch
                {
                    string s => JsonValue.Create(s),
                    Enum e => JsonValue.Create(JsonNamingPolicy.SnakeCaseLower.ConvertName(e.ToString())),
                    _ => Mismatch(path, "string", value, errors),
                };
            case FieldKind.Json:
            case FieldKind.Table:
                if (type.Kind == FieldKind.Table && value is string or not IEnumerable)
                {
                    return Mismatch(path, "a sequence of records", value, errors);
                }

                return JsonSerializer.SerializeToNode(value, value.GetType(), PsrJson.Options);
            case FieldKind.List:
                if (value is string or not IEnumerable)
                {
                    return Mismatch(path, "a sequence", value, errors);
                }

                var array = new JsonArray();
                var index = 0;
                foreach (var item in (IEnumerable)value)
                {
                    var itemPath = $"{path}[{index++}]";
                    array.Add(item is null ? AddError(itemPath, "NULL_VALUE", "null items are not allowed", errors) : ToWire(item, type.ItemType!, itemPath, errors));
                }

                return array;
            default:
                return Mismatch(path, type.ToString(), value, errors);
        }
    }

    private static JsonNode? Mismatch(string path, string expected, object value, List<InputError> errors) =>
        AddError(path, "TYPE_MISMATCH", $"expected {expected}, got {value.GetType().Name}", errors);

    private static JsonNode? AddError(string path, string code, string message, List<InputError> errors)
    {
        errors.Add(new InputError(path, code, message));
        return null;
    }

    /// <summary>Reads input members from an anonymous object, record, dictionary or JSON object.</summary>
    internal static Dictionary<string, object?> ReadMembers(object? inputs)
    {
        switch (inputs)
        {
            case null:
                return new(StringComparer.Ordinal);
            case JsonObject json:
                return json.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                return element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.Ordinal);
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                return pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            case IDictionary dictionary:
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!] = entry.Value;
                }

                return result;
            default:
                return inputs.GetType()
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    .ToDictionary(
                        p => p.Name.Any(char.IsUpper) ? JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name) : p.Name,
                        p => p.GetValue(inputs),
                        StringComparer.Ordinal);
        }
    }
}
