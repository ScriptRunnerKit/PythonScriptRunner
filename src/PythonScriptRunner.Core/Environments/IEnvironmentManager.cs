namespace PythonScriptRunner;

/// <summary>Creates and caches the Python environment of each stage (one venv per dependency set).</summary>
public interface IEnvironmentManager
{
    /// <summary>Resolves dependencies into a lock with hashes (<c>uv pip compile --universal --generate-hashes</c>).</summary>
    /// <param name="pythonVersion">Target Python version; <c>null</c> uses the runner default.</param>
    /// <param name="requirements">Requirements in requirements.txt format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The locked requirements (empty when there are no requirements).</returns>
    /// <exception cref="PythonEnvironmentException">Resolution failed.</exception>
    Task<string> LockAsync(string? pythonVersion, string? requirements, CancellationToken cancellationToken = default);

    /// <summary>Returns the environment of a stage, creating it on first use.</summary>
    /// <param name="stage">The stage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="PythonEnvironmentException">The environment could not be created.</exception>
    Task<PythonEnvironment> EnsureAsync(StageDefinition stage, CancellationToken cancellationToken = default);
}

/// <summary>A ready-to-use Python environment.</summary>
/// <param name="Hash">Hash of Python version + locked requirements + psrunner.</param>
/// <param name="Directory">Folder of the venv.</param>
/// <param name="PythonExecutable">Python interpreter of the venv.</param>
/// <param name="Created">Whether it was created by this call.</param>
public sealed record PythonEnvironment(string Hash, string Directory, string PythonExecutable, bool Created);

/// <summary>A Python environment could not be resolved or created.</summary>
public sealed class PythonEnvironmentException : PsrException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed (includes the uv output).</param>
    /// <param name="innerException">Cause.</param>
    public PythonEnvironmentException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
