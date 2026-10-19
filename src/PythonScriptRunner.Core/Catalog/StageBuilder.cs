using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Fluent definition of a stage in C#.</summary>
public sealed class StageBuilder
{
    private readonly string _key;
    private readonly List<FieldDefinition> _inputs = [];
    private readonly List<FieldDefinition> _outputs = [];
    private readonly List<string> _secrets = [];
    private int _version = 1;
    private string? _name;
    private string? _description;
    private ScriptSource? _script;
    private string? _pythonVersion;
    private string? _requirements;
    private TimeSpan? _timeout;
    private int? _memoryLimitMb;
    private RetryPolicy _retry = RetryPolicy.None;
    private bool _idempotent;

    internal StageBuilder(string key) => _key = key;

    /// <summary>Version number. Default: 1.</summary>
    /// <param name="version">Version (≥ 1).</param>
    public StageBuilder Version(int version)
    {
        _version = version;
        return this;
    }

    /// <summary>Display name. Default: the key.</summary>
    /// <param name="name">Name.</param>
    public StageBuilder Name(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>Description.</summary>
    /// <param name="description">Text.</param>
    public StageBuilder Description(string description)
    {
        _description = description;
        return this;
    }

    /// <summary>Python source code of the stage (must declare one <c>@stage</c> function).</summary>
    /// <param name="content">Source code.</param>
    public StageBuilder Script(string content)
    {
        _script = ScriptSource.Inline(content);
        return this;
    }

    /// <summary>Path of the <c>.py</c> file of the stage.</summary>
    /// <param name="path">Script path.</param>
    public StageBuilder ScriptFile(string path)
    {
        _script = ScriptSource.FromFile(path);
        return this;
    }

    /// <summary>Python version (minimum 3.10). Default: runner default (3.12).</summary>
    /// <param name="version">Version such as <c>3.12</c>.</param>
    public StageBuilder Python(string version)
    {
        _pythonVersion = version;
        return this;
    }

    /// <summary>Dependencies in requirements.txt format.</summary>
    /// <param name="requirements">Requirements text.</param>
    public StageBuilder Requirements(string requirements)
    {
        _requirements = requirements;
        return this;
    }

    /// <summary>Declares an input.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="type">Field type.</param>
    /// <param name="required">Whether the input is required.</param>
    /// <param name="default">Default value (wire format).</param>
    /// <param name="description">Description.</param>
    public StageBuilder Input(string name, FieldType type, bool required = true, JsonNode? @default = null, string? description = null) =>
        Input(new FieldDefinition(name, type) { Required = required, Default = @default, Description = description });

    /// <summary>Declares an input with every option.</summary>
    /// <param name="field">Field declaration.</param>
    public StageBuilder Input(FieldDefinition field)
    {
        _inputs.Add(field);
        return this;
    }

    /// <summary>Declares an output.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="type">Field type.</param>
    /// <param name="required">Whether the script must produce it.</param>
    /// <param name="description">Description.</param>
    public StageBuilder Output(string name, FieldType type, bool required = true, string? description = null) =>
        Output(new FieldDefinition(name, type) { Required = required, Description = description });

    /// <summary>Declares an output with every option.</summary>
    /// <param name="field">Field declaration.</param>
    public StageBuilder Output(FieldDefinition field)
    {
        _outputs.Add(field);
        return this;
    }

    /// <summary>Declares a secret the script needs (resolved by <see cref="ISecretProvider"/>).</summary>
    /// <param name="name">Secret name in UPPER_SNAKE_CASE.</param>
    public StageBuilder Secret(string name)
    {
        _secrets.Add(name);
        return this;
    }

    /// <summary>Timeout of each attempt. Default: runner default (5 minutes).</summary>
    /// <param name="timeout">Timeout.</param>
    public StageBuilder Timeout(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    /// <summary>Memory limit of the Python process. Default: runner default (1024 MB).</summary>
    /// <param name="megabytes">Limit in MB (≥ 64).</param>
    public StageBuilder MemoryLimitMb(int megabytes)
    {
        _memoryLimitMb = megabytes;
        return this;
    }

    /// <summary>Automatic retries after a failure.</summary>
    /// <param name="maxAttempts">Retries after the first attempt.</param>
    /// <param name="initialDelay">Delay before the first retry. Default: 10 s.</param>
    /// <param name="backoffMultiplier">Delay multiplier per retry. Default: 2.</param>
    public StageBuilder Retry(int maxAttempts, TimeSpan? initialDelay = null, double backoffMultiplier = 2.0)
    {
        _retry = new RetryPolicy(maxAttempts, initialDelay ?? TimeSpan.FromSeconds(10), backoffMultiplier);
        return this;
    }

    /// <summary>Marks the stage as safe to run twice with the same inputs.</summary>
    /// <param name="idempotent">Whether the stage is idempotent.</param>
    public StageBuilder Idempotent(bool idempotent = true)
    {
        _idempotent = idempotent;
        return this;
    }

    internal StageDefinition Build() => new()
    {
        Key = _key,
        Version = _version,
        Name = _name ?? _key,
        Description = _description,
        Script = _script ?? throw new PsrConfigurationException($"Stage '{_key}' has no script."),
        PythonVersion = _pythonVersion,
        Requirements = _requirements,
        Inputs = [.. _inputs],
        Outputs = [.. _outputs],
        RequiredSecrets = [.. _secrets],
        Timeout = _timeout,
        MemoryLimitMb = _memoryLimitMb,
        Retry = _retry,
        Idempotent = _idempotent,
    };
}
