using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PythonScriptRunner;

/// <summary>Declaration of one input or output of a stage.</summary>
/// <param name="Name">Field name (<c>^[a-z_][a-z0-9_]{0,63}$</c>).</param>
/// <param name="Type">Field type.</param>
[JsonConverter(typeof(FieldDefinitionJsonConverter))]
public sealed record FieldDefinition(string Name, FieldType Type)
{
    /// <summary>Whether a value must be provided (inputs) or produced (outputs). Default: <c>true</c>.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Default value in wire format; not allowed for files and tables.</summary>
    public JsonNode? Default { get; init; }

    /// <summary>Text shown in the panel and in generated forms (up to 500 chars).</summary>
    public string? Description { get; init; }

    /// <summary>Accepted MIME types for <c>file</c>/<c>list&lt;file&gt;</c> fields (optional).</summary>
    public IReadOnlyList<string>? ContentTypes { get; init; }

    /// <summary>Declared columns of a <c>table</c> field (optional).</summary>
    public IReadOnlyList<TableColumn>? Columns { get; init; }
}

/// <summary>A declared column of a <c>table</c> field.</summary>
/// <param name="Name">Column name.</param>
/// <param name="Type">Scalar type of the column.</param>
public sealed record TableColumn(string Name, FieldType Type);
