using System.Text.Json;
using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Outcome of one attempt (one Python process).</summary>
public sealed record AttemptResult
{
    /// <summary>Attempt identifier.</summary>
    public required AttemptId Id { get; init; }

    /// <summary>1-based attempt number.</summary>
    public required int Number { get; init; }

    /// <summary>Why the attempt was started.</summary>
    public required AttemptCause Cause { get; init; }

    /// <summary>Final status.</summary>
    public required AttemptStatus Status { get; init; }

    /// <summary>When the attempt started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Total duration of the attempt.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Time spent by the runner outside the Python process.</summary>
    public required TimeSpan Overhead { get; init; }

    /// <summary>Exit code of the process (<c>null</c> when it did not start).</summary>
    public int? ExitCode { get; init; }

    /// <summary>Failure, when the attempt did not succeed.</summary>
    public RunError? Error { get; init; }

    /// <summary>Tail of stdout.</summary>
    public LogTail Stdout { get; init; } = LogTail.Empty;

    /// <summary>Tail of stderr.</summary>
    public LogTail Stderr { get; init; } = LogTail.Empty;

    /// <summary>Hash of the Python environment used.</summary>
    public string? EnvironmentHash { get; init; }
}

/// <summary>Outcome of one step (one stage execution, possibly with retries).</summary>
public sealed record StepResult
{
    /// <summary>Key of the step (the stage key for single-stage runs).</summary>
    public required string StepKey { get; init; }

    /// <summary>1-based position in the run.</summary>
    public required int Order { get; init; }

    /// <summary>Key of the executed stage.</summary>
    public required string StageKey { get; init; }

    /// <summary>Exact version of the executed stage.</summary>
    public required int StageVersion { get; init; }

    /// <summary>Final status.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>Every attempt, in order.</summary>
    public required IReadOnlyList<AttemptResult> Attempts { get; init; }

    /// <summary>Validated outputs in wire format (file values carry metadata only).</summary>
    public JsonObject? Outputs { get; init; }

    /// <summary>Output files by field name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> Files { get; init; } =
        new Dictionary<string, IReadOnlyList<PsrFile>>();

    /// <summary>Failure of the last attempt.</summary>
    public RunError? Error { get; init; }
}

/// <summary>Result of a synchronous run. Dispose it to delete the local output files.</summary>
public sealed class RunResult : IAsyncDisposable
{
    private readonly JsonObject _outputs;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> _files;
    private readonly Func<ValueTask>? _cleanup;
    private int _disposed;

    /// <summary>Creates a result (used by runner implementations).</summary>
    /// <param name="id">Run identifier.</param>
    /// <param name="kind">What was executed.</param>
    /// <param name="status">Final status.</param>
    /// <param name="duration">Total duration.</param>
    /// <param name="steps">Step results, in order.</param>
    /// <param name="outputs">Declared outputs of the run, in wire format.</param>
    /// <param name="files">Output files of the run by field name.</param>
    /// <param name="error">Failure, when the run did not succeed.</param>
    /// <param name="cleanup">Deletes local files when the result is disposed.</param>
    /// <param name="deduplicated">Whether this is an earlier run returned for the same idempotency key.</param>
    public RunResult(
        RunId id,
        RunKind kind,
        RunStatus status,
        TimeSpan duration,
        IReadOnlyList<StepResult> steps,
        JsonObject? outputs,
        IReadOnlyDictionary<string, IReadOnlyList<PsrFile>>? files,
        RunError? error,
        Func<ValueTask>? cleanup = null,
        bool deduplicated = false)
    {
        Id = id;
        Kind = kind;
        Status = status;
        Duration = duration;
        Steps = steps;
        _outputs = outputs ?? [];
        _files = files ?? new Dictionary<string, IReadOnlyList<PsrFile>>();
        Error = error;
        _cleanup = cleanup;
        Deduplicated = deduplicated;
    }

    /// <summary>Run identifier.</summary>
    public RunId Id { get; }

    /// <summary>What was executed.</summary>
    public RunKind Kind { get; }

    /// <summary>
    /// Final status: Succeeded, Failed or Cancelled. A deduplicated result reports the current status of the earlier run,
    /// which may still be running.
    /// </summary>
    public RunStatus Status { get; }

    /// <summary>Whether this is an earlier run returned because the same idempotency key was used within 24 hours.</summary>
    public bool Deduplicated { get; }

    /// <summary>Output files of the run by declared output name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> Files => _files;

    /// <summary>Failure, when the run did not succeed.</summary>
    public RunError? Error { get; }

    /// <summary>Total duration.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Step results, in order.</summary>
    public IReadOnlyList<StepResult> Steps { get; }

    /// <summary>Declared outputs in wire format.</summary>
    public JsonObject Outputs => _outputs;

    /// <summary>Reads a declared output.</summary>
    /// <typeparam name="T">Target .NET type (see the type mapping of the protocol).</typeparam>
    /// <param name="name">Output name.</param>
    /// <exception cref="KeyNotFoundException">The output was not produced.</exception>
    /// <exception cref="InvalidCastException">The value cannot be converted to <typeparamref name="T"/>.</exception>
    public T GetOutput<T>(string name) => ReadValue<T>(_outputs, _files, name, scope: null);

    /// <summary>Tries to read a declared output.</summary>
    /// <typeparam name="T">Target .NET type.</typeparam>
    /// <param name="name">Output name.</param>
    /// <param name="value">The value, when produced.</param>
    public bool TryGetOutput<T>(string name, out T? value)
    {
        if (!_outputs.ContainsKey(name))
        {
            value = default;
            return false;
        }

        value = GetOutput<T>(name);
        return true;
    }

    /// <summary>Reads a <c>file</c> output.</summary>
    /// <param name="name">Output name.</param>
    public PsrFile GetFile(string name)
    {
        var files = GetFiles(name);
        return files.Count == 1
            ? files[0]
            : throw new InvalidCastException($"Output '{name}' contains {files.Count} files; use GetFiles.");
    }

    /// <summary>Reads a <c>file</c> or <c>list&lt;file&gt;</c> output.</summary>
    /// <param name="name">Output name.</param>
    public IReadOnlyList<PsrFile> GetFiles(string name) =>
        _files.TryGetValue(name, out var files)
            ? files
            : throw new KeyNotFoundException($"Output '{name}' is not a file output or was not produced.");

    /// <summary>Reads an output of a specific step (useful to debug pipelines).</summary>
    /// <typeparam name="T">Target .NET type.</typeparam>
    /// <param name="stepKey">Step key.</param>
    /// <param name="name">Output name.</param>
    public T GetStepOutput<T>(string stepKey, string name)
    {
        var step = Steps.FirstOrDefault(s => s.StepKey == stepKey)
            ?? throw new KeyNotFoundException($"Step '{stepKey}' is not part of this run.");
        return ReadValue<T>(step.Outputs ?? [], step.Files, name, stepKey);
    }

    /// <summary>Throws <see cref="RunFailedException"/> unless the run succeeded.</summary>
    public void EnsureSuccess()
    {
        if (Status != RunStatus.Succeeded)
        {
            throw new RunFailedException(this);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _cleanup is not null)
        {
            await _cleanup().ConfigureAwait(false);
        }
    }

    private static T ReadValue<T>(
        JsonObject outputs, IReadOnlyDictionary<string, IReadOnlyList<PsrFile>> files, string name, string? scope)
    {
        var where = scope is null ? $"Output '{name}'" : $"Output '{name}' of step '{scope}'";
        if (!outputs.TryGetPropertyValue(name, out var node))
        {
            throw new KeyNotFoundException($"{where} was not produced or is not declared.");
        }

        if (typeof(T) == typeof(PsrFile) || typeof(IEnumerable<PsrFile>).IsAssignableFrom(typeof(T)))
        {
            if (!files.TryGetValue(name, out var list))
            {
                throw new InvalidCastException($"{where} is not a file output.");
            }

            object value = typeof(T) == typeof(PsrFile) ? list.Single() : list;
            return (T)value;
        }

        if (node is null)
        {
            return default!;
        }

        try
        {
            return node.Deserialize<T>(PsrJson.Options)!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
        {
            throw new InvalidCastException($"{where} cannot be read as {typeof(T).Name}: {ex.Message}", ex);
        }
    }
}
