namespace PythonScriptRunner;

/// <summary>Settings of the Python runner.</summary>
public sealed class PythonScriptRunnerOptions
{
    /// <summary>Folder for work and result directories. Default: <c>%TEMP%/psr</c>.</summary>
    public string WorkRoot { get; set; } = Path.Combine(Path.GetTempPath(), "psr");

    /// <summary>Folder where Python environments (venvs) and lock files are cached.</summary>
    /// <remarks>The default only names the folder: it is created when the first environment needs it (a container with a
    /// read-only file system sets another root and must not fail on the default).</remarks>
    public string EnvironmentCacheRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "psr");

    /// <summary>Path or name of the <c>uv</c> executable. Default: <c>uv</c> (searched in PATH).</summary>
    public string UvPath { get; set; } = "uv";

    /// <summary>Python version of stages that do not declare one. Default: 3.12.</summary>
    public string DefaultPythonVersion { get; set; } = "3.12";

    /// <summary>Timeout of stages that do not declare one. Default: 5 minutes.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Memory limit (MB) of stages that do not declare one. Default: 1024.</summary>
    public int DefaultMemoryLimitMb { get; set; } = 1024;

    /// <summary>Maximum size of one file. Default: 50 MB.</summary>
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Maximum size of all files of one run. Default: 200 MB.</summary>
    public long MaxRunFilesBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Bytes of stdout/stderr kept per attempt (the tail). Default: 1 MB.</summary>
    public int MaxLogBytes { get; set; } = 1024 * 1024;

    /// <summary>Maximum number of Python processes running at the same time. Default: processor count.</summary>
    public int MaxConcurrentProcesses { get; set; } = Environment.ProcessorCount;

    /// <summary>Package index (pip <c>--index-url</c>). Default: PyPI.</summary>
    public string? PackageIndexUrl { get; set; }

    /// <summary>Extra package indexes (pip <c>--extra-index-url</c>).</summary>
    public IList<string> ExtraIndexUrls { get; } = [];

    /// <summary>
    /// The <c>psrunner</c> SDK installed in every environment: a pip requirement
    /// (default <c>psrunner==0.1.0</c>) or the path of a local wheel.
    /// </summary>
    public string PsrunnerRequirement { get; set; } = "psrunner==0.1.0";

    /// <summary>Age after which leftover work and result folders are deleted. Default: 24 hours.</summary>
    public TimeSpan OrphanRetention { get; set; } = TimeSpan.FromHours(24);
}
