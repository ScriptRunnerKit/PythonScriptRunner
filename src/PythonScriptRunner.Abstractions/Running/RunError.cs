using System.Text.Json.Nodes;

namespace PythonScriptRunner;

/// <summary>Why a run, step or attempt failed.</summary>
/// <param name="Kind">Failure classification.</param>
/// <param name="Message">Human-readable message.</param>
public sealed record RunError(ErrorKind Kind, string Message)
{
    /// <summary>Stable code (e.g. a <c>StageError</c> code or <c>UNDECLARED_OUTPUT</c>).</summary>
    public string? Code { get; init; }

    /// <summary>Key of the step that failed.</summary>
    public string? StepKey { get; init; }

    /// <summary>Python traceback, when available.</summary>
    public string? Traceback { get; init; }

    /// <summary>Extra details given by the script (<c>StageError(details=...)</c>).</summary>
    public JsonNode? Details { get; init; }
}

/// <summary>The last bytes written to stdout or stderr by an attempt.</summary>
/// <param name="Content">Captured text (tail), with secret values masked.</param>
/// <param name="Truncated">Whether older output was discarded.</param>
/// <param name="OriginalBytes">Total number of bytes written by the process.</param>
public sealed record LogTail(string Content, bool Truncated, long OriginalBytes)
{
    /// <summary>No output.</summary>
    public static LogTail Empty { get; } = new(string.Empty, false, 0);
}
