using System.Globalization;
using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Fluent definition of a pipeline in C#.</summary>
public sealed class PipelineBuilder
{
    private readonly string _key;
    private readonly List<FieldDefinition> _inputs = [];
    private readonly List<PipelineStepDefinition> _steps = [];
    private readonly List<PipelineOutputDefinition> _outputs = [];
    private int _version = 1;
    private string? _name;
    private string? _description;

    internal PipelineBuilder(string key) => _key = key;

    /// <summary>Version number. Default: 1.</summary>
    /// <param name="version">Version (≥ 1).</param>
    public PipelineBuilder Version(int version)
    {
        _version = version;
        return this;
    }

    /// <summary>Display name. Default: the key.</summary>
    /// <param name="name">Name.</param>
    public PipelineBuilder Name(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>Description.</summary>
    /// <param name="description">Text.</param>
    public PipelineBuilder Description(string description)
    {
        _description = description;
        return this;
    }

    /// <summary>Declares a pipeline input.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="type">Field type.</param>
    /// <param name="required">Whether the input is required.</param>
    /// <param name="default">Default value (wire format).</param>
    /// <param name="description">Description.</param>
    public PipelineBuilder Input(string name, FieldType type, bool required = true, JsonNode? @default = null, string? description = null) =>
        Input(new FieldDefinition(name, type) { Required = required, Default = @default, Description = description });

    /// <summary>Declares a pipeline input with every option.</summary>
    /// <param name="field">Field declaration.</param>
    public PipelineBuilder Input(FieldDefinition field)
    {
        _inputs.Add(field);
        return this;
    }

    /// <summary>Adds a step that runs <c>stage@version</c> (e.g. <c>"html-to-pdf@1"</c>).</summary>
    /// <param name="key">Step key (slug, unique in the pipeline).</param>
    /// <param name="stage">Stage reference <c>key@version</c>.</param>
    /// <param name="configure">Bindings and display name; unbound inputs use the implicit binding by name.</param>
    public PipelineBuilder Step(string key, string stage, Action<PipelineStepBuilder>? configure = null)
    {
        var (stageKey, stageVersion) = ParseStageReference(stage);
        return Step(key, stageKey, stageVersion, configure);
    }

    /// <summary>Adds a step that runs a stage version.</summary>
    /// <param name="key">Step key (slug, unique in the pipeline).</param>
    /// <param name="stageKey">Stage key.</param>
    /// <param name="stageVersion">Stage version.</param>
    /// <param name="configure">Bindings and display name.</param>
    public PipelineBuilder Step(string key, string stageKey, int stageVersion, Action<PipelineStepBuilder>? configure = null)
    {
        var step = new PipelineStepBuilder();
        configure?.Invoke(step);
        _steps.Add(new PipelineStepDefinition(key, stageKey, stageVersion) { DisplayName = step.DisplayNameValue, Bindings = [.. step.Bindings] });
        return this;
    }

    /// <summary>Declares a pipeline output taken from a step output.</summary>
    /// <param name="name">Output name.</param>
    /// <param name="source">Step output (e.g. <c>From.Step("stats", "sum")</c>).</param>
    /// <param name="description">Description.</param>
    public PipelineBuilder Output(string name, StepOutputSource source, string? description = null)
    {
        _outputs.Add(new PipelineOutputDefinition(name, source) { Description = description });
        return this;
    }

    internal PipelineDefinition Build() => new()
    {
        Key = _key,
        Version = _version,
        Name = _name ?? _key,
        Description = _description,
        Inputs = [.. _inputs],
        Steps = [.. _steps],
        Outputs = [.. _outputs],
    };

    /// <summary>Parses <c>key@version</c>.</summary>
    internal static (string Key, int Version) ParseStageReference(string reference)
    {
        var separator = reference.LastIndexOf('@');
        if (separator <= 0 || !int.TryParse(reference.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new PsrConfigurationException($"Invalid stage reference '{reference}'; use key@version (e.g. html-to-pdf@1).");
        }

        return (reference[..separator], version);
    }
}

/// <summary>Bindings of one pipeline step.</summary>
public sealed class PipelineStepBuilder
{
    internal PipelineStepBuilder()
    {
    }

    internal List<InputBinding> Bindings { get; } = [];

    internal string? DisplayNameValue { get; private set; }

    /// <summary>Binds an input of the stage to a source.</summary>
    /// <param name="input">Stage input name.</param>
    /// <param name="source">Origin: <see cref="From.Pipeline"/>, <see cref="From.Step"/> or <see cref="From.Constant"/>.</param>
    public PipelineStepBuilder Bind(string input, BindingSource source)
    {
        Bindings.Add(new InputBinding(input, source));
        return this;
    }

    /// <summary>Label shown in the panel.</summary>
    /// <param name="displayName">Label.</param>
    public PipelineStepBuilder DisplayName(string displayName)
    {
        DisplayNameValue = displayName;
        return this;
    }
}
