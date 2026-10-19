using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Immutable definition of one version of a pipeline: inputs, ordered steps and declared outputs.</summary>
public sealed record PipelineDefinition
{
    /// <summary>Unique slug of the pipeline (e.g. <c>html-to-email</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Version number (1, 2, ...).</summary>
    public required int Version { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Inputs received by the pipeline.</summary>
    public IReadOnlyList<FieldDefinition> Inputs { get; init; } = [];

    /// <summary>Steps, executed in this order (linear).</summary>
    public required IReadOnlyList<PipelineStepDefinition> Steps { get; init; }

    /// <summary>Declared outputs of the pipeline (may be empty).</summary>
    public IReadOnlyList<PipelineOutputDefinition> Outputs { get; init; } = [];
}

/// <summary>One step of a pipeline: a fixed stage version and the bindings of its inputs.</summary>
/// <param name="Key">Step key (slug, unique in the pipeline).</param>
/// <param name="StageKey">Key of the executed stage.</param>
/// <param name="StageVersion">Exact version of the executed stage.</param>
public sealed record PipelineStepDefinition(string Key, string StageKey, int StageVersion)
{
    /// <summary>Label shown in the panel.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Explicit bindings; unbound inputs use the implicit binding by name.</summary>
    public IReadOnlyList<InputBinding> Bindings { get; init; } = [];
}

/// <summary>Where one input of a step comes from.</summary>
/// <param name="Input">Input name of the stage.</param>
/// <param name="Source">Origin of the value.</param>
public sealed record InputBinding(string Input, BindingSource Source);

/// <summary>A declared output of the pipeline, taken from an output of a step.</summary>
/// <param name="Name">Output name.</param>
/// <param name="Source">Step output that provides the value (its type is inherited).</param>
public sealed record PipelineOutputDefinition(string Name, StepOutputSource Source)
{
    /// <summary>Description shown in the panel.</summary>
    public string? Description { get; init; }
}

/// <summary>Origin of a bound value.</summary>
public abstract record BindingSource
{
    private protected BindingSource()
    {
    }
}

/// <summary>An input of the pipeline (<c>$pipeline.&lt;field&gt;</c>).</summary>
/// <param name="Field">Pipeline input name.</param>
public sealed record PipelineInputSource(string Field) : BindingSource
{
    /// <inheritdoc />
    public override string ToString() => $"$pipeline.{Field}";
}

/// <summary>An output of an earlier step (<c>$steps.&lt;step&gt;.&lt;field&gt;</c>).</summary>
/// <param name="Step">Step key.</param>
/// <param name="Field">Output name of that step.</param>
public sealed record StepOutputSource(string Step, string Field) : BindingSource
{
    /// <inheritdoc />
    public override string ToString() => $"$steps.{Step}.{Field}";
}

/// <summary>A fixed value in wire format (<c>{"const": ...}</c>); files are not allowed.</summary>
/// <param name="Value">The value.</param>
public sealed record ConstantSource(JsonNode? Value) : BindingSource
{
    /// <inheritdoc />
    public override string ToString() => $"const {Value?.ToJsonString() ?? "null"}";
}

/// <summary>Shortcuts to create binding sources.</summary>
public static class From
{
    /// <summary>An input of the pipeline.</summary>
    /// <param name="field">Pipeline input name.</param>
    public static PipelineInputSource Pipeline(string field) => new(field);

    /// <summary>An output of an earlier step.</summary>
    /// <param name="step">Step key.</param>
    /// <param name="field">Output name.</param>
    public static StepOutputSource Step(string step, string field) => new(step, field);

    /// <summary>A fixed value in wire format.</summary>
    /// <param name="value">The value (e.g. <c>465</c> or <c>"no-reply@example.com"</c>).</param>
    public static ConstantSource Constant(JsonNode? value) => new(value);
}
