namespace PythonScriptRunner;

/// <summary>The kind of value a field carries.</summary>
public enum FieldKind
{
    /// <summary>64-bit floating point number.</summary>
    Number,
    /// <summary>64-bit integer.</summary>
    Integer,
    /// <summary>Exact decimal number (sent as a JSON string).</summary>
    Decimal,
    /// <summary>UTF-8 text.</summary>
    String,
    /// <summary>Boolean.</summary>
    Bool,
    /// <summary>Calendar date (ISO 8601).</summary>
    Date,
    /// <summary>Date and time with a UTC offset (ISO 8601).</summary>
    DateTime,
    /// <summary>One value of a closed list of strings.</summary>
    Enum,
    /// <summary>Any JSON value.</summary>
    Json,
    /// <summary>A list of records (JSON objects).</summary>
    Table,
    /// <summary>A file of any format.</summary>
    File,
    /// <summary>A list of values of one item type.</summary>
    List,
}

/// <summary>Type of a stage input or output (one of the 12 supported types).</summary>
public sealed class FieldType : IEquatable<FieldType>
{
    private FieldType(FieldKind kind, FieldType? itemType = null, IReadOnlyList<string>? enumValues = null)
    {
        Kind = kind;
        ItemType = itemType;
        EnumValues = enumValues;
    }

    /// <summary>The kind of value.</summary>
    public FieldKind Kind { get; }

    /// <summary>Item type of a <see cref="FieldKind.List"/>; <c>null</c> otherwise.</summary>
    public FieldType? ItemType { get; }

    /// <summary>Allowed values of an <see cref="FieldKind.Enum"/>; <c>null</c> otherwise.</summary>
    public IReadOnlyList<string>? EnumValues { get; }

    /// <summary><c>number</c>: 64-bit floating point.</summary>
    public static FieldType Number { get; } = new(FieldKind.Number);

    /// <summary><c>integer</c>: 64-bit integer.</summary>
    public static FieldType Integer { get; } = new(FieldKind.Integer);

    /// <summary><c>decimal</c>: exact decimal number.</summary>
    public static FieldType Decimal { get; } = new(FieldKind.Decimal);

    /// <summary><c>string</c>: text.</summary>
    public static FieldType String { get; } = new(FieldKind.String);

    /// <summary><c>bool</c>: boolean.</summary>
    public static FieldType Bool { get; } = new(FieldKind.Bool);

    /// <summary><c>date</c>: calendar date.</summary>
    public static FieldType Date { get; } = new(FieldKind.Date);

    /// <summary><c>datetime</c>: date and time with offset.</summary>
    public static FieldType DateTime { get; } = new(FieldKind.DateTime);

    /// <summary><c>json</c>: any JSON value.</summary>
    public static FieldType Json { get; } = new(FieldKind.Json);

    /// <summary><c>table</c>: list of records.</summary>
    public static FieldType Table { get; } = new(FieldKind.Table);

    /// <summary><c>file</c>: a file of any format.</summary>
    public static FieldType File { get; } = new(FieldKind.File);

    /// <summary><c>enum</c> with the given allowed values.</summary>
    /// <param name="values">1 to 200 unique, non-empty values.</param>
    public static FieldType Enum(params IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string[] list = [.. values];
        if (list.Length is 0 or > 200 || list.Distinct(StringComparer.Ordinal).Count() != list.Length
            || list.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("An enum needs 1 to 200 unique, non-empty values.", nameof(values));
        }

        return new FieldType(FieldKind.Enum, enumValues: list);
    }

    /// <summary><c>list&lt;T&gt;</c>, where T is a scalar type, an enum or a file.</summary>
    /// <param name="itemType">Type of each item.</param>
    public static FieldType ListOf(FieldType itemType)
    {
        ArgumentNullException.ThrowIfNull(itemType);
        if (!itemType.IsScalar && itemType.Kind is not (FieldKind.Enum or FieldKind.File))
        {
            throw new ArgumentException(
                $"List items must be a scalar type, an enum or a file; '{itemType}' is not allowed.", nameof(itemType));
        }

        return new FieldType(FieldKind.List, itemType, itemType.EnumValues);
    }

    /// <summary>True for number, integer, decimal, string, bool, date and datetime.</summary>
    public bool IsScalar => Kind <= FieldKind.DateTime;

    /// <summary>True for <c>file</c> and <c>list&lt;file&gt;</c>.</summary>
    public bool IsFile => Kind == FieldKind.File || (Kind == FieldKind.List && ItemType!.Kind == FieldKind.File);

    /// <summary>Name used in JSON manifests and in the protocol (e.g. <c>datetime</c>).</summary>
    public string WireName => WireNameOf(Kind);

    /// <summary>Wire name of a kind (e.g. <c>datetime</c>).</summary>
    /// <param name="kind">The kind.</param>
    public static string WireNameOf(FieldKind kind) => kind switch
    {
        FieldKind.DateTime => "datetime",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Parses the wire representation (<c>type</c>, <c>itemType</c>, <c>enumValues</c>).</summary>
    /// <param name="type">Wire type name.</param>
    /// <param name="itemType">Item type name for lists.</param>
    /// <param name="enumValues">Allowed values for enums.</param>
    /// <exception cref="ArgumentException">The combination is invalid.</exception>
    public static FieldType Parse(string type, string? itemType = null, IReadOnlyList<string>? enumValues = null)
    {
        var kind = ParseKind(type);
        if (kind == FieldKind.List)
        {
            if (itemType is null)
            {
                throw new ArgumentException("List fields require an item type.", nameof(itemType));
            }

            return ListOf(Parse(itemType, null, enumValues));
        }

        if (itemType is not null)
        {
            throw new ArgumentException("Only list fields have an item type.", nameof(itemType));
        }

        if (kind == FieldKind.Enum)
        {
            return Enum(enumValues ?? throw new ArgumentException("Enum fields require values.", nameof(enumValues)));
        }

        if (enumValues is not null)
        {
            throw new ArgumentException("Only enum fields have values.", nameof(enumValues));
        }

        return kind switch
        {
            FieldKind.Number => Number,
            FieldKind.Integer => Integer,
            FieldKind.Decimal => Decimal,
            FieldKind.String => String,
            FieldKind.Bool => Bool,
            FieldKind.Date => Date,
            FieldKind.DateTime => DateTime,
            FieldKind.Json => Json,
            FieldKind.Table => Table,
            _ => File,
        };
    }

    private static FieldKind ParseKind(string type) => type switch
    {
        "number" => FieldKind.Number,
        "integer" => FieldKind.Integer,
        "decimal" => FieldKind.Decimal,
        "string" => FieldKind.String,
        "bool" => FieldKind.Bool,
        "date" => FieldKind.Date,
        "datetime" => FieldKind.DateTime,
        "enum" => FieldKind.Enum,
        "json" => FieldKind.Json,
        "table" => FieldKind.Table,
        "file" => FieldKind.File,
        "list" => FieldKind.List,
        _ => throw new ArgumentException($"Unknown field type '{type}'.", nameof(type)),
    };

    /// <inheritdoc />
    public bool Equals(FieldType? other) =>
        other is not null
        && Kind == other.Kind
        && Equals(ItemType, other.ItemType)
        && (EnumValues ?? []).SequenceEqual(other.EnumValues ?? [], StringComparer.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FieldType);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, ItemType, EnumValues?.Count ?? 0);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        FieldKind.List => $"list<{ItemType}>",
        FieldKind.Enum => $"enum({string.Join('|', EnumValues!)})",
        _ => WireName,
    };
}
