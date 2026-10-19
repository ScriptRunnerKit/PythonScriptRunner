namespace PythonScriptRunner;

/// <summary>Base class of PythonScriptRunner exceptions.</summary>
public abstract class PsrException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    protected PsrException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The stage does not exist in the catalog.</summary>
public sealed class StageNotFoundException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="stageKey">Requested key.</param>
    public StageNotFoundException(string stageKey)
        : base($"Stage '{stageKey}' was not found.")
    {
        StageKey = stageKey;
    }

    /// <summary>Requested key.</summary>
    public string StageKey { get; }
}

/// <summary>The stage exists but not in the requested version.</summary>
public sealed class VersionNotFoundException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="key">Stage or pipeline key.</param>
    /// <param name="version">Requested version.</param>
    public VersionNotFoundException(string key, int version)
        : base($"Version {version} of '{key}' was not found.")
    {
        Key = key;
        Version = version;
    }

    /// <summary>Stage or pipeline key.</summary>
    public string Key { get; }

    /// <summary>Requested version.</summary>
    public int Version { get; }
}

/// <summary>The version exists but cannot run (it is a draft or was archived).</summary>
public sealed class VersionNotRunnableException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="key">Stage or pipeline key.</param>
    /// <param name="version">Requested version.</param>
    /// <param name="status">Current status of the version.</param>
    public VersionNotRunnableException(string key, int version, VersionStatus status)
        : base($"Version {version} of '{key}' is {status} and cannot run; only published versions run.")
    {
        Key = key;
        Version = version;
        Status = status;
    }

    /// <summary>Stage or pipeline key.</summary>
    public string Key { get; }

    /// <summary>Requested version.</summary>
    public int Version { get; }

    /// <summary>Current status of the version.</summary>
    public VersionStatus Status { get; }
}

/// <summary>The run does not exist (or is beyond the retention).</summary>
public sealed class RunNotFoundException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="runId">Requested run.</param>
    public RunNotFoundException(RunId runId)
        : base($"Run {runId} was not found.")
    {
        RunId = runId;
    }

    /// <summary>Requested run.</summary>
    public RunId RunId { get; }
}

/// <summary>The stored file does not exist (or is beyond the retention).</summary>
public sealed class ArtifactNotFoundException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="artifactId">Requested file.</param>
    public ArtifactNotFoundException(ArtifactId artifactId)
        : base($"Artifact {artifactId} was not found.")
    {
        ArtifactId = artifactId;
    }

    /// <summary>Requested file.</summary>
    public ArtifactId ArtifactId { get; }
}

/// <summary>One invalid input.</summary>
/// <param name="Field">Field path (e.g. <c>coeficientes[2]</c>).</param>
/// <param name="Code">Stable code (e.g. <c>MISSING_REQUIRED_INPUT</c>).</param>
/// <param name="Message">Human-readable message.</param>
public sealed record InputError(string Field, string Code, string Message);

/// <summary>The inputs of a call do not match the contract; no run was created.</summary>
public sealed class InputValidationException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="errors">Every invalid input.</param>
    public InputValidationException(IReadOnlyList<InputError> errors)
        : base("Invalid inputs: " + string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}")))
    {
        Errors = errors;
    }

    /// <summary>Every invalid input.</summary>
    public IReadOnlyList<InputError> Errors { get; }
}

/// <summary>The runner, a catalog or a stage definition is misconfigured.</summary>
public sealed class PsrConfigurationException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">Cause.</param>
    public PsrConfigurationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown by <see cref="RunResult.EnsureSuccess"/> when the run did not succeed.</summary>
public sealed class RunFailedException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="result">The run result.</param>
    public RunFailedException(RunResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    /// <summary>The run result.</summary>
    public RunResult Result { get; }

    private static string BuildMessage(RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var error = result.Error;
        return error is null
            ? $"Run {result.Id} ended with status {result.Status}."
            : $"Run {result.Id} ended with status {result.Status}: [{error.Kind}{(error.Code is null ? "" : $"/{error.Code}")}] {error.Message}";
    }
}
